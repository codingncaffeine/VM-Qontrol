using System.Buffers.Binary;
using System.Text;

namespace VmQontrol.Core.Media;

/// <summary>
/// Reads files from a disc image's UDF filesystem (ECMA-167 / OSTA UDF with a type 1 partition map, as Windows
/// installation discs use). Windows discs keep their real files there; the ISO 9660 view holds only a README. Reads the
/// descriptors and the file asked for, never the whole image. Returns <c>null</c> for anything it cannot follow.
/// </summary>
public sealed class UdfReader : IDisposable
{
    private const int TagFileSetDescriptor = 256, TagFileIdentifier = 257, TagFileEntry = 261, TagExtendedFileEntry = 266;
    private readonly Stream _stream;
    private readonly int _blockSize;
    private readonly long _partitionStart;
    private readonly (uint Lbn, uint Length) _root;

    private UdfReader(Stream stream, int blockSize, long partitionStart, (uint, uint) root)
    {
        _stream = stream;
        _blockSize = blockSize;
        _partitionStart = partitionStart;
        _root = root;
    }

    public static UdfReader? Open(string file)
    {
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        var reader = Open(stream);
        if (reader is null) stream.Dispose();
        return reader;
    }

    public static UdfReader? Open(Stream stream)
    {
        try
        {
            const int sector = 2048;
            var anchor = Read(stream, 256L * sector, sector);
            if (Tag(anchor) != 2) return null;
            var vdsLength = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(16));
            var vdsLocation = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(20));
            long? partitionStart = null;
            int blockSize = 0;
            (uint Lbn, uint Length)? fsd = null;
            for (var i = 0; i < vdsLength / sector && i < 64; i++)
            {
                var d = Read(stream, (vdsLocation + i) * (long)sector, sector);
                switch (Tag(d))
                {
                    case 5: // Partition Descriptor
                        partitionStart = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(188));
                        break;
                    case 6: // Logical Volume Descriptor
                        blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(212));
                        fsd = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(252)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(248)));
                        if (d[440] != 1) return null; // Only type 1 (physical) partition maps; UDF 2.5 metadata partitions are not read.
                        break;
                    case 8: // Terminating Descriptor
                        i = int.MaxValue - 1;
                        break;
                }
            }

            if (partitionStart is not { } start || blockSize == 0 || fsd is not { } f) return null;
            var fileSet = Read(stream, (start + f.Lbn) * blockSize, blockSize);
            if (Tag(fileSet) != TagFileSetDescriptor) return null;
            var root = (BinaryPrimitives.ReadUInt32LittleEndian(fileSet.AsSpan(404)), BinaryPrimitives.ReadUInt32LittleEndian(fileSet.AsSpan(400)));
            return new UdfReader(stream, blockSize, start * (long)blockSize, root);
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>A file's bytes by its path ("sources/idwbinfo.txt", any letter case), or <c>null</c>; at most <paramref name="limit"/> bytes.</summary>
    public byte[]? ReadFile(string path, int limit = 16 * 1024 * 1024) =>
        Find(path) is { } entry ? Contents(entry, limit) : null;

    /// <summary>The first <paramref name="count"/> bytes of a file and its size (for headers of large files).</summary>
    public (byte[] Head, long Size)? ReadHead(string path, int count)
    {
        if (Find(path) is not { } entry) return null;
        var (_, size) = Extents(entry);
        return (Contents(entry, count) ?? [], size);
    }

    /// <summary>Bytes at an offset inside a file (reads across its extents).</summary>
    public byte[]? ReadAt(string path, long offset, int count)
    {
        if (Find(path) is not { } entry) return null;
        var (extents, _) = Extents(entry);
        var result = new byte[count];
        var filled = 0;
        long position = 0;
        foreach (var (lbn, length) in extents)
        {
            if (offset + filled < position + length && filled < count)
            {
                var within = offset + filled - position;
                var take = (int)Math.Min(count - filled, length - within);
                var bytes = Read(_stream, _partitionStart + (long)lbn * _blockSize + within, take);
                bytes.CopyTo(result, filled);
                filled += take;
            }

            position += length;
        }

        return filled == count ? result : null;
    }

    private byte[]? Find(string path)
    {
        var entry = Block(_root.Lbn);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = Contents(entry, 4 * 1024 * 1024);
            if (directory is null) return null;
            byte[]? next = null;
            for (var at = 0; at + 38 <= directory.Length;)
            {
                if (Tag(directory.AsSpan(at)) != TagFileIdentifier) break;
                var nameLength = directory[at + 19];
                var useLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 36));
                var icbLbn = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 24));
                var name = DecodeName(directory.AsSpan(at + 38 + useLength, nameLength));
                if (string.Equals(name, part, StringComparison.OrdinalIgnoreCase)) next = Block(icbLbn);
                var size = 38 + useLength + nameLength;
                at += (size + 3) & ~3;
                if (next is not null) break;
            }

            if (next is null) return null;
            entry = next;
        }

        return entry;
    }

    private byte[]? Contents(byte[] entry, int limit)
    {
        var (extents, size) = Extents(entry);
        if (extents.Count == 0 && size > 0)
        {
            // Data stored inside the entry itself.
            var (adOffset, adLength) = Allocation(entry);
            return entry.AsSpan(adOffset, (int)Math.Min(Math.Min(adLength, size), limit)).ToArray();
        }

        var total = (int)Math.Min(size, limit);
        var result = new byte[total];
        var filled = 0;
        foreach (var (lbn, length) in extents)
        {
            if (filled >= total) break;
            var take = (int)Math.Min(length, total - filled);
            Read(_stream, _partitionStart + (long)lbn * _blockSize, take).CopyTo(result, filled);
            filled += take;
        }

        return result;
    }

    private (List<(uint Lbn, uint Length)> Extents, long Size) Extents(byte[] entry)
    {
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(56));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(34)) & 7;
        var (offset, length) = Allocation(entry);
        var extents = new List<(uint, uint)>();
        if (flags == 3) return (extents, size);
        var step = flags == 0 ? 8 : 16;
        for (var at = offset; at + step <= offset + length; at += step)
        {
            var extentLength = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(at)) & 0x3FFFFFFF;
            var lbn = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(at + 4));
            if (extentLength == 0) break;
            extents.Add((lbn, extentLength));
        }

        return (extents, size);
    }

    /// <summary>Where a (extended) file entry's allocation descriptors start, and their length.</summary>
    private static (int Offset, int Length) Allocation(byte[] entry)
    {
        var extended = Tag(entry) == TagExtendedFileEntry;
        var eaLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(extended ? 208 : 168));
        var adLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(extended ? 212 : 172));
        return ((extended ? 216 : 176) + eaLength, adLength);
    }

    private byte[] Block(uint lbn) => Read(_stream, _partitionStart + (long)lbn * _blockSize, _blockSize);

    private static ushort Tag(ReadOnlySpan<byte> d) => BinaryPrimitives.ReadUInt16LittleEndian(d);

    /// <summary>OSTA compressed Unicode: a first byte of 8 means one byte per character, 16 means UTF-16 big endian.</summary>
    private static string DecodeName(ReadOnlySpan<byte> raw) => raw.Length == 0 ? "" : raw[0] switch
    {
        8 => Encoding.Latin1.GetString(raw[1..]),
        16 => Encoding.BigEndianUnicode.GetString(raw[1..]),
        _ => "",
    };

    private static byte[] Read(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    public void Dispose() => _stream.Dispose();
}
