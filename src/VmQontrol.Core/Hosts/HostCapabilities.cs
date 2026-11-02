namespace VmQontrol.Core.Hosts;

/// <summary>
/// What the host offers, read once by <see cref="HostProbe"/> (or written by a test): the command builder takes every host
/// fact from here and reads nothing itself, so the same machine always turns into the same command on the same host.
/// </summary>
public sealed record HostCapabilities
{
    /// <summary>"GenuineIntel", "AuthenticAMD", "HygonGenuine"...</summary>
    public string CpuVendor { get; init; } = "";

    public int LogicalCpus { get; init; } = 1;

    public bool SmtOn { get; init; }

    public int Sockets { get; init; } = 1;

    /// <summary>Total memory in decimal gigabytes, as <c>free --giga</c> reports it (quickemu's bands use this).</summary>
    public int MemoryGB { get; init; }

    public IReadOnlySet<string> CpuFlags { get; init; } = new HashSet<string>();

    public Version QemuVersion { get; init; } = new(0, 0, 0);

    /// <summary>The device names <c>qemu-system-x86_64 -device help</c> lists.</summary>
    public IReadOnlySet<string> QemuDevices { get; init; } = new HashSet<string>();

    public bool KvmUsable { get; init; }

    /// <summary>The GPU render node for VirGL (/dev/dri/renderD128), or <c>null</c> when there is none.</summary>
    public string? RenderNode { get; init; }

    /// <summary>The firmware files present, for <see cref="FirmwareLocator"/>.</summary>
    public IReadOnlySet<string> FirmwareFiles { get; init; } = new HashSet<string>();

    public bool IsIntel => CpuVendor == "GenuineIntel";

    public bool IsAmd => CpuVendor == "AuthenticAMD";

    /// <summary>quickemu's check: AMD reports invariant TSC as constant_tsc.</summary>
    public bool HasFlag(string flag) => CpuFlags.Contains(flag) || (flag == "invtsc" && IsAmd && CpuFlags.Contains("constant_tsc"));
}
