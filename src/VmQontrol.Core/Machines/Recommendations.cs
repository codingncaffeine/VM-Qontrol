using VmQontrol.Core.Hosts;
using VmQontrol.Core.OsInfo;

namespace VmQontrol.Core.Machines;

/// <summary>A recommended value with the reason, in words, for the wizard and the settings dialog.</summary>
public sealed record Recommended<T>(T Value, string Reason, T Minimum);

public sealed record MachineRecommendations(Recommended<long> MemoryMiB, Recommended<int> Cores, Recommended<long> DiskGiB);

/// <summary>
/// What a new machine should get on this computer: memory, processors and disk, each with why. Sources: the operating
/// system's own requirements (osinfo-db, and the minimums Windows 11 and macOS enforce), quickemu's bands for the
/// host's size, and the host's headroom (the machine never gets more than half the memory).
/// </summary>
public static class Recommendations
{
    /// <summary>
    /// Secure Boot is on for Windows 11 when this computer has a Secure Boot firmware build: Setup on current Windows 11
    /// discs (found on build 26300, 2026-11-02) refuses a PC without it, and the answer file's LabConfig switches no
    /// longer get past that check. quickemu still turns it off; virt-manager and Boxes turn it on.
    /// </summary>
    public static bool SecureBoot(GuestConfig guest, HostCapabilities host) =>
        guest.Os == "windows" && guest.Release == "11" && Hosts.FirmwareLocator.Find(host.FirmwareFiles, true) is not null;

    /// <param name="displayName">How the system is named to the person ("Windows 10"); osinfo-db's name when <c>null</c>.</param>
    /// <summary>The recommendations for <paramref name="guest"/> on <paramref name="host"/>, each with its reason.</summary>
    /// <param name="guest">The guest profile.</param>
    /// <param name="os">The osinfo-db entry, when known.</param>
    /// <param name="host">This computer.</param>
    public static MachineRecommendations For(GuestConfig guest, OsEntry? os, HostCapabilities host, string? displayName = null)
    {
        var name = displayName ?? os?.Name ?? guest.Os;
        OsResources? Res(IReadOnlyDictionary<string, OsResources>? d) => d is null ? null : d.GetValueOrDefault("x86_64") ?? d.GetValueOrDefault("all");
        var osRecommended = Res(os?.Recommended);
        var osMinimum = Res(os?.Minimum);

        // Memory: quickemu's band for this host, raised to what the system recommends, capped at half the host.
        var band = 1024L * host.MemoryGB switch { >= 128 => 32, >= 64 => 16, >= 16 => 8, >= 8 => 4, _ => 2 };
        var minimumMiB = guest.Os switch
        {
            "windows" when guest.Release == "11" => 4096L,
            "windows" or "windows-server" => 2048L,
            "macos" => 8192L,
            _ => osMinimum?.RamBytes is { } r ? r / (1024 * 1024) : 512,
        };
        var wanted = Math.Max(band, osRecommended?.RamBytes is { } rr ? rr / (1024 * 1024) : 0);
        // Half of this computer, in whole gigabytes (a 16 GB computer offers 8 GB, never 7.8).
        var cap = Math.Max(minimumMiB, host.MemoryGB / 2 * 1024L);
        var memory = Math.Max(minimumMiB, Math.Min(wanted, cap));
        var memoryReason = memory == minimumMiB && wanted > memory
            ? $"{name} needs at least {Gb(minimumMiB)}; this computer cannot comfortably spare more."
            : $"Comfortable for {name} on a computer with {host.MemoryGB} GB, leaving at least half for the computer itself.";

        // Processors: quickemu's band, at least what the system needs (Windows 11 refuses to install on one).
        var cores = host.LogicalCpus switch { >= 32 => 16, >= 16 => 8, >= 8 => 4, >= 4 => 2, _ => 1 };
        var minCores = guest.Os == "windows" && guest.Release == "11" ? 2 : osMinimum?.Cpus ?? 1;
        cores = Math.Max(cores, minCores);
        if (guest.Os == "macos")
        {
            var p = 1;
            while (p * 2 <= cores) p *= 2;
            cores = p;
        }

        var coresReason = guest.Os == "macos"
            ? "macOS only starts with a power-of-two number of processors."
            : $"Half of this computer's {host.LogicalCpus} processors, so it stays responsive while the machine runs.";

        // Disk: what the system recommends, with room to work; the file only grows as it is used.
        var minDisk = guest.Os switch { "windows" when guest.Release == "11" => 64L, "windows" or "windows-server" => 32L, "macos" => 64L, _ => osMinimum?.StorageBytes is { } s ? Math.Max(8, s / (1L << 30)) : 8 };
        var disk = guest.Os switch
        {
            "windows" or "windows-server" => 80L,
            "macos" => 128L,
            _ => Math.Max(32, osRecommended?.StorageBytes is { } sb ? sb / (1L << 30) * 2 : 32),
        };
        disk = Math.Max(disk, minDisk);
        var diskReason = $"Room for {name}, updates and your programs. The disk file starts small and grows only as the machine writes to it.";

        return new MachineRecommendations(new(memory, memoryReason, minimumMiB), new(cores, coresReason, minCores), new(disk, diskReason, minDisk));
    }

    private static string Gb(long mib) => mib % 1024 == 0 ? $"{mib / 1024} GB" : $"{mib} MB";
}
