using System.Text.Json.Serialization;

using VmQontrol.Core.Qemu;

namespace VmQontrol.Core.Machines;

/// <summary>
/// One virtual machine's settings, saved as <c>vm.json</c>. Properties have setters, not init accessors, on purpose: the
/// source-generated JSON reader builds init-only properties through one object initializer and leaves every property
/// missing from the file null, so a file from an older version (or written by hand) would lose its defaults. in the machine's folder. A <c>null</c> setting means "the
/// recommended value": the guest profile and the host decide it at power on, so a machine moved to a bigger computer
/// gets the bigger defaults. Relative file names are relative to the machine's folder.
/// </summary>
public sealed record MachineConfig
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string Name { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>
    /// A template, as Workstation's: never started, only cloned, so every machine made from it begins in the same state.
    /// Linked clones of a template share its disks.
    /// </summary>
    public bool IsTemplate { get; set; }

    public GuestConfig Guest { get; set; } = new();

    public FirmwareConfig Firmware { get; set; } = new();

    public CpuConfig Cpu { get; set; } = new();

    /// <summary>Memory in MiB; <c>null</c> = the recommended amount for this host.</summary>
    public long? MemoryMiB { get; set; }

    public List<DiskConfig> Disks { get; set; } = [];

    public List<MediaConfig> Media { get; set; } = [];

    public List<NicConfig> Nics { get; set; } = [new()];

    public DisplayConfig Display { get; set; } = new();

    public AudioConfig Audio { get; set; } = new();

    public InputConfig Input { get; set; } = new();

    public UsbConfig Usb { get; set; } = new();

    public bool Tpm { get; set; }

    /// <summary>Serial port as a socket for the console tab; <c>null</c> = the profile's choice (off for Windows and macOS).</summary>
    public bool? Serial { get; set; }

    /// <summary>Drives of this computer connected to the machine at power on (a whole disk, a partition or a disc drive).</summary>
    public List<HostDriveConfig> HostDrives { get; set; } = [];

    /// <summary>Serial ports of this computer the machine uses, as its next COM ports (connected at power on).</summary>
    public List<HostSerialConfig> HostSerialPorts { get; set; } = [];

    /// <summary>Folders of this computer shared with the machine (virtiofs).</summary>
    public List<ShareConfig> Shares { get; set; } = [];

    /// <summary>Boot order, boot menu and extra devices; every default is quickemu's.</summary>
    public DevicesConfig Devices { get; set; } = new();

    public AutoProtectConfig AutoProtect { get; set; } = new();

    /// <summary>Pinning, hugepages, I/O threads, PCI passthrough and shared memory for Looking Glass; all off by default.</summary>
    public PerformanceConfig Performance { get; set; } = new();

    /// <summary>Boot a kernel directly (an installer's kernel with its answer-file options); <c>null</c> = boot normally.</summary>
    public KernelBootConfig? KernelBoot { get; set; }

    /// <summary>Extra QEMU arguments, appended last, one argument per entry.</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>Where the machine came from when it was not made here; an imported machine's files stay where they were.</summary>
    public ImportSource? ImportedFrom { get; set; }

    /// <summary>Set when VM Qontrol wrote this machine's answer file: the machine installs its system by itself, and the
    /// machine page says so until the guest reports it has finished (<see cref="Install.InstallSignal"/>).</summary>
    public EasyInstallConfig? EasyInstall { get; set; }

    /// <summary>What happens to the machine's changes at power off (<see cref="DiscardSession"/>).</summary>
    public DiscardMode OnPowerOff { get; set; } = DiscardMode.Keep;

    /// <summary>The machine this one was copied from by an import (VMware, VirtualBox, OVF, libvirt, a disk); the source was only read.</summary>
    public ImportSource? CopiedFrom { get; set; }
}

/// <summary>What Easy Install was asked to do, for the "Installing for you" card.</summary>
public sealed record EasyInstallConfig
{
    /// <summary>"Windows 11 Pro", "Ubuntu": what the card says is being installed.</summary>
    public string System { get; set; } = "";

    /// <summary>The account the guest signs in to, as the person typed it.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Windows: Lean Windows was on.</summary>
    public bool Lean { get; set; }
}

/// <summary>The guest operating system. <see cref="Os"/> uses quickemu's names (linux, windows, macos, freebsd...).</summary>
public sealed record GuestConfig
{
    public string Os { get; set; } = "linux";

    /// <summary>The release where it changes the hardware (macOS: "sonoma", Windows: "11"); empty otherwise.</summary>
    public string Release { get; set; } = "";

    /// <summary>The osinfo-db identifier when known (e.g. "http://microsoft.com/win/11"), for recommendations.</summary>
    public string? OsInfoId { get; set; }

    /// <summary>The account Easy Install made in the guest, as the guest spells it; <c>null</c> when unknown. The SSH hint uses it.</summary>
    public string? Account { get; set; }

    /// <summary>The command that opens a shell in the guest through the forwarded SSH port.</summary>
    public string SshCommand(int port) => $"ssh -p {port} {SshAccount}@localhost";

    /// <summary>
    /// The account for the SSH command, when it is a plain account name: anything else (a leading '-', spaces, shell
    /// characters from a hand-edited or imported file) would become an option or a second argument when the command is
    /// split and run, so "user" stands in.
    /// </summary>
    private string SshAccount => Account is { Length: > 0 and <= 64 } a && !a.StartsWith('-') && a.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? a : "user";

    /// <summary>SSH over vsock (systemd's ssh proxy on this computer; a guest with systemd 256 or newer listens there).</summary>
    public string SshVsockCommand(uint cid) => $"ssh {SshAccount}@vsock/{cid}";
}

public enum FirmwareKind
{
    Uefi,
    Bios,
}

public sealed record FirmwareConfig
{
    public FirmwareKind Kind { get; set; } = FirmwareKind.Uefi;

    public bool SecureBoot { get; set; }

    /// <summary>A firmware code file of the machine's own (macOS ships one); <c>null</c> = the host's OVMF.</summary>
    public string? CodeFile { get; set; }

    /// <summary>The machine's own UEFI variable store, copied from the host's template on first power on.</summary>
    public string VarsFile { get; set; } = "OVMF_VARS.fd";
}

public sealed record CpuConfig
{
    /// <summary>Logical processors for the guest; <c>null</c> = recommended for this host.</summary>
    public int? Cores { get; set; }

    /// <summary>A QEMU CPU model; <c>null</c> = the profile's choice (host passthrough for most guests).</summary>
    public string? Model { get; set; }

    /// <summary>Processors can be added while the machine runs, up to this many; <c>null</c> = not (quickemu's command line).</summary>
    public int? MaxCores { get; set; }

    /// <summary>Nested virtualization: the guest can run virtual machines of its own. <c>null</c> = as this computer's KVM
    /// allows (quickemu's line: host passthrough carries vmx/svm whenever KVM's nested option is on); false = hidden.</summary>
    public bool? Nested { get; set; }
}

/// <summary>A folder of this computer the machine sees; <see cref="Tag"/> is the name the guest mounts it by.</summary>
public sealed record ShareConfig
{
    public required string Path { get; set; }

    public required string Tag { get; set; }

    public bool ReadOnly { get; set; }
}

/// <summary>Devices and boot behavior beyond the main hardware. <c>null</c> = quickemu's choice.</summary>
public sealed record DevicesConfig
{
    /// <summary>A random-number generator the guest can draw on (faster boots and key generation); on by default.</summary>
    public bool? Rng { get; set; }

    /// <summary>Memory ballooning (the guest can hand unused memory back); the profile decides by default.</summary>
    public bool? Balloon { get; set; }

    /// <summary>A watchdog and what it does when the guest stops answering it: reset, poweroff, pause; <c>null</c> = none.</summary>
    public string? Watchdog { get; set; }

    /// <summary>The guest reports its crashes (pvpanic), so the machine shows "Crashed" instead of hanging silently.</summary>
    public bool CrashReporting { get; set; }

    /// <summary>BIOS boot order as letters: c disk, d CD/DVD, n network (UEFI machines keep their order in the firmware).</summary>
    public string? BootOrder { get; set; }

    /// <summary>Show the firmware's boot menu for a few seconds at every power on.</summary>
    public bool BootMenu { get; set; }

    /// <summary>A VirtIO socket (vsock) between this computer and the guest, at the guest's own address
    /// (<see cref="QemuCommandBuilder.VsockCid"/>): SSH and tools reach it with no network at all.</summary>
    public bool Vsock { get; set; }
}

/// <summary>A kernel, initrd and kernel command line to boot instead of the firmware's boot order.</summary>
public sealed record KernelBootConfig
{
    public required string Kernel { get; set; }

    public string? Initrd { get; set; }

    public string Append { get; set; } = "";

    /// <summary>An installer's kernel is used only while the system disk is unused, like the installer disc.</summary>
    public Connect Connect { get; set; } = Connect.UntilInstalled;
}

public enum DiskBus
{
    Auto,
    Virtio,
    Sata,
    Ide,

    /// <summary>An NVMe drive (VMware Workstation's default for Windows 10 and 11; Windows has the driver built in).</summary>
    Nvme,

    /// <summary>VMware's paravirtual SCSI controller, for machines imported from VMware whose system has its driver.</summary>
    Pvscsi,

    /// <summary>VirtIO SCSI (virt-manager's choice for many disks): one controller for all of them, TRIM as SCSI UNMAP,
    /// disks plugged in while running; Windows needs the vioscsi driver (on the VirtIO driver disc).</summary>
    Scsi,
}

public sealed record DiskConfig
{
    public required string File { get; set; }

    public string Format { get; set; } = "qcow2";

    public DiskBus Bus { get; set; } = DiskBus.Auto;

    /// <summary>Size for a disk not made yet, in GiB.</summary>
    public long SizeGiB { get; set; } = 64;

    /// <summary>qemu-img preallocation: off, metadata, falloc or full.</summary>
    public string Preallocation { get; set; } = "off";

    /// <summary>Most megabytes a second the disk reads and writes together (VirtualBox's bandwidth group, virt-manager's
    /// iotune); <c>null</c> = as fast as this computer's drive.</summary>
    public int? LimitMBps { get; set; }

    /// <summary>Most reads and writes a second together; <c>null</c> = no limit.</summary>
    public int? LimitIops { get; set; }
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

    /// <summary>A cloud image's cloud-init seed (account, keys), on a read-only VirtIO disk (D39).</summary>
    CloudInit,
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
    public required MediaRole Role { get; set; }

    public required string File { get; set; }

    public Connect Connect { get; set; } = Connect.Always;
}

public enum NetMode
{
    /// <summary>NAT through QEMU's user network: the guest reaches out, the host reaches in through forwarded ports.</summary>
    User,

    /// <summary>Like <see cref="User"/> but the guest cannot reach outside the host.</summary>
    Restricted,

    /// <summary>On the host's own network through a bridge.</summary>
    Bridge,

    /// <summary>A private network between machines (a LAN segment), with no route to anything else.</summary>
    Segment,

    None,
}

public sealed record PortForward(string Protocol, int HostPort, int GuestPort);

public sealed record NicConfig
{
    public NetMode Mode { get; set; } = NetMode.User;

    /// <summary>The adapter model; <c>null</c> = the profile's choice.</summary>
    public string? Model { get; set; }

    public string? Mac { get; set; }

    public string? Bridge { get; set; }

    /// <summary>The private network's name: machines with the same name are on the same segment.</summary>
    public string? Segment { get; set; }

    /// <summary>Forward a host port to the guest's SSH port; the first free one from 22220 when <see cref="SshHostPort"/> is unset.</summary>
    public bool SshForward { get; set; } = true;

    public int? SshHostPort { get; set; }

    public List<PortForward> Forwards { get; set; } = [];

    /// <summary>The cable is plugged in at power on (Workstation's "Connect at power on"); false = the guest starts with
    /// its link down until Connect the network cable.</summary>
    public bool ConnectedAtPowerOn { get; set; } = true;
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
    public DisplayBackend Backend { get; set; } = DisplayBackend.Spice;

    /// <summary>The graphics adapter; <c>null</c> = the profile's choice.</summary>
    public string? Device { get; set; }

    /// <summary>3D acceleration (VirGL); <c>null</c> = on when the host has a GPU render node.</summary>
    public bool? Gl { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Monitors the guest sees (1 to 4); the viewer opens a window for each. <c>null</c> = one.</summary>
    public int? Monitors { get; set; }

    /// <summary>The viewer program: spicy, remote-viewer, or none.</summary>
    public string Viewer { get; set; } = "auto";

    /// <summary>The viewer opens full screen (the guest's resolution follows the screen with the guest tools).</summary>
    public bool FullScreen { get; set; }

    /// <summary>Copy and paste between this computer and the guest (through the guest tools); off isolates the guest.</summary>
    public bool CopyPaste { get; set; } = true;

    /// <summary>Files dragged onto the viewer window land in the guest (through the guest tools); off isolates the guest.</summary>
    public bool FileTransfer { get; set; } = true;
}

public sealed record AudioConfig
{
    /// <summary>The sound card; <c>null</c> = the profile's choice; "none" = no sound card.</summary>
    public string? Device { get; set; }

    /// <summary>
    /// The guest may hear this computer's microphone (through the viewer, while it shows the machine). Off = an
    /// output-only sound device, so no program in the guest can record.
    /// </summary>
    public bool Microphone { get; set; } = true;
}

public sealed record InputConfig
{
    /// <summary>usb, ps2 or virtio; <c>null</c> = the profile's choice.</summary>
    public string? Keyboard { get; set; }

    /// <summary>tablet, usb, ps2 or virtio; <c>null</c> = the profile's choice.</summary>
    public string? Mouse { get; set; }

    public string Layout { get; set; } = "en-us";
}

public sealed record UsbDevice(string VendorId, string ProductId, string? Name = null);

public sealed record UsbConfig
{
    /// <summary>ehci (USB 2), xhci (USB 3) or none; <c>null</c> = the profile's choice.</summary>
    public string? Controller { get; set; }

    /// <summary>Host devices handed to the guest at power on.</summary>
    public List<UsbDevice> Passthrough { get; set; } = [];
}

/// <summary>
/// A drive of this computer the machine uses: <see cref="Path"/> is its /dev/disk/by-id link, so it is the same drive
/// after a reboot or a replug. Read-only unless the person typed its name to allow writing.
/// </summary>
public sealed record HostDriveConfig
{
    public required string Path { get; set; }

    /// <summary>What the person saw when they chose it ("Kingston DataTraveler (removable disk, 29.3 GiB)").</summary>
    public string Name { get; set; } = "";

    public Devices.HostDriveKind Kind { get; set; } = Devices.HostDriveKind.Disk;

    public bool Writable { get; set; }
}

/// <summary>A serial port of this computer handed to the machine; <see cref="Path"/> is its by-id link or device.</summary>
public sealed record HostSerialConfig
{
    public required string Path { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>The settings that trade generality for speed (phase 10); every one is off until chosen.</summary>
public sealed record PerformanceConfig
{
    /// <summary>Each VirtIO disk gets an I/O thread of its own, so disk work leaves the processors' threads alone.</summary>
    public bool IoThreads { get; set; }

    /// <summary>This computer's processors the machine's processors run on, one per guest processor in order; empty = not pinned.</summary>
    public List<int> PinnedCpus { get; set; } = [];

    /// <summary>Where QEMU's own threads (devices, disks) run; empty = anywhere.</summary>
    public List<int> EmulatorCpus { get; set; } = [];

    /// <summary>"2M" or "1G": the machine's memory comes from this computer's reserved huge pages; <c>null</c> = ordinary memory.</summary>
    public string? HugePages { get; set; }

    /// <summary>Shared memory for Looking Glass (/dev/shm/looking-glass), in MiB; <c>null</c> = none.</summary>
    public int? LookingGlassMiB { get; set; }

    /// <summary>PCI devices handed to the machine (vfio-pci), by address ("0000:0b:00.0").</summary>
    public List<string> PciDevices { get; set; } = [];
}

public sealed record ImportSource(string Kind, string Path);

/// <summary>Workstation's AutoProtect: a snapshot every so often while the machine runs, keeping the newest few.</summary>
public sealed record AutoProtectConfig
{
    public bool Enabled { get; set; }

    /// <summary>Minutes of running between automatic snapshots.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>How many automatic snapshots to keep; older ones are deleted. Snapshots the person took are never touched.</summary>
    public int Keep { get; set; } = 3;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MachineConfig))]
internal sealed partial class MachineJsonContext : JsonSerializerContext;
