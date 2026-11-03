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
        if (Find(path) is not { IsDirectory: false } entry || entry.Size > limit) return null;
        return ReadAt(path, 0, checked((int)entry.Size));
    }

    public bool Exists(string path) => Find(path) is not null;

    private sealed record Entry(string Name, uint Extent, uint Length, bool IsDirectory)
    {
        /// <summary>Every piece of the file in order: one for most files, several for one over 4 GiB (multi-extent).</summary>
        public List<(uint Extent, uint Length)> Pieces { get; init; } = [(Extent, Length)];

        public long Size => Pieces.Sum(p => (long)p.Length);
    }

    /// <summary>The size of the file at <paramref name="path"/>, or <c>null</c>.</summary>
    public long? SizeOf(string path) => Find(path) is { IsDirectory: false } entry ? entry.Size : null;

    /// <summary>Bytes at an offset inside a file, across its pieces; <c>null</c> when the file is missing or too short.</summary>
    public byte[]? ReadAt(string path, long offset, int count)
    {
        if (Find(path) is not { IsDirectory: false } entry || offset < 0 || offset + count > entry.Size) return null;
        var result = new byte[count];
        var filled = 0;
        long start = 0;
        foreach (var (extent, length) in entry.Pieces)
        {
            var at = offset + filled;
            if (filled < count && at < start + length)
            {
                var take = (int)Math.Min(count - filled, start + length - at);
                _stream.Position = (long)extent * Sector + (at - start);
                _stream.ReadExactly(result, filled, take);
                filled += take;
            }

            start += length;
        }

        return filled == count ? result : null;
    }

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
        Entry? open = null;
        foreach (var (entry, more) in Records(directory))
        {
            if (open is not null && entry.Name == open.Name)
            {
                open.Pieces.Add((entry.Extent, entry.Length));
            }
            else
            {
                if (open is not null) yield return open;
                open = entry;
            }

            if (!more)
            {
                yield return open;
                open = null;
            }
        }

        if (open is not null) yield return open;
    }

    private IEnumerable<(Entry Entry, bool More)> Records(Entry directory)
    {
        // A directory on a real disc is a few sectors to a few megabytes; a size past this is a damaged disc, not read.
        if (directory.Size > 64L << 20) throw new InvalidDataException($"A directory on the disc claims {directory.Size} bytes; the disc looks damaged.");
        var data = new byte[directory.Size];
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
                var flags = data[at + 25];
                yield return (new Entry(name, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 2)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 10)), (flags & 2) != 0), (flags & 0x80) != 0);
            }

            at += length;
        }
    }

    public void Dispose() => _stream.Dispose();
}
