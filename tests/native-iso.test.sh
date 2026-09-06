#!/usr/bin/env bash
# Real-image integration test. Development dependencies: .NET 10, xorriso, Python 3.
# Usage: bash tests/native-iso.test.sh /path/to/ubuntu-24.04.4-live-server-amd64.iso
# Uses ~10 GiB of scratch space. Does not boot a VM or require root/mount/WSL.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_iso="$(realpath "${1:?Pass a stock Ubuntu live-server ISO}")"
work="$(mktemp -d)"
trap 'rm -r "$work"' EXIT

CONSTRUCT_TEST_ISO="$source_iso" dotnet test "$root/tests/Construct.Iso.Tests" -c Release
BOOTSTRAP_PUBKEY_FILE="$root/reference/bootstrap_ed25519.pub" VM_HOSTNAME_SOURCE=hyperv-kvp \
  dotnet run --project "$root/src/Construct.Iso" -c Release --no-build -- "$source_iso" "$work/native.iso"

# Use an independent ISO reader, including the partition-offset filesystem used for USB boot.
xorriso -indev "$work/native.iso" -osirrox on -extract / "$work/tree" >"$work/extract.log" 2>&1
(cd "$work/tree" && md5sum -c md5sum.txt >"$work/md5.log")
echo "All $(wc -l <"$work/md5.log") installer file checksums passed."
dd if="$work/native.iso" of="$work/partition.iso" bs=2048 skip=16 status=none
for image in native partition; do
  for view in any norock; do
    xorriso -read_fs "$view" -indev "$work/$image.iso" -osirrox on \
      -extract /boot/grub/user-data "$work/$image-$view-user-data" \
      -extract /boot/grub/meta-data "$work/$image-$view-meta-data" \
      -extract /boot/grub/grub.cfg "$work/$image-$view-grub.cfg" >"$work/$image-$view.log" 2>&1
    for file in user-data meta-data grub.cfg; do
      cmp "$work/tree/boot/grub/$file" "$work/$image-$view-$file"
    done
  done
done
echo "Primary, Joliet, partition primary and partition Joliet seeds match."

xorriso -indev "$source_iso" -osirrox on -extract_boot_images "$work/original-boot" >"$work/original-boot.log" 2>&1
xorriso -indev "$work/native.iso" -osirrox on -extract_boot_images "$work/native-boot" >"$work/native-boot.log" 2>&1
for file in "$work/original-boot"/eltorito_img*.img; do
  cmp "$file" "$work/native-boot/$(basename "$file")"
done
echo "BIOS and UEFI boot images are byte-identical to stock."

python3 - "$source_iso" "$work/native.iso" "$work/tree/boot/grub/user-data" <<'PY'
import pathlib, struct, sys, zlib, re, base64
original, output, seed = map(pathlib.Path, sys.argv[1:])
with original.open('rb') as a, output.open('rb') as b:
    assert a.read(446) == b.read(446), 'MBR boot code changed'
    for lba in (1, output.stat().st_size // 512 - 1):
        b.seek(lba * 512)
        header = bytearray(b.read(512))
        assert header[:8] == b'EFI PART'
        size, checksum = struct.unpack_from('<II', header, 12)
        struct.pack_into('<I', header, 16, 0)
        assert zlib.crc32(header[:size]) == checksum
        assert struct.unpack_from('<Q', header, 24)[0] == lba
        start, count, length, checksum = struct.unpack_from('<QIII', header, 72)
        b.seek(start * 512)
        assert zlib.crc32(b.read(count * length)) == checksum
text = seed.read_text()
assert '${' not in text, 'Unexpanded seed template'
assert 'hostname: construct-seed' in text
payloads = [base64.b64decode(s).decode() for s in re.findall(r'echo ([A-Za-z0-9+/=]+) \| base64 -d', text)]
assert len(payloads) == 4
assert any('VirtualMachineName' in p for p in payloads)
assert any('.\\Provision-AgentVM.ps1' in p for p in payloads)
print('GPT checksums, MBR code and embedded guest scripts verified.')
PY

# A pre-patched input must fail without creating output.
if BOOTSTRAP_PUBKEY_FILE="$root/reference/bootstrap_ed25519.pub" \
  dotnet run --project "$root/src/Construct.Iso" -c Release --no-build -- "$work/native.iso" "$work/twice.iso"; then
  echo 'ERROR: accepted an already patched image' >&2
  exit 1
fi
test ! -e "$work/twice.iso"
echo 'Native ISO integration checks passed.'
