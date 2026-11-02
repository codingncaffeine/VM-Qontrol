using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace VmQontrol.Core.Install;

/// <summary>
/// A "newc" cpio archive, as the Linux kernel unpacks an initrd. Appending a second, compressed archive to an initrd
/// adds files to it (the kernel unpacks them in turn), which is how an answer file is put inside a Debian installer.
/// </summary>
public static class Cpio
{
    public static byte[] Newc(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        var ino = 1;
        foreach (var (name, data) in files) Entry(output, name, data, 0x81A4 /* regular file, 0644 */, ino++);
        Entry(output, "TRAILER!!!", [], 0, 0);
        Pad(output, 512);
        return output.ToArray();
    }

    /// <summary>The archive gzip-compressed (the form an initrd addition takes).</summary>
    public static byte[] Gzipped(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(Newc(files));
        return output.ToArray();
    }

    private static void Entry(Stream s, string name, byte[] data, int mode, int ino)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        string Hex(long v) => v.ToString("X8", CultureInfo.InvariantCulture);
        var header = "070701" + Hex(ino) + Hex(mode) + Hex(0) + Hex(0) + Hex(1) + Hex(0) + Hex(data.Length)
                     + Hex(0) + Hex(0) + Hex(0) + Hex(0) + Hex(nameBytes.Length) + Hex(0);
        s.Write(Encoding.ASCII.GetBytes(header));
        s.Write(nameBytes);
        Pad(s, 4);
        s.Write(data);
        Pad(s, 4);
    }

    private static void Pad(Stream s, int to)
    {
        while (s.Length % to != 0) s.WriteByte(0);
    }
}
