using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Import;

/// <summary>A disk of a machine made elsewhere: its file (any format qemu-img reads) and the bus the guest knows it on.</summary>
public sealed record ForeignDisk(string Path, DiskBus Bus);

/// <summary>
/// A machine made by another program (VMware, VirtualBox, an OVF appliance, libvirt) or a bare disk, read but not touched:
/// what VM Qontrol will make of it, and every guess it had to make, in words (D30).
/// </summary>
public sealed record ForeignMachine
{
    /// <summary>"VMware", "VirtualBox", "OVF", "libvirt", "disk".</summary>
    public required string Kind { get; init; }

    /// <summary>The file that was read (.vmx, .vbox, .ovf/.ova, .xml, the disk).</summary>
    public required string Source { get; init; }

    public required string Name { get; init; }

    public GuestConfig Guest { get; init; } = new();

    /// <summary>What the source called the system ("windows9-64", "Ubuntu_64"), for the notes.</summary>
    public string SourceOs { get; init; } = "";

    public long? MemoryMiB { get; init; }

    public int? Cores { get; init; }

    public FirmwareKind Firmware { get; init; } = FirmwareKind.Bios;

    public bool SecureBoot { get; init; }

    public bool Tpm { get; init; }

    public List<ForeignDisk> Disks { get; init; } = [];

    /// <summary>Disc images in its drives (kept as they are, not copied).</summary>
    public List<string> Discs { get; init; } = [];

    public List<NicConfig> Nics { get; init; } = [];

    /// <summary>What did not carry over or was guessed; shown before importing.</summary>
    public List<string> Notes { get; init; } = [];

    /// <summary>What stops the import, with what to do about it (e.g. VirtualBox snapshots); empty = it can go ahead.</summary>
    public List<string> Problems { get; init; } = [];

    /// <summary>Files the reader unpacked to a temporary folder (an .ova); deleted after the import.</summary>
    public string? TemporaryFolder { get; init; }
}

/// <summary>Names a system from the identifiers VMware, VirtualBox, OVF and libosinfo use, as VM Qontrol's guest names.</summary>
public static class ForeignGuest
{
    public static GuestConfig From(string id)
    {
        var s = id.ToLowerInvariant();
        // Before Windows: "darwin" holds "win".
        if (s.Contains("darwin", StringComparison.Ordinal) || s.Contains("macos", StringComparison.Ordinal) || s.Contains("osx", StringComparison.Ordinal))
            return new GuestConfig { Os = "macos" };
        if (s.Contains("win", StringComparison.Ordinal))
        {
            var server = s.Contains("srv", StringComparison.Ordinal) || s.Contains("server", StringComparison.Ordinal) || s.Contains("longhorn", StringComparison.Ordinal);
            // VMware: windows9 = 10, windows11; VirtualBox: Windows10_64, Windows11_64; OVF: windows9_64Guest, windows11_64Guest.
            var release = s.Contains("11", StringComparison.Ordinal) ? "11"
                : s.Contains("10", StringComparison.Ordinal) || s.Contains("windows9", StringComparison.Ordinal) ? "10"
                : s.Contains("windows8", StringComparison.Ordinal) || s.Contains("win8", StringComparison.Ordinal) ? "8"
                : s.Contains("windows7", StringComparison.Ordinal) || s.Contains("win7", StringComparison.Ordinal) ? "7" : "";
            return new GuestConfig { Os = server ? "windows-server" : "windows", Release = server ? "" : release };
        }

        if (s.Contains("freebsd", StringComparison.Ordinal)) return new GuestConfig { Os = "freebsd" };
        if (s.Contains("solaris", StringComparison.Ordinal)) return new GuestConfig { Os = "solaris" };
        if (s.Contains("dos", StringComparison.Ordinal) && !s.Contains("dosemu", StringComparison.Ordinal)) return new GuestConfig { Os = "freedos" };
        // Linux by name (ubuntu-64, Fedora_64, rhel9_64Guest, debian12-64, http://ubuntu.com/ubuntu/24.04...), and anything else.
        return new GuestConfig { Os = "linux", OsInfoId = id.StartsWith("http", StringComparison.Ordinal) ? id : null };
    }
}
