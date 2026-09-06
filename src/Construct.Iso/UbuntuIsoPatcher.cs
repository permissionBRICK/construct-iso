using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Construct.Iso;

/// <summary>
/// Deliberately restricted to Ubuntu's GRUB/GPT hybrid live-server layout. File contents and
/// existing directory locations remain fixed. New payload goes immediately before the appended
/// EFI partition; all four filesystem views, GPT copies, MBR and El Torito are updated together.
/// Seed files live in the existing /boot/grub directory, avoiding directory/path-table relocation.
/// </summary>
public static class UbuntuIsoPatcher
{
    private const int Block = 2048;
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
    private static void Both(byte[] b, int o, uint v)
    {
        W32(b, o, v);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o + 4), v);
    }
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException("Unsupported or damaged Ubuntu ISO: " + reason);
    }
    private static byte[] Read(Stream stream, long offset, int count)
    {
        Require(offset >= 0 && count >= 0 && offset <= stream.Length - count, "extent outside image");
        var bytes = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }
    private static int Padded(int length) => checked((length + Block - 1) / Block * Block);

    public static async Task BuildAsync(string source, string output, AutoinstallSeed seed,
        Action<string>? progress = null, CancellationToken cancellationToken = default, bool overwrite = false)
    {
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        if (string.Equals(source, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Source and output must be different files.");
        if (File.Exists(output) && !overwrite) throw new IOException("Output already exists; choose a new output path.");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("Checking Ubuntu hybrid ISO layout…");
        var plan = Plan(input, seed);
        var scratch = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var target = new FileStream(scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous))
            {
                progress?.Invoke("Copying installer and inserting autoinstall payload…");
                input.Position = 0;
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                var lastReport = DateTime.UtcNow;
                while (copied < input.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (copied == plan.Split) await target.WriteAsync(plan.Payload, cancellationToken);
                    var stop = copied < plan.Split ? plan.Split : input.Length;
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, stop - copied)), cancellationToken);
                    if (count == 0) throw new EndOfStreamException("Source ISO changed during copy.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    copied += count;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(10))
                    {
                        progress?.Invoke($"Copied {copied * 100 / input.Length}%");
                        lastReport = DateTime.UtcNow;
                    }
                }
                foreach (var (offset, data) in plan.Patches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Position = offset;
                    await target.WriteAsync(data, cancellationToken);
                }
                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);
                Require(target.Length == input.Length + plan.Payload.Length, "output size mismatch");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(scratch, output, overwrite); // Publish only a fully written image, replacing atomically if requested.
            progress?.Invoke($"Autoinstall ISO ready: {output}");
        }
        finally
        {
            if (File.Exists(scratch)) File.Delete(scratch);
        }
    }

    private sealed record BuildPlan(long Split, byte[] Payload, Dictionary<long, byte[]> Patches);
    private sealed record View(uint Base, long Offset, byte[] Descriptor, bool Joliet);
    private sealed record Entry(int Offset, byte[] Record, string Name);
    private sealed record DirectoryData(long Offset, byte[] Bytes, List<Entry> Entries);

    private static BuildPlan Plan(Stream input, AutoinstallSeed seed)
    {
        Require(input.Length % Block == 0, "image is not sector aligned");
        var mbr = Read(input, 0, 512);
        Require(mbr[510] == 0x55 && mbr[511] == 0xaa, "missing MBR");
        var gpt = Read(input, 512, 512);
        ValidateGpt(gpt);
        Require(U64(gpt, 24) == 1 && U64(gpt, 32) == (ulong)(input.Length / 512 - 1), "unexpected GPT geometry");
        var tableLba = U64(gpt, 72);
        var entryCount = U32(gpt, 80);
        var entrySize = U32(gpt, 84);
        Require(entryCount == 248 && entrySize == 128 && tableLba == 2, "expected Ubuntu's 248-entry GPT");
        var table = Read(input, checked((long)tableLba * 512), checked((int)(entryCount * entrySize)));
        Require(Crc32(table) == U32(gpt, 88), "GPT partition checksum mismatch");
        var backup = Read(input, checked((long)U64(gpt, 32) * 512), 512);
        ValidateGpt(backup);
        Require(U64(backup, 32) == 1 && U64(backup, 24) == U64(gpt, 32) &&
            U64(gpt, 40) == 64 && U64(backup, 40) == U64(gpt, 40) && U64(backup, 48) == U64(gpt, 48) &&
            backup.AsSpan(56, 16).SequenceEqual(gpt.AsSpan(56, 16)) &&
            U32(backup, 80) == entryCount && U32(backup, 84) == entrySize && U32(backup, 88) == U32(gpt, 88), "GPT copies disagree");
        var backupTableOffset = checked((long)U64(backup, 72) * 512);
        Require(Read(input, backupTableOffset, table.Length).SequenceEqual(table), "GPT partition tables disagree");
        // Ubuntu live-server: ISO partition, EFI System Partition, 300 KiB padding partition.
        Require(new Guid(table.AsSpan(128, 16)) == new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b"), "second partition is not EFI");
        var split512 = U64(table, 128 + 32);
        Require(split512 % 4 == 0 && U64(table, 32) == 64 && U64(table, 40) + 1 == split512, "unexpected ISO/EFI boundary");
        Require(U64(table, 128 + 40) + 1 == U64(table, 256 + 32) && U64(table, 256 + 40) + 1 == U64(gpt, 48), "unexpected EFI/padding boundary");
        Require(table.AsSpan(384).IndexOfAnyExcept((byte)0) < 0, "additional GPT partitions");
        var split = checked((long)split512 * 512);
        Require(split > 40 * Block && split < backupTableOffset, "invalid payload boundary");
        var splitBlock = checked((uint)(split / Block));

        var views = new List<View>();
        foreach (uint baseBlock in new uint[] { 0, 16 })
        {
            var terminated = false;
            for (var i = 16; i < 32; i++)
            {
                var offset = ((long)baseBlock + i) * Block;
                var d = Read(input, offset, Block);
                Require(Encoding.ASCII.GetString(d, 1, 5) == "CD001" && d[6] == 1, "invalid volume descriptor");
                if (d[0] == 255) { terminated = true; break; }
                if (d[0] is 1 or 2)
                {
                    Require(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(128)) == Block &&
                        BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(130)) == Block, "non-2048-byte filesystem");
                    Require(U32(d, 80) == BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(84)), "volume size endian mismatch");
                    if (d[0] == 2) Require(d[88] == '%' && d[89] == '/' && d[90] is 0x40 or 0x43 or 0x45, "non-Joliet secondary view");
                    views.Add(new(baseBlock, offset, d, d[0] == 2));
                }
                else Require(d[0] == 0 && baseBlock == 0, "unexpected volume descriptor type");
            }
            Require(terminated, "unterminated descriptor set");
        }
        Require(views.Count == 4 && views.Count(v => v.Joliet) == 2, "expected primary and Joliet views at offsets 0 and 16");
        Require(!views[0].Joliet && views[1].Joliet && !views[2].Joliet && views[3].Joliet &&
            U32(views[0].Descriptor, 80) == input.Length / Block && U32(views[1].Descriptor, 80) == splitBlock,
            "unexpected whole-disc filesystem sizes");
        Require(Encoding.ASCII.GetString(views[0].Descriptor, 40, 32).StartsWith("Ubuntu-Server ", StringComparison.Ordinal), "volume is not Ubuntu Server");
        Require(views.Where(v => v.Base == 16).All(v => U32(v.Descriptor, 80) + v.Base == splitBlock), "ISO partition does not end at EFI boundary");

        var patches = new Dictionary<long, byte[]>();
        var dirs = views.Select(v => FindDirectory(input, v, "boot", "grub")).ToArray();
        var originalGrub = ReadFile(input, views[0], dirs[0], "grub.cfg");
        Require(Encoding.UTF8.GetString(originalGrub).Contains("/casper/vmlinuz", StringComparison.Ordinal), "missing Ubuntu GRUB kernel entry");
        Require(dirs.All(d => d.Entries.All(e => e.Name is not ("user-data" or "meta-data"))), "seed already exists");
        foreach (var (v, dir) in views.Zip(dirs))
            Require(ReadFile(input, v, dir, "grub.cfg").SequenceEqual(originalGrub), "GRUB differs between filesystem views");
        var grubText = Encoding.UTF8.GetString(originalGrub);
        Require(!grubText.Contains("Autoinstall The Construct", StringComparison.Ordinal), "image is already patched");
        var grub = Encoding.UTF8.GetBytes(PatchGrub(grubText));

        // Reject extents crossing the insertion boundary, including hidden continuation areas.
        foreach (var v in views) ValidateTree(input, v, split);

        var bootDescriptor = Read(input, 17 * Block, Block);
        Require(bootDescriptor[0] == 0 && Encoding.ASCII.GetString(bootDescriptor, 7, 23) == "EL TORITO SPECIFICATION", "missing El Torito descriptor");
        var catalogOffset = checked((long)U32(bootDescriptor, 71) * Block);
        Require(catalogOffset < split, "boot catalog outside ISO partition");
        var catalog = Read(input, catalogOffset, Block);
        Require(catalog[0] == 1 && catalog[1] == 0 && catalog[30] == 0x55 && catalog[31] == 0xaa &&
            Enumerable.Range(0, 16).Sum(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(i * 2))) % 65536 == 0,
            "invalid BIOS catalog validation entry");
        Require(catalog[32] == 0x88 && catalog[33] == 0 && U32(catalog, 40) < splitBlock,
            "unsupported BIOS boot entry");
        Require(catalog[64] == 0x91 && catalog[65] == 0xef && catalog[66] == 1 && catalog[67] == 0 &&
            catalog[96] == 0x88 && catalog[97] == 0 && U32(catalog, 104) == splitBlock &&
            BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(102)) == U64(table, 128 + 40) - split512 + 1 &&
            catalog.AsSpan(128).IndexOfAnyExcept((byte)0) < 0, "expected exactly one BIOS and one appended EFI boot entry");

        using var payload = new MemoryStream();
        (uint Lba, int Size) Add(byte[] bytes)
        {
            var lba = checked(splitBlock + (uint)(payload.Length / Block));
            payload.Write(bytes);
            payload.Write(new byte[Padded(bytes.Length) - bytes.Length]);
            return (lba, bytes.Length);
        }
        var files = new Dictionary<string, (uint Lba, int Size)>
        {
            ["grub.cfg"] = Add(grub), ["user-data"] = Add(Encoding.UTF8.GetBytes(seed.UserData)),
            ["meta-data"] = Add(Encoding.UTF8.GetBytes(seed.MetaData))
        };
        var rootDirs = views.Select(v => FindDirectory(input, v)).ToArray();
        var checksums = ReadFile(input, views[0], rootDirs[0], "md5sum.txt");
        foreach (var (v, root) in views.Zip(rootDirs))
            Require(ReadFile(input, v, root, "md5sum.txt").SequenceEqual(checksums), "checksum files differ between views");
        var checksumText = Encoding.UTF8.GetString(checksums);
        Require(!Regex.IsMatch(checksumText, @"[ *](?:\./)?(?:md5sum\.txt|boot\.catalog)\r?$", RegexOptions.Multiline), "self-referential or boot catalog checksum");
        var replaced = 0;
        checksumText = Regex.Replace(checksumText, @"^[0-9a-fA-F]{32}( [ *](?:\./)?boot/grub/grub\.cfg)\r?$", m =>
        { replaced++; return Convert.ToHexStringLower(MD5.HashData(grub)) + m.Groups[1].Value; }, RegexOptions.Multiline);
        Require(replaced == 1, "expected one GRUB checksum");
        foreach (var (name, text) in new[] { ("user-data", seed.UserData), ("meta-data", seed.MetaData) })
            checksumText = checksumText.TrimEnd('\r', '\n') + "\n" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text))) + $"  ./boot/grub/{name}\n";
        var checksumFile = Add(Encoding.UTF8.GetBytes(checksumText));
        var delta = checked((uint)(payload.Length / Block));
        var delta512 = checked((ulong)delta * 4);

        foreach (var (v, dir) in views.Zip(dirs))
        {
            foreach (var e in dir.Entries.Where(e => e.Name == "grub.cfg")) SetExtent(e.Record, files["grub.cfg"], v.Base);
            foreach (var name in new[] { "meta-data", "user-data" })
                dir.Entries.Add(new(0, NewFileRecord(name, files[name], v, dir.Entries.Single(e => e.Name == "grub.cfg").Record), name));
            patches.Add(dir.Offset, PackDirectory(dir));
        }
        foreach (var (v, root) in views.Zip(rootDirs))
        {
            SetExtent(root.Entries.Single(e => e.Name == "md5sum.txt").Record, checksumFile, v.Base);
            patches.Add(root.Offset, PackDirectory(root));
            Both(v.Descriptor, 80, checked(U32(v.Descriptor, 80) + delta));
            patches.Add(v.Offset, v.Descriptor);
        }
        W32(catalog, 104, checked(splitBlock + delta));
        patches.Add(catalogOffset, catalog);

        W64(table, 40, checked(U64(table, 40) + delta512));
        for (var p = 1; p <= 2; p++)
        {
            W64(table, p * 128 + 32, checked(U64(table, p * 128 + 32) + delta512));
            W64(table, p * 128 + 40, checked(U64(table, p * 128 + 40) + delta512));
        }
        W64(gpt, 32, checked(U64(gpt, 32) + delta512));
        W64(gpt, 48, checked(U64(gpt, 48) + delta512));
        W64(backup, 24, checked(U64(backup, 24) + delta512));
        W64(backup, 48, checked(U64(backup, 48) + delta512));
        W64(backup, 72, checked(U64(backup, 72) + delta512));
        foreach (var header in new[] { gpt, backup })
        {
            W32(header, 88, Crc32(table));
            W32(header, 16, 0);
            W32(header, 16, Crc32(header.AsSpan(0, (int)U32(header, 12))));
        }
        // Ubuntu's protective MBR must span the enlarged image; reject a hybrid MBR with extra entries.
        // xorriso also writes an active, type-zero, one-sector dummy entry for old BIOS firmware.
        Require(mbr[450] == 0xee && U32(mbr, 454) == 1 && mbr[462] == 0x80 && mbr[466] == 0 &&
            U32(mbr, 470) == 0 && U32(mbr, 474) == 1 && mbr.AsSpan(478, 32).IndexOfAnyExcept((byte)0) < 0,
            "expected Ubuntu's protective MBR and BIOS dummy entry");
        W32(mbr, 458, checked((uint)Math.Min(uint.MaxValue, (ulong)(input.Length / 512 - 1) + delta512)));
        patches.Add(0, mbr);
        patches.Add(512, gpt);
        patches.Add((long)tableLba * 512, table);
        patches.Add(backupTableOffset + payload.Length, table);
        patches.Add(input.Length - 512 + payload.Length, backup);
        return new(split, payload.ToArray(), patches);
    }

    public static string PatchGrub(string original) =>
        "set timeout=5\nset default=0\n\n" +
        "menuentry \"Autoinstall The Construct VM (blank base + setup hint)\" {\n" +
        "    set gfxpayload=keep\n" +
        "    linux  /casper/vmlinuz  autoinstall ds=nocloud\\;s=/cdrom/boot/grub/  ---\n" +
        "    initrd /casper/initrd\n}\n\n" +
        Regex.Replace(original.Replace("\r\n", "\n"), @"^[ \t]*set[ \t]+(?:timeout|default)[^\n]*(?:\n|$)", "", RegexOptions.Multiline);

    private static DirectoryData FindDirectory(Stream input, View v, params string[] path)
    {
        var record = v.Descriptor.AsSpan(156, 34).ToArray();
        foreach (var name in path)
        {
            var dir = ReadDirectory(input, v, record);
            record = dir.Entries.SingleOrDefault(e => e.Name == name)?.Record
                ?? throw new InvalidDataException($"Missing ISO directory: {name}");
            Require((record[25] & 2) != 0, "expected directory");
        }
        return ReadDirectory(input, v, record);
    }

    private static DirectoryData ReadDirectory(Stream input, View v, byte[] record)
    {
        var size = U32(record, 10);
        Require(size is > 0 and <= 16 * 1024 * 1024 && size % Block == 0, "invalid directory size");
        var offset = checked(((long)U32(record, 2) + v.Base) * Block);
        var bytes = Read(input, offset, (int)size);
        var entries = new List<Entry>();
        for (var p = 0; p < bytes.Length;)
        {
            var length = bytes[p];
            if (length == 0) { p = (p / Block + 1) * Block; continue; }
            Require(length >= 34 && p + length <= bytes.Length && p % Block + length <= Block, "invalid directory record");
            var r = bytes.AsSpan(p, length).ToArray();
            Require(r[1] == 0 && r[26] == 0 && r[27] == 0 && 33 + r[32] <= r.Length, "unsupported directory record");
            Require(U32(r, 2) == BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(6)) &&
                U32(r, 10) == BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(14)), "record endian mismatch");
            var name = r[32] == 1 && r[33] <= 1 ? (r[33] == 0 ? "." : "..") :
                (v.Joliet ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(r, 33, r[32]).Split(';')[0].TrimEnd('.').ToLowerInvariant();
            if (!v.Joliet)
            {
                foreach (var su in SystemUse(r))
                    if (su[0] == 'N' && su[1] == 'M')
                    {
                        Require(su.Length >= 5 && su[4] == 0, "continued Rock Ridge name");
                        name = Encoding.UTF8.GetString(su, 5, su.Length - 5);
                    }
            }
            entries.Add(new(p, r, name));
            p += length;
        }
        return new(offset, bytes, entries);
    }

    private static IEnumerable<byte[]> SystemUse(byte[] record)
    {
        var start = 33 + record[32];
        if (start % 2 != 0) start++;
        for (var p = start; p + 4 <= record.Length;)
        {
            var length = record[p + 2];
            if (record[p] == 0 && length == 0) yield break;
            Require(length >= 4 && p + length <= record.Length, "invalid Rock Ridge entry");
            yield return record.AsSpan(p, length).ToArray();
            p += length;
        }
    }

    private static void ValidateTree(Stream input, View v, long split)
    {
        var todo = new Queue<byte[]>();
        todo.Enqueue(v.Descriptor.AsSpan(156, 34).ToArray());
        var seen = new HashSet<uint>();
        while (todo.TryDequeue(out var record))
        {
            Require(seen.Count < 10000, "too many directories");
            if (!seen.Add(U32(record, 2))) continue;
            var dir = ReadDirectory(input, v, record);
            foreach (var e in dir.Entries)
            {
                var r = e.Record;
                Require(((long)U32(r, 2) + v.Base) * Block + U32(r, 10) <= split, "file extent crosses EFI boundary");
                if (!v.Joliet)
                    foreach (var su in SystemUse(r))
                        if (su[0] == 'C' && su[1] == 'E')
                        {
                            Require(su.Length == 28 && ((long)U32(su, 4) + v.Base) * Block + U32(su, 12) + U32(su, 20) <= split,
                                "continuation area crosses EFI boundary");
                        }
                if (e.Name is not ("." or "..") && (r[25] & 2) != 0) todo.Enqueue(r);
            }
        }
    }

    private static byte[] ReadFile(Stream input, View v, DirectoryData dir, string name)
    {
        var records = dir.Entries.Where(e => e.Name == name).ToArray();
        Require(records.Length == 1 && (records[0].Record[25] & 0x82) == 0, $"missing or multi-extent file: {name}");
        var r = records[0].Record;
        Require(U32(r, 10) <= 16 * 1024 * 1024, "metadata file too large");
        return Read(input, ((long)U32(r, 2) + v.Base) * Block, checked((int)U32(r, 10)));
    }

    private static void SetExtent(byte[] r, (uint Lba, int Size) file, uint baseBlock)
    {
        Both(r, 2, checked(file.Lba - baseBlock));
        Both(r, 10, checked((uint)file.Size));
    }

    private static byte[] NewFileRecord(string name, (uint Lba, int Size) file, View v, byte[] template)
    {
        var id = v.Joliet ? Encoding.BigEndianUnicode.GetBytes(name) : Encoding.ASCII.GetBytes(name == "user-data" ? "USER_DAT.;1" : "META_DAT.;1");
        var nm = Encoding.ASCII.GetBytes(name);
        var start = (33 + id.Length + 1) & ~1;
        var r = new byte[start + (v.Joliet ? 0 : 5 + nm.Length)];
        Array.Copy(template, r, 33);
        r[0] = checked((byte)r.Length);
        r[25] = 0;
        r[32] = checked((byte)id.Length);
        id.CopyTo(r, 33);
        if (!v.Joliet)
        {
            r[start] = (byte)'N'; r[start + 1] = (byte)'M'; r[start + 2] = checked((byte)(5 + nm.Length)); r[start + 3] = 1;
            nm.CopyTo(r, start + 5);
        }
        SetExtent(r, file, v.Base);
        return r;
    }

    private static byte[] PackDirectory(DirectoryData dir)
    {
        var result = new byte[dir.Bytes.Length];
        var p = 0;
        // Dot entries must occupy the first two records even in Joliet, whose ordinary names
        // start with a zero UTF-16 high byte and would otherwise sort before the 0x01 parent ID.
        foreach (var e in dir.Entries.OrderBy(e => e.Name == "." ? 0 : e.Name == ".." ? 1 : 2)
                     .ThenBy(e => Convert.ToHexString(e.Record.AsSpan(33, e.Record[32])), StringComparer.Ordinal))
        {
            if (p % Block + e.Record.Length > Block) p = Padded(p);
            Require(p + e.Record.Length <= result.Length, "directory has insufficient space for seed records");
            e.Record.CopyTo(result, p);
            p += e.Record.Length;
        }
        return result;
    }

    private static void ValidateGpt(byte[] header)
    {
        Require(Encoding.ASCII.GetString(header, 0, 8) == "EFI PART" && U32(header, 8) == 0x10000 && U32(header, 12) == 92,
            "missing or unsupported GPT header");
        var expected = U32(header, 16);
        W32(header, 16, 0);
        Require(Crc32(header.AsSpan(0, 92)) == expected, "GPT header checksum mismatch");
        W32(header, 16, expected);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        return ~crc;
    }
}
