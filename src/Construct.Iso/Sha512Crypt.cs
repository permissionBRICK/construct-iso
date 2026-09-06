using System.Security.Cryptography;
using System.Text;

namespace Construct.Iso;

/// <summary>SHA-512 crypt ($6$), default 5000 rounds. No platform crypt()/openssl dependency.</summary>
public static class Sha512Crypt
{
    private const string Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Hash(string password, string? salt = null)
    {
        salt ??= string.Concat(RandomNumberGenerator.GetBytes(16).Select(b => Alphabet[b & 63]));
        if (salt.Length is < 1 or > 16 || salt.Any(c => !Alphabet.Contains(c)))
            throw new ArgumentException("Invalid SHA-512 crypt salt.");
        var key = Encoding.UTF8.GetBytes(password);
        if (key.Length is < 1 or > 1024) throw new ArgumentException("Seed password must be 1–1024 UTF-8 bytes.");
        var saltBytes = Encoding.ASCII.GetBytes(salt);
        byte[] Digest(params byte[][] parts)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            foreach (var p in parts) h.AppendData(p);
            return h.GetHashAndReset();
        }
        byte[] Repeat(byte[] source, int length) => Enumerable.Range(0, length).Select(i => source[i % source.Length]).ToArray();
        var alternate = Digest(key, saltBytes, key);
        using var initial = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        initial.AppendData(key);
        initial.AppendData(saltBytes);
        initial.AppendData(Repeat(alternate, key.Length));
        for (var n = key.Length; n > 0; n >>= 1) initial.AppendData((n & 1) != 0 ? alternate : key);
        var result = initial.GetHashAndReset();
        using var ph = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < key.Length; i++) ph.AppendData(key);
        var pbytes = Repeat(ph.GetHashAndReset(), key.Length);
        using var sh = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < 16 + result[0]; i++) sh.AppendData(saltBytes);
        var sbytes = Repeat(sh.GetHashAndReset(), saltBytes.Length);
        for (var i = 0; i < 5000; i++)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            h.AppendData((i & 1) != 0 ? pbytes : result);
            if (i % 3 != 0) h.AppendData(sbytes);
            if (i % 7 != 0) h.AppendData(pbytes);
            h.AppendData((i & 1) != 0 ? result : pbytes);
            result = h.GetHashAndReset();
        }
        var encoded = new StringBuilder();
        void Encode(int a, int b, int c, int count)
        {
            var bits = (a << 16) | (b << 8) | c;
            for (var i = 0; i < count; i++) { encoded.Append(Alphabet[bits & 63]); bits >>= 6; }
        }
        int[] order = [0,21,42,22,43,1,44,2,23,3,24,45,25,46,4,47,5,26,6,27,48,
            28,49,7,50,8,29,9,30,51,31,52,10,53,11,32,12,33,54,34,55,13,56,14,35,
            15,36,57,37,58,16,59,17,38,18,39,60,40,61,19,62,20,41];
        for (var i = 0; i < order.Length; i += 3) Encode(result[order[i]], result[order[i+1]], result[order[i+2]], 4);
        Encode(0, 0, result[63], 2);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(pbytes);
        return $"$6${salt}${encoded}";
    }
}
