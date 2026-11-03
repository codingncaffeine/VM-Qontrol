using System.Buffers.Binary;
using System.Text;

namespace VmQontrol.Core.Media;

/// <summary>
/// Writes a small data disc: ISO 9660 with Joliet names, files in the root folder only. That is all an answer-file
/// disc needs (Windows autounattend.xml, a cloud-init "cidata" seed), and it needs no ISO tool on the host.
/// </summary>
public static class IsoWriter
{
    private const int Sector = 2048;

    /// <summary>Writes the disc to a file. <paramref name="keepLabelCase"/> keeps the label's letter case in the primary
    /// descriptor (as genisoimage -V does, and many real discs do, e.g. "Ubuntu 24.04.1 LTS amd64"); strict ISO 9660
    /// upper-cases it.</summary>
    public static void Write(string isoFile, string volumeId, IReadOnlyDictionary<string, byte[]> files, bool keepLabelCase = false)
    {
        // Answer discs hold a password hash or, for Windows, the password and product key: readable by this account only.
        if (File.Exists(isoFile)) File.Delete(isoFile);
        using var stream = new FileStream(isoFile, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        Write(stream, volumeId, files, keepLabelCase);
    }

    public static void Write(Stream output, string volumeId, IReadOnlyDictionary<string, byte[]> files, bool keepLabelCase = false)
    {
        var names = files.Keys.Order(StringComparer.Ordinal).ToList();
        var shortNames = ShortNames(names);
        // Fixed layout: descriptors 16-18, path tables 19-22, root folders 23-24, file data from 25.
        const int primaryRoot = 23, jolietRoot = 24;
        var lba = 25;
        var extents = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            extents[name] = lba;
            lba += Math.Max(1, (files[name].Length + Sector - 1) / Sector);
        }

        // 150 sectors of padding, as mkisofs adds: readers read ahead and fail on a disc that ends right after its data.
        var total = lba + 150;
        var image = new byte[(long)total * Sector];
        var now = DateTime.UtcNow;

        var primaryDir = Directory(primaryRoot, names.Select(n => (Encoding.ASCII.GetBytes(shortNames[n] + ";1"), extents[n], files[n].Length)), now);
        var jolietDir = Directory(jolietRoot, names.Select(n => (Encoding.BigEndianUnicode.GetBytes(n), extents[n], files[n].Length)), now);
        if (primaryDir.Length > Sector || jolietDir.Length > Sector) throw new ArgumentException("Too many files for a one-sector folder.");
        primaryDir.CopyTo(image, primaryRoot * Sector);
        jolietDir.CopyTo(image, jolietRoot * Sector);

        PathTable(image.AsSpan(19 * Sector), primaryRoot, littleEndian: true);
        PathTable(image.AsSpan(20 * Sector), primaryRoot, littleEndian: false);
        PathTable(image.AsSpan(21 * Sector), jolietRoot, littleEndian: true);
        PathTable(image.AsSpan(22 * Sector), jolietRoot, littleEndian: false);

        Descriptor(image.AsSpan(16 * Sector, Sector), 1, keepLabelCase ? volumeId : volumeId.ToUpperInvariant(), primaryRoot, 19, 20, total, now, joliet: false);
        Descriptor(image.AsSpan(17 * Sector, Sector), 2, volumeId, jolietRoot, 21, 22, total, now, joliet: true);
        var end = image.AsSpan(18 * Sector, Sector);
        end[0] = 255;
        "CD001"u8.CopyTo(end[1..]);
        end[6] = 1;

        foreach (var name in names) files[name].CopyTo(image, extents[name] * Sector);
        output.Write(image);
    }

    /// <summary>ISO 9660 names: upper case, d-characters only, unique; Joliet carries the real names.</summary>
    private static Dictionary<string, string> ShortNames(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var clean = new string(name.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
            var dot = clean.LastIndexOf('.');
            var stem = (dot >= 0 ? clean[..dot] : clean).Replace('.', '_');
            var ext = dot >= 0 ? clean[(dot + 1)..] : "";
            stem = stem[..Math.Min(stem.Length, 24)];
            var candidate = ext.Length > 0 ? $"{stem}.{ext[..Math.Min(3, ext.Length)]}" : stem;
            for (var i = 1; !used.Add(candidate); i++) candidate = $"{stem[..Math.Min(stem.Length, 20)]}_{i}{(ext.Length > 0 ? "." + ext[..Math.Min(3, ext.Length)] : "")}";
            result[name] = candidate;
        }

        return result;
    }

    private static byte[] Directory(int self, IEnumerable<(byte[] Name, int Extent, int Length)> entries, DateTime now)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Record([0], self, Sector, isDirectory: true, now));
        bytes.AddRange(Record([1], self, Sector, isDirectory: true, now));
        foreach (var (name, extent, length) in entries) bytes.AddRange(Record(name, extent, length, isDirectory: false, now));
        return [.. bytes];
    }

    private static byte[] Record(byte[] name, int extent, int length, bool isDirectory, DateTime now)
    {
        var size = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        var r = new byte[size];
        r[0] = (byte)size;
        Both32(r.AsSpan(2), extent);
        Both32(r.AsSpan(10), length);
        r[18] = (byte)(now.Year - 1900);
        r[19] = (byte)now.Month;
        r[20] = (byte)now.Day;
        r[21] = (byte)now.Hour;
        r[22] = (byte)now.Minute;
        r[23] = (byte)now.Second;
        r[25] = (byte)(isDirectory ? 2 : 0);
        Both16(r.AsSpan(28), 1);
        r[32] = (byte)name.Length;
        name.CopyTo(r, 33);
        return r;
    }

    private static void PathTable(Span<byte> s, int rootExtent, bool littleEndian)
    {
        s[0] = 1;
        if (littleEndian) BinaryPrimitives.WriteInt32LittleEndian(s[2..], rootExtent);
        else BinaryPrimitives.WriteInt32BigEndian(s[2..], rootExtent);
        if (littleEndian) BinaryPrimitives.WriteInt16LittleEndian(s[6..], 1);
        else BinaryPrimitives.WriteInt16BigEndian(s[6..], 1);
    }

    private static void Descriptor(Span<byte> d, byte type, string volumeId, int rootExtent, int pathL, int pathM, int totalSectors, DateTime now, bool joliet)
    {
        d[0] = type;
        "CD001"u8.CopyTo(d[1..]);
        d[6] = 1;
        Text(d, joliet, 8, 32, "LINUX");
        Text(d, joliet, 40, 32, volumeId);
        Both32(d[80..], totalSectors);
        if (joliet) { d[88] = 0x25; d[89] = 0x2F; d[90] = 0x45; }
        Both16(d[120..], 1);
        Both16(d[124..], 1);
        Both16(d[128..], Sector);
        Both32(d[132..], 10);
        BinaryPrimitives.WriteInt32LittleEndian(d[140..], pathL);
        BinaryPrimitives.WriteInt32BigEndian(d[148..], pathM);
        Record([0], rootExtent, Sector, isDirectory: true, now).CopyTo(d[156..]);
        Text(d, joliet, 190, 128, volumeId);
        Text(d, joliet, 318, 128, "");
        Text(d, joliet, 446, 128, "");
        Text(d, joliet, 574, 128, "VM QONTROL");
        Text(d, joliet, 702, 37, "");
        Text(d, joliet, 739, 37, "");
        Text(d, joliet, 776, 37, "");
        var stamp = Encoding.ASCII.GetBytes(now.ToString("yyyyMMddHHmmss00", System.Globalization.CultureInfo.InvariantCulture));
        stamp.CopyTo(d[813..]);
        stamp.CopyTo(d[830..]);
        d.Slice(847, 16).Fill((byte)'0');
        d.Slice(864, 16).Fill((byte)'0');
        d[881] = 1;
    }

    private static void Text(Span<byte> d, bool joliet, int offset, int length, string text)
    {
        var span = d.Slice(offset, length);
        if (joliet)
        {
            for (var i = 0; i + 1 < length; i += 2) { span[i] = 0; span[i + 1] = (byte)' '; }
            var b = Encoding.BigEndianUnicode.GetBytes(text);
            b.AsSpan(0, Math.Min(b.Length, length & ~1)).CopyTo(span);
        }
        else
        {
            span.Fill((byte)' ');
            var b = Encoding.ASCII.GetBytes(text);
            b.AsSpan(0, Math.Min(b.Length, length)).CopyTo(span);
        }
    }

    private static void Both16(Span<byte> s, int v)
    {
        BinaryPrimitives.WriteInt16LittleEndian(s, (short)v);
        BinaryPrimitives.WriteInt16BigEndian(s[2..], (short)v);
    }

    private static void Both32(Span<byte> s, int v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(s, v);
        BinaryPrimitives.WriteInt32BigEndian(s[4..], v);
    }
}
