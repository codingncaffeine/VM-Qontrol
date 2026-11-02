using System.Security.Cryptography;
using System.Text;

namespace VmQontrol.Core.Install;

/// <summary>
/// SHA-512 crypt ("$6$…"), the password hash Linux installers take (Ubuntu's autoinstall accepts only a hashed
/// password). Implements Ulrich Drepper's published specification, including its 5000-round default.
/// </summary>
public static class Sha512Crypt
{
    private const string Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>A hash with a fresh random salt.</summary>
    public static string Hash(string password)
    {
        var salt = new char[16];
        for (var i = 0; i < salt.Length; i++) salt[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Hash(password, new string(salt));
    }

    public static string Hash(string password, string salt, int rounds = 5000)
    {
        var p = Encoding.UTF8.GetBytes(password);
        var s = Encoding.UTF8.GetBytes(salt.Length > 16 ? salt[..16] : salt);

        var b = SHA512.HashData([.. p, .. s, .. p]);
        var a = new List<byte>(p.Length * 4);
        a.AddRange(p);
        a.AddRange(s);
        for (var n = p.Length; n > 0; n -= 64) a.AddRange(b.AsSpan(0, Math.Min(64, n)).ToArray());
        for (var n = p.Length; n > 0; n >>= 1) a.AddRange((n & 1) != 0 ? b : p);
        var c = SHA512.HashData(a.ToArray());

        var dp = SHA512.HashData(Repeat(p, p.Length));
        var pp = Stretch(dp, p.Length);
        var ds = SHA512.HashData(Repeat(s, 16 + c[0]));
        var ss = Stretch(ds, s.Length);

        for (var i = 0; i < rounds; i++)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            h.AppendData((i & 1) != 0 ? pp : c);
            if (i % 3 != 0) h.AppendData(ss);
            if (i % 7 != 0) h.AppendData(pp);
            h.AppendData((i & 1) != 0 ? c : pp);
            c = h.GetHashAndReset();
        }

        var output = new StringBuilder("$6$");
        if (rounds != 5000) output.Append("rounds=").Append(rounds).Append('$');
        output.Append(Encoding.UTF8.GetString(s)).Append('$');
        int[][] order =
        [
            [0, 21, 42], [22, 43, 1], [44, 2, 23], [3, 24, 45], [25, 46, 4], [47, 5, 26], [6, 27, 48], [28, 49, 7],
            [50, 8, 29], [9, 30, 51], [31, 52, 10], [53, 11, 32], [12, 33, 54], [34, 55, 13], [56, 14, 35], [15, 36, 57],
            [37, 58, 16], [59, 17, 38], [18, 39, 60], [40, 61, 19], [62, 20, 41],
        ];
        foreach (var t in order) Encode(output, c[t[0]], c[t[1]], c[t[2]], 4);
        Encode(output, 0, 0, c[63], 2);
        return output.ToString();
    }

    private static byte[] Repeat(byte[] data, int times)
    {
        var result = new byte[data.Length * times];
        for (var i = 0; i < times; i++) data.CopyTo(result, i * data.Length);
        return result;
    }

    private static byte[] Stretch(byte[] digest, int length)
    {
        var result = new byte[length];
        for (var i = 0; i < length; i++) result[i] = digest[i % digest.Length];
        return result;
    }

    private static void Encode(StringBuilder output, byte b2, byte b1, byte b0, int chars)
    {
        var w = (b2 << 16) | (b1 << 8) | b0;
        for (var i = 0; i < chars; i++)
        {
            output.Append(Alphabet[w & 0x3F]);
            w >>= 6;
        }
    }
}
