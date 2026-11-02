namespace VmQontrol.Core.Machines;

/// <summary>Whether a system disk already holds an installed system.</summary>
public static class DiskState
{
    /// <summary>quickemu's threshold: a fresh qcow2 is under 197632 × 8 bytes; anything larger has been written to.</summary>
    public const long UnusedBelowBytes = 197632L * 8;

    public static bool IsUsed(string disk) => File.Exists(disk) && new FileInfo(disk).Length > UnusedBelowBytes;
}
