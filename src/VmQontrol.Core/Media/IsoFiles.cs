using System.Buffers.Binary;
using System.Text;

namespace VmQontrol.Core.Media;

/// <summary>
/// Reads files from a disc image's ISO 9660 filesystem, by their real names through the Joliet tree when the disc has
/// one (Ubuntu, Debian and Fedora discs do), else through the primary tree with ";1" versions ignored. Used to take an
/// installer's kernel and initrd off the disc for a direct boot.
/// </summary>
public sealed class IsoFiles : IDisposable
{
    private const int Sector = 2048;
    private readonly Stream _stream;
    private readonly (uint Extent, uint Length) _root;
    private readonly bool _joliet;

    private IsoFiles(Stream stream, (uint, uint) root, bool joliet)
    {
        _stream = stream;
        _root = root;
        _joliet = joliet;
    }

    public static IsoFiles? Open(string file)
    {
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        var reader = Open(stream);
        if (reader is null) stream.Dispose();
        return reader;
    }

    public static IsoFiles? Open(Stream stream)
    {
        (uint, uint)? primary = null, joliet = null;
        var d = new byte[Sector];
        for (var n = 16; n < 32; n++)
        {
            stream.Position = (long)n * Sector;
            if (stream.ReadAtLeast(d, Sector, throwOnEndOfStream: false) < Sector || !d.AsSpan(1, 5).SequenceEqual("CD001"u8)) break;
            var root = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(156 + 2)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(156 + 10)));
            if (d[0] == 1) primary ??= root;
            if (d[0] == 2 && d[88] == 0x25 && d[89] == 0x2F && d[90] is 0x40 or 0x43 or 0x45) joliet ??= root;
            if (d[0] == 255) break;
        }

        return joliet is { } j ? new IsoFiles(stream, j, true) : primary is { } p ? new IsoFiles(stream, p, false) : null;
    }

    /// <summary>The file at <paramref name="path"/> ("casper/vmlinuz", any letter case), or <c>null</c>.</summary>
    public byte[]? Read(string path, long limit = 512L * 1024 * 1024)
    {
        if (Find(path) is not { IsDirectory: false } entry || entry.Length > limit) return null;
        var bytes = new byte[entry.Length];
        _stream.Position = (long)entry.Extent * Sector;
        _stream.ReadExactly(bytes);
        return bytes;
    }

    public bool Exists(string path) => Find(path) is not null;

    private sealed record Entry(string Name, uint Extent, uint Length, bool IsDirectory);

    private Entry? Find(string path)
    {
        var current = new Entry("", _root.Extent, _root.Length, true);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!current.IsDirectory) return null;
            current = List(current).FirstOrDefault(e => Matches(e.Name, part));
            if (current is null) return null;
        }

        return current;
    }

    private static bool Matches(string onDisc, string wanted)
    {
        var name = onDisc.Split(';')[0];
        if (name.EndsWith('.') && !wanted.EndsWith('.')) name = name[..^1];
        return string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, wanted.Replace('-', '_'), StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<Entry> List(Entry directory)
    {
        var data = new byte[directory.Length];
        _stream.Position = (long)directory.Extent * Sector;
        _stream.ReadExactly(data);
        for (var at = 0; at < data.Length;)
        {
            var length = data[at];
            if (length == 0)
            {
                at = (at / Sector + 1) * Sector; // records never cross a sector; zero padding runs to the next
                continue;
            }

            var nameLength = data[at + 32];
            var raw = data.AsSpan(at + 33, nameLength);
            if (!(nameLength == 1 && raw[0] is 0 or 1))
            {
                var name = _joliet ? Encoding.BigEndianUnicode.GetString(raw) : Encoding.ASCII.GetString(raw);
                yield return new Entry(name, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 2)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 10)), (data[at + 25] & 2) != 0);
            }

            at += length;
        }
    }

    public void Dispose() => _stream.Dispose();
}
