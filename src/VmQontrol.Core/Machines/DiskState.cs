using System.Buffers.Binary;
using System.Text;

namespace VmQontrol.Core.Machines;

/// <summary>Whether a system disk already holds an installed system, and what it is layered on.</summary>
public static class DiskState
{
    /// <summary>quickemu's threshold: a fresh qcow2 is under 197632 × 8 bytes; anything larger has been written to.</summary>
    public const long UnusedBelowBytes = 197632L * 8;

    /// <summary>
    /// Written to, or layered on a base that holds a system (a linked clone, or a machine whose disk was frozen for one):
    /// such an overlay starts empty, yet the machine it belongs to is installed.
    /// </summary>
    public static bool IsUsed(string disk) => File.Exists(disk) && (new FileInfo(disk).Length > UnusedBelowBytes || BackingFile(disk) is not null);

    /// <summary>
    /// The file a qcow2 disk is layered on, resolved to a full path (relative names are relative to the disk's folder);
    /// <c>null</c> for a disk that stands alone, is not qcow2, or cannot be read. Reads the header only (qcow2 spec §2).
    /// </summary>
    public static string? BackingFile(string disk)
    {
        try
        {
            using var f = new FileStream(disk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[20];
            if (f.Read(header) < header.Length || BinaryPrimitives.ReadUInt32BigEndian(header) != 0x514649FB) return null;
            var offset = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            if (offset == 0 || size is 0 or > 1023) return null;
            var name = new byte[size];
            f.Position = (long)offset;
            f.ReadExactly(name);
            var backing = Encoding.UTF8.GetString(name);
            return backing.Contains(':', StringComparison.Ordinal) && !backing.StartsWith('/') ? backing
                : Path.GetFullPath(backing, Path.GetDirectoryName(Path.GetFullPath(disk))!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The disk and every file under it, nearest first (stops at a loop or after 64 layers).</summary>
    public static IReadOnlyList<string> Chain(string disk)
    {
        var chain = new List<string> { Path.GetFullPath(disk) };
        while (chain.Count < 64 && BackingFile(chain[^1]) is { } next && !chain.Contains(next)) chain.Add(next);
        return chain;
    }
}
