using System.Text.Json.Serialization;

namespace VmQontrol.Core.Machines;

/// <summary>
/// One virtual machine's settings, saved as <c>vm.json</c> in the machine's folder. A <c>null</c> setting means "the
/// recommended value": the guest profile and the host decide it at power on, so a machine moved to a bigger computer
/// gets the bigger defaults. Relative file names are relative to the machine's folder.
/// </summary>
public sealed record MachineConfig
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; init; } = CurrentSchema;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Name { get; init; }

    public string Notes { get; init; } = "";

    public GuestConfig Guest { get; init; } = new();

    public FirmwareConfig Firmware { get; init; } = new();

    public CpuConfig Cpu { get; init; } = new();

    /// <summary>Memory in MiB; <c>null</c> = the recommended amount for this host.</summary>
    public long? MemoryMiB { get; init; }

    public List<DiskConfig> Disks { get; init; } = [];

    public List<MediaConfig> Media { get; init; } = [];

    public List<NicConfig> Nics { get; init; } = [new()];

    public DisplayConfig Display { get; init; } = new();

    public AudioConfig Audio { get; init; } = new();

    public InputConfig Input { get; init; } = new();

    public UsbConfig Usb { get; init; } = new();

    public bool Tpm { get; init; }

    /// <summary>Serial port as a socket for the console tab; <c>null</c> = the profile's choice (off for Windows and macOS).</summary>
    public bool? Serial { get; init; }

    /// <summary>Extra QEMU arguments, appended last, one argument per entry.</summary>
    public List<string> ExtraArgs { get; init; } = [];

    /// <summary>Where the machine came from when it was not made here; an imported machine's files stay where they were.</summary>
    public ImportSource? ImportedFrom { get; init; }
}

/// <summary>The guest operating system. <see cref="Os"/> uses quickemu's names (linux, windows, macos, freebsd...).</summary>
public sealed record GuestConfig
{
    public string Os { get; init; } = "linux";

    /// <summary>The release where it changes the hardware (macOS: "sonoma", Windows: "11"); empty otherwise.</summary>
    public string Release { get; init; } = "";

    /// <summary>The osinfo-db identifier when known (e.g. "http://microsoft.com/win/11"), for recommendations.</summary>
    public string? OsInfoId { get; init; }
}

public enum FirmwareKind
{
    Uefi,
    Bios,
}

public sealed record FirmwareConfig
{
    public FirmwareKind Kind { get; init; } = FirmwareKind.Uefi;

    public bool SecureBoot { get; init; }

    /// <summary>A firmware code file of the machine's own (macOS ships one); <c>null</c> = the host's OVMF.</summary>
    public string? CodeFile { get; init; }

    /// <summary>The machine's own UEFI variable store, copied from the host's template on first power on.</summary>
    public string VarsFile { get; init; } = "OVMF_VARS.fd";
}

public sealed record CpuConfig
{
    /// <summary>Logical processors for the guest; <c>null</c> = recommended for this host.</summary>
    public int? Cores { get; init; }

    /// <summary>A QEMU CPU model; <c>null</c> = the profile's choice (host passthrough for most guests).</summary>
    public string? Model { get; init; }
}

public enum DiskBus
{
    Auto,
    Virtio,
    Sata,
    Ide,
}

public sealed record DiskConfig
{
    public required string File { get; init; }

    public string Format { get; init; } = "qcow2";

    public DiskBus Bus { get; init; } = DiskBus.Auto;

    /// <summary>Size for a disk not made yet, in GiB.</summary>
    public long SizeGiB { get; init; } = 64;

    /// <summary>qemu-img preallocation: off, metadata, falloc or full.</summary>
    public string Preallocation { get; init; } = "off";
}

public enum MediaRole
{
    /// <summary>The installer the machine boots from until an operating system is installed.</summary>
    Installer,

    /// <summary>A second disc kept in the drive (drivers, guest tools).</summary>
    Drivers,

    /// <summary>The answer file disc for an unattended Windows install.</summary>
    Unattended,

    /// <summary>A macOS recovery image (attached as a raw disk).</summary>
    Recovery,

    /// <summary>A macOS boot loader (OpenCore).</summary>
    Bootloader,

    Floppy,
}

public enum Connect
{
    Always,
    Never,

    /// <summary>Connected while the system disk is still unused, as quickemu does: the installer, until it has installed.</summary>
    UntilInstalled,
}

public sealed record MediaConfig
{
    public required MediaRole Role { get; init; }

    public required string File { get; init; }

    public Connect Connect { get; init; } = Connect.Always;
}

public enum NetMode
{
    /// <summary>NAT through QEMU's user network: the guest reaches out, the host reaches in through forwarded ports.</summary>
    User,

    /// <summary>Like <see cref="User"/> but the guest cannot reach outside the host.</summary>
    Restricted,

    /// <summary>On the host's own network through a bridge.</summary>
    Bridge,

    None,
}

public sealed record PortForward(string Protocol, int HostPort, int GuestPort);

public sealed record NicConfig
{
    public NetMode Mode { get; init; } = NetMode.User;

    /// <summary>The adapter model; <c>null</c> = the profile's choice.</summary>
    public string? Model { get; init; }

    public string? Mac { get; init; }

    public string? Bridge { get; init; }

    /// <summary>Forward a host port to the guest's SSH port; the first free one from 22220 when <see cref="SshHostPort"/> is unset.</summary>
    public bool SshForward { get; init; } = true;

    public int? SshHostPort { get; init; }

    public List<PortForward> Forwards { get; init; } = [];
}

public enum DisplayBackend
{
    /// <summary>A SPICE server on a local socket, shown by an external viewer.</summary>
    Spice,
    SpiceApp,
    Gtk,
    Sdl,

    /// <summary>No screen at all (servers, tests).</summary>
    None,
}

public sealed record DisplayConfig
{
    public DisplayBackend Backend { get; init; } = DisplayBackend.Spice;

    /// <summary>The graphics adapter; <c>null</c> = the profile's choice.</summary>
    public string? Device { get; init; }

    /// <summary>3D acceleration (VirGL); <c>null</c> = on when the host has a GPU render node.</summary>
    public bool? Gl { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>The viewer program: spicy, remote-viewer, or none.</summary>
    public string Viewer { get; init; } = "auto";
}

public sealed record AudioConfig
{
    /// <summary>The sound card; <c>null</c> = the profile's choice; "none" = no sound card.</summary>
    public string? Device { get; init; }
}

public sealed record InputConfig
{
    /// <summary>usb, ps2 or virtio; <c>null</c> = the profile's choice.</summary>
    public string? Keyboard { get; init; }

    /// <summary>tablet, usb, ps2 or virtio; <c>null</c> = the profile's choice.</summary>
    public string? Mouse { get; init; }

    public string Layout { get; init; } = "en-us";
}

public sealed record UsbDevice(string VendorId, string ProductId, string? Name = null);

public sealed record UsbConfig
{
    /// <summary>ehci (USB 2), xhci (USB 3) or none; <c>null</c> = the profile's choice.</summary>
    public string? Controller { get; init; }

    /// <summary>Host devices handed to the guest at power on.</summary>
    public List<UsbDevice> Passthrough { get; init; } = [];
}

public sealed record ImportSource(string Kind, string Path);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MachineConfig))]
internal sealed partial class MachineJsonContext : JsonSerializerContext;
