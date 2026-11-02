using VmQontrol.Core.Hosts;

namespace VmQontrol.Core.Qemu;

/// <summary>Where a running machine's sockets and process id live: a short path, because a unix socket path is limited to 107 bytes.</summary>
public sealed record RuntimePaths(string Directory)
{
    /// <summary>The supervisor's QMP connection (held for the machine's life by the app).</summary>
    public string Qmp => Path.Combine(Directory, "qmp.sock");

    /// <summary>A second QMP socket for short commands (vmq), because one QMP socket serves one client at a time.</summary>
    public string Control => Path.Combine(Directory, "control.sock");

    /// <summary>Written while the machine runs: who started it and from where.</summary>
    public string Lock => Path.Combine(Directory, "lock.json");

    public string Spice => Path.Combine(Directory, "spice.sock");

    public string Agent => Path.Combine(Directory, "agent.sock");

    public string Serial => Path.Combine(Directory, "serial.sock");

    public string Tpm => Path.Combine(Directory, "swtpm.sock");

    public string Pid => Path.Combine(Directory, "qemu.pid");

    /// <summary>The virtiofsd socket for a shared folder.</summary>
    public string Share(int index) => Path.Combine(Directory, $"fs{index}.sock");
}

/// <summary>Everything about the moment of power on that is not in the machine's settings.</summary>
public sealed record LaunchContext
{
    public required HostCapabilities Host { get; init; }

    /// <summary>The machine's folder: relative file names resolve here, and TPM state lives here.</summary>
    public required string MachineDirectory { get; init; }

    public required RuntimePaths Runtime { get; init; }

    /// <summary>The NAT network inside every machine ("10.0.5.0/24"); <c>null</c> = QEMU's own 10.0.2.0/24 (the virtual network editor).</summary>
    public string? NatSubnet { get; init; }

    /// <summary>The host port for the guest's SSH, chosen free at power on.</summary>
    public int SshHostPort { get; init; } = 22220;

    /// <summary>Whether the system disk already holds an installed system: installers marked "until installed" stay out.</summary>
    public bool SystemDiskUsed { get; init; }

    /// <summary>No screen, no sound, no viewer: tests and background work. Never anything audible.</summary>
    public bool Headless { get; init; }

    /// <summary>The host sound backend for GTK and SDL windows (pipewire, pa or alsa); SPICE carries sound itself.</summary>
    public string HostAudio { get; init; } = "pipewire";

    /// <summary>Show the firmware's boot menu this once (power on to firmware on a BIOS machine).</summary>
    public bool FirmwareMenu { get; init; }

    public string Share { get; init; } = "/usr/share";

    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>The virtiofsd program, when installed (shared folders need it).</summary>
    public string? Virtiofsd { get; init; } = new[] { "/usr/lib/virtiofsd", "/usr/libexec/virtiofsd", "/usr/bin/virtiofsd" }.FirstOrDefault(File.Exists);

    public Func<string, string> FirmwareFormat { get; init; } = FirmwareLocator.FormatOf;
}

/// <summary>A virtiofsd process serving one shared folder, started before QEMU.</summary>
public sealed record VirtiofsdLaunch(IReadOnlyList<string> Args, string Socket);

/// <summary>The swtpm process a machine with a TPM needs, started before QEMU.</summary>
public sealed record SwtpmLaunch(IReadOnlyList<string> Args, string Socket, string StateDirectory);

/// <summary>The command for one power on, and what the caller must do or say first.</summary>
public sealed record QemuLaunch
{
    public required IReadOnlyList<string> Args { get; init; }

    public SwtpmLaunch? Swtpm { get; init; }

    public IReadOnlyList<VirtiofsdLaunch> Shares { get; init; } = [];

    /// <summary>The UEFI variable store to make from the host's template before the first power on (source, destination).</summary>
    public (string Template, string Destination)? VarsToCreate { get; init; }

    /// <summary>Reasons the machine cannot start as configured; empty when it can.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Cores { get; init; }

    public long MemoryMiB { get; init; }
}
