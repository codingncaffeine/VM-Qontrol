using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>A choice in a settings list: the stored value (null = automatic) and the words for it.</summary>
public sealed record Option(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A disc or floppy slot in the settings, editable.</summary>
public sealed partial class MediaRow(MediaConfig media) : ObservableObject
{
    public MediaRole Role { get; } = media.Role;

    [ObservableProperty] private string _file = media.File;
    [ObservableProperty] private Option _connect = SettingsViewModel.ConnectOptions.First(o => o.Value == media.Connect.ToString());

    public string Title => Role switch
    {
        MediaRole.Installer => "Installer disc",
        MediaRole.Drivers => "Second disc (drivers, tools)",
        MediaRole.Unattended => "Answer-file disc (Easy Install)",
        MediaRole.Recovery => "macOS recovery image",
        MediaRole.Bootloader => "macOS boot loader",
        _ => "Floppy disk",
    };

    public string FileName => Path.GetFileName(File);

    partial void OnFileChanged(string value) => OnPropertyChanged(nameof(FileName));

    public MediaConfig ToConfig() => new() { Role = Role, File = File, Connect = Enum.Parse<Connect>(Connect.Value!) };

    /// <summary>Where the disc is now, in words: in the drive, out because the system is installed, or at the next power on.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>The disc is in the running machine's drive: Eject takes it out now.</summary>
    [ObservableProperty]
    private bool _inDrive;

    public string EjectTip => InDrive ? "Take the disc out now, and keep it out from the next power on (after Save)" : "Keep this disc out of the drive from the next power on (after Save)";

    partial void OnInDriveChanged(bool value) => OnPropertyChanged(nameof(EjectTip));
}

/// <summary>A disk in the settings: its file, size and bus; the size only grows, and only while the machine is off.</summary>
public sealed partial class DiskRow(DiskConfig disk, string machineDirectory) : ObservableObject
{
    public DiskConfig Original { get; } = disk;

    public string File => Original.File;

    public string FileName => Path.GetFileName(Original.File);

    public string FullPath => Path.IsPathRooted(Original.File) ? Original.File : Path.Combine(machineDirectory, Original.File);

    [ObservableProperty] private decimal _sizeGb = disk.SizeGiB;
    [ObservableProperty] private Option _bus = SettingsViewModel.BusOptions.First(o => o.Value == disk.Bus.ToString());

    /// <summary>The smallest size allowed: a disk can grow, never shrink.</summary>
    public decimal MinimumGb => Original.SizeGiB;

    public string Used => System.IO.File.Exists(FullPath) ? $"{Formats.Bytes(new FileInfo(FullPath).Length)} used on this computer" : "Made at the next power on";

    public DiskConfig ToConfig() => Original with { SizeGiB = (long)SizeGb, Bus = Enum.Parse<DiskBus>(Bus.Value!) };
}

/// <summary>A shared folder in the settings.</summary>
public sealed partial class ShareRow(ShareConfig share) : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MountHint))] private string _path = share.Path;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MountHint))] private string _tag = share.Tag;
    [ObservableProperty] private bool _readOnly = share.ReadOnly;

    public string MountHint => $"sudo mkdir -p /mnt/{Tag} && sudo mount -t virtiofs {Tag} /mnt/{Tag}";

    public ShareConfig ToConfig() => new() { Path = Path, Tag = Tag.Trim().Length > 0 ? Tag.Trim() : "share", ReadOnly = ReadOnly };
}

/// <summary>A USB device the machine always connects (a rule): by its kind, at power on and whenever plugged in.</summary>
public sealed class UsbRuleRow(UsbDevice device)
{
    public UsbDevice Device { get; } = device;

    public string Name => Device.Name ?? "USB device";

    public string Id => $"{Device.VendorId}:{Device.ProductId}";
}

/// <summary>A drive of this computer the machine connects at every power on.</summary>
public sealed class HostDriveRow(HostDriveConfig drive)
{
    public HostDriveConfig Drive { get; } = drive;

    public string Name => Drive.Name.Length > 0 ? Drive.Name : Drive.Path;

    public string Detail => $"{(Drive.Writable ? "Writable" : "Read-only")} · {Drive.Path}";

    public Avalonia.Media.Geometry? Icon =>
        Avalonia.Application.Current?.TryGetResource(Drive.Kind == VmQontrol.Core.Devices.HostDriveKind.Optical ? "IconDisc" : "IconDisk", null, out var g) == true ? g as Avalonia.Media.Geometry : null;
}

/// <summary>A serial port of this computer: handed to the machine at power on or not, and whether this account may open it.</summary>
public sealed partial class SerialPortRow(VmQontrol.Core.Devices.HostSerialPort port, bool connected, SettingsViewModel owner) : ObservableObject
{
    public VmQontrol.Core.Devices.HostSerialPort Port { get; } = port;

    public string Name => Port.Description;

    public string Detail => $"{Port.DevicePath} · {(Port.IsUsb ? "USB adapter" : "on the board")}{(Port.Accessible ? "" : $" · needs the {Port.Group ?? "serial"} group")}";

    public bool NeedsAccess => !Port.Accessible;

    [ObservableProperty]
    private bool _connected = connected;

    [RelayCommand]
    private Task GiveAccessAsync() => owner.GiveSerialAccessAsync(this);
}

/// <summary>A forwarded port: a port on this computer that reaches a port in the machine.</summary>
public sealed partial class ForwardRow(PortForward forward) : ObservableObject
{
    [ObservableProperty] private string _protocol = forward.Protocol;
    [ObservableProperty] private decimal _hostPort = forward.HostPort;
    [ObservableProperty] private decimal _guestPort = forward.GuestPort;

    public PortForward ToConfig() => new(Protocol, (int)HostPort, (int)GuestPort);
}

public sealed record SettingsSection(string Key, string Title, string Group)
{
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["general"] = "IconInfo", ["memory"] = "IconMemory", ["processors"] = "IconCpu", ["disks"] = "IconDisk", ["media"] = "IconDisc",
        ["network"] = "IconNetwork", ["usb"] = "IconUsb", ["hostdrives"] = "IconDisk", ["sound"] = "IconSound", ["display"] = "IconScreen", ["input"] = "IconKeyboard",
        ["firmware"] = "IconShield", ["serial"] = "IconTerminal", ["boot"] = "IconBoot", ["performance"] = "IconCpu", ["shares"] = "IconFolder",
        ["autoprotect"] = "IconClock", ["advanced"] = "IconCode", ["history"] = "IconHistory",
    };

    /// <summary>The section's icon in the side list.</summary>
    public Avalonia.Media.Geometry? Icon =>
        Avalonia.Application.Current?.TryGetResource(Icons.GetValueOrDefault(Key, "IconSettings"), null, out var g) == true ? g as Avalonia.Media.Geometry : null;
}

/// <summary>
/// A machine's settings, every one of them, in words. Each field says what it does and what is recommended; each
/// section says whether a change applies now or at the next power on. Saving records the change in the machine's
/// settings history, so any change can be undone.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public static readonly IReadOnlyList<Option> ConnectOptions =
    [
        new(nameof(Connect.Always), "Always connected"),
        new(nameof(Connect.UntilInstalled), "Connected until the system is installed"),
        new(nameof(Connect.Never), "Disconnected"),
    ];

    public static readonly IReadOnlyList<Option> BusOptions =
    [
        new(nameof(DiskBus.Auto), "Automatic (fastest the system supports)"),
        new(nameof(DiskBus.Virtio), "VirtIO (fastest; needs drivers on Windows)"),
        new(nameof(DiskBus.Sata), "SATA (works everywhere)"),
        new(nameof(DiskBus.Ide), "IDE (very old systems)"),
        new(nameof(DiskBus.Nvme), "NVMe (Windows 10 and 11 have its driver; VMware's default)"),
        new(nameof(DiskBus.Pvscsi), "VMware paravirtual SCSI (machines brought over from VMware)"),
    ];

    public IReadOnlyList<SettingsSection> Sections { get; } =
    [
        new("general", "General", "Options"),
        new("memory", "Memory", "Hardware"),
        new("processors", "Processors", "Hardware"),
        new("disks", "Hard disks", "Hardware"),
        new("media", "CD/DVD and floppy", "Hardware"),
        new("network", "Network", "Hardware"),
        new("usb", "USB", "Hardware"),
        new("hostdrives", "Host drives", "Hardware"),
        new("sound", "Sound", "Hardware"),
        new("display", "Display", "Hardware"),
        new("input", "Keyboard and mouse", "Hardware"),
        new("firmware", "Firmware and TPM", "Hardware"),
        new("serial", "Serial port", "Hardware"),
        new("boot", "Boot and devices", "Hardware"),
        new("performance", "Performance", "Hardware"),
        new("shares", "Shared folders", "Options"),
        new("autoprotect", "AutoProtect", "Options"),
        new("advanced", "Advanced", "Options"),
        new("history", "Change history", "Options"),
    ];

    public IReadOnlyList<GuestChoice> GuestChoices { get; } = new NewMachineViewModel.GuestChoiceList();

    public IReadOnlyList<Option> NetModes { get; } =
    [
        new("User", "NAT: share this computer's connection (recommended)"),
        new("Restricted", "NAT without internet: this computer only"),
        new("Bridge", "Bridged: a device on your own network"),
        new("Segment", "Private network between machines (a LAN segment)"),
        new("None", "No network"),
    ];

    public IReadOnlyList<Option> NicModels { get; } =
    [
        new(null, "Automatic"),
        new("virtio-net-pci", "VirtIO (fastest; Linux, BSD, Windows with drivers)"),
        new("e1000", "Intel E1000 (works everywhere)"),
        new("rtl8139", "Realtek 8139 (very old systems)"),
        new("vmxnet3", "VMware VMXNET3"),
    ];

    public IReadOnlyList<Option> UsbControllers { get; } = [new(null, "Automatic"), new("xhci", "USB 3 (recommended)"), new("ehci", "USB 2"), new("none", "None")];

    public IReadOnlyList<Option> SoundCards { get; } =
    [
        new(null, "Automatic"), new("intel-hda", "Intel HD Audio"), new("ich9-intel-hda", "Intel HD Audio (ICH9)"),
        new("virtio-sound-pci", "VirtIO sound"), new("ac97", "AC'97 (older systems)"), new("sb16", "Sound Blaster 16 (DOS)"), new("none", "No sound card"),
    ];

    public IReadOnlyList<Option> DisplayBackends { get; } =
    [
        new("Spice", "In a viewer window (recommended)"),
        new("SpiceApp", "In QEMU's own SPICE window"),
        new("Gtk", "In a QEMU GTK window"),
        new("Sdl", "In a QEMU SDL window"),
        new("None", "No screen (a server)"),
    ];

    public IReadOnlyList<Option> DisplayDevices { get; } =
    [
        new(null, "Automatic"), new("virtio-vga", "VirtIO GPU"), new("qxl-vga", "QXL (Windows guest tools)"),
        new("vmware-svga", "VMware SVGA"), new("VGA", "Standard VGA"), new("bochs-display", "Bochs display"),
    ];

    public IReadOnlyList<Option> GlOptions { get; } = [new(null, "Automatic (on with a GPU)"), new("on", "On"), new("off", "Off")];

    public IReadOnlyList<Option> Keyboards { get; } = [new(null, "Automatic"), new("usb", "USB"), new("ps2", "PS/2"), new("virtio", "VirtIO")];

    public IReadOnlyList<Option> Pointers { get; } = [new(null, "Automatic"), new("tablet", "Tablet (pointer follows your mouse exactly)"), new("usb", "USB mouse"), new("ps2", "PS/2 mouse"), new("virtio", "VirtIO")];

    public IReadOnlyList<Option> SerialOptions { get; } = [new(null, "Automatic"), new("on", "On: shown in the Console tab"), new("off", "Off")];

    public IReadOnlyList<Option> AutoProtectIntervals { get; } =
        [new("15", "Every 15 minutes"), new("30", "Every 30 minutes"), new("60", "Every hour"), new("240", "Every 4 hours"), new("1440", "Every day")];

    public IReadOnlyList<Option> WatchdogOptions { get; } = [new(null, "None"), new("reset", "Restart the machine"), new("poweroff", "Power the machine off"), new("pause", "Pause the machine")];

    public IReadOnlyList<Option> BootOrders { get; } =
    [
        new(null, "Automatic (CD/DVD while installing, then the disk)"), new("c", "Disk only"), new("dc", "CD/DVD, then disk"),
        new("cd", "Disk, then CD/DVD"), new("ncd", "Network, then disk, then CD/DVD"),
    ];

    public IReadOnlyList<Option> ViewerOptions { get; } = [new("auto", "Automatic"), new("remote-viewer", "remote-viewer"), new("spicy", "spicy")];

    private readonly MachineViewModel _machine;
    private readonly HostCapabilities? _host;

    public SettingsViewModel(MachineViewModel machine, HostCapabilities? host)
    {
        _machine = machine;
        _host = host;
        var m = machine.Machine;
        Original = m;
        Name = m.Name;
        Notes = m.Notes;
        Guest = GuestChoices.FirstOrDefault(g => g.Os == m.Guest.Os && g.Release == m.Guest.Release) ?? GuestChoices.FirstOrDefault(g => g.Os == m.Guest.Os);
        MemoryAuto = m.MemoryMiB is null;
        MemoryGb = m.MemoryMiB is { } mib ? mib / 1024m : RecommendedMemoryGb();
        CoresAuto = m.Cpu.Cores is null;
        Cores = m.Cpu.Cores ?? RecommendedCores();
        CpuModel = m.Cpu.Model ?? "";
        Uefi = m.Firmware.Kind == FirmwareKind.Uefi;
        SecureBoot = m.Firmware.SecureBoot;
        Tpm = m.Tpm;
        foreach (var d in m.Disks) Disks.Add(new DiskRow(d, machine.Folder));
        foreach (var x in m.Media) Media.Add(new MediaRow(x));
        _ = RefreshMediaStatusAsync();
        var nic = m.Nics.FirstOrDefault() ?? new NicConfig { Mode = VmQontrol.Core.Machines.NetMode.None };
        NetMode = NetModes.First(o => o.Value == nic.Mode.ToString());
        NicModel = NicModels.FirstOrDefault(o => o.Value == nic.Model) ?? new Option(nic.Model, nic.Model ?? "Automatic");
        Mac = nic.Mac ?? "";
        Bridge = nic.Bridge ?? "br0";
        Segment = nic.Segment ?? "lab";
        SshForward = nic.SshForward;
        SshPort = nic.SshHostPort ?? 0;
        foreach (var f in nic.Forwards) Forwards.Add(new ForwardRow(f));
        foreach (var sh in m.Shares) Shares.Add(new ShareRow(sh));
        UsbController = UsbControllers.FirstOrDefault(o => o.Value == m.Usb.Controller) ?? UsbControllers[0];
        foreach (var u in m.Usb.Passthrough) UsbRules.Add(new UsbRuleRow(u));
        foreach (var d in m.HostDrives) HostDrives.Add(new HostDriveRow(d));
        Perf = new PerformanceSettingsViewModel(m.Performance,
            () => (int)(CoresAuto ? RecommendedCores() : Cores),
            () => (long)((MemoryAuto ? RecommendedMemoryGb() : MemoryGb) * 1024),
            () => Width > 0 && Height > 0 ? ((int)Width, (int)Height) : (1920, 1080));
        LoadSerialPorts(m);
        SoundCard = SoundCards.FirstOrDefault(o => o.Value == m.Audio.Device) ?? SoundCards[0];
        DisplayBackend = DisplayBackends.First(o => o.Value == m.Display.Backend.ToString());
        DisplayDevice = DisplayDevices.FirstOrDefault(o => o.Value == m.Display.Device) ?? DisplayDevices[0];
        Gl = GlOptions.First(o => o.Value == m.Display.Gl switch { true => "on", false => "off", _ => null });
        Width = m.Display.Width ?? 0;
        Monitors = m.Display.Monitors ?? 1;
        Microphone = m.Audio.Microphone;
        Height = m.Display.Height ?? 0;
        Viewer = ViewerOptions.FirstOrDefault(o => o.Value == m.Display.Viewer) ?? ViewerOptions[0];
        FullScreen = m.Display.FullScreen;
        CopyPaste = m.Display.CopyPaste;
        FileTransfer = m.Display.FileTransfer;
        Keyboard = Keyboards.FirstOrDefault(o => o.Value == m.Input.Keyboard) ?? Keyboards[0];
        Pointer = Pointers.FirstOrDefault(o => o.Value == m.Input.Mouse) ?? Pointers[0];
        Layout = m.Input.Layout;
        Serial = SerialOptions.First(o => o.Value == m.Serial switch { true => "on", false => "off", _ => null });
        ExtraArgs = string.Join('\n', m.ExtraArgs);
        Rng = m.Devices.Rng ?? true;
        Balloon = m.Devices.Balloon ?? true;
        BalloonAuto = m.Devices.Balloon is null;
        Watchdog = WatchdogOptions.FirstOrDefault(o => o.Value == m.Devices.Watchdog) ?? WatchdogOptions[0];
        CrashReporting = m.Devices.CrashReporting;
        BootOrder = BootOrders.FirstOrDefault(o => o.Value == m.Devices.BootOrder) ?? BootOrders[0];
        BootMenu = m.Devices.BootMenu;
        AutoProtectOn = m.AutoProtect.Enabled;
        AutoProtectInterval = AutoProtectIntervals.FirstOrDefault(o => o.Value == m.AutoProtect.IntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? new Option(m.AutoProtect.IntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Every {m.AutoProtect.IntervalMinutes} minutes");
        AutoProtectKeep = m.AutoProtect.Keep;
        Section = Sections[0];
        LoadHistory();
    }

    public MachineConfig Original { get; private set; }

    public string Title => $"{_machine.Name} settings";

    public bool Running => _machine.IsActive;

    public string TimingNote => Running
        ? "The machine is running: changes marked \"next power on\" take effect when it is powered off and on again. Discs can be changed now."
        : "The machine is off: every change takes effect at the next power on.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SectionKey))]
    private SettingsSection _section;

    public string SectionKey => Section.Key;

    // General
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private GuestChoice? _guest;

    // Memory and processors
    [ObservableProperty] private bool _memoryAuto;
    [ObservableProperty] private decimal _memoryGb;
    [ObservableProperty] private bool _coresAuto;
    [ObservableProperty] private decimal _cores;
    [ObservableProperty] private string _cpuModel;

    public decimal HostMemoryGb => _host?.MemoryGB ?? 0;

    public decimal HostCores => _host?.LogicalCpus ?? 1;

    public string MemoryNote => $"Recommended: {RecommendedMemoryGb()} GB for this system on this computer ({HostMemoryGb} GB). Giving more than half of the computer's memory slows the computer itself.";

    public string CoresNote => $"Recommended: {RecommendedCores()} of this computer's {HostCores} processors.";

    // Firmware
    [ObservableProperty] private bool _uefi;
    [ObservableProperty] private bool _secureBoot;
    [ObservableProperty] private bool _tpm;

    // Disks and media
    public ObservableCollection<DiskRow> Disks { get; } = [];

    public ObservableCollection<MediaRow> Media { get; } = [];

    [ObservableProperty] private decimal _newDiskGb = 32;

    // Network
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBridged), nameof(IsNat), nameof(IsSegment))] private Option _netMode;
    [ObservableProperty] private Option _nicModel;
    [ObservableProperty] private string _mac;
    [ObservableProperty] private string _bridge;
    [ObservableProperty] private string _segment;

    public bool IsSegment => NetMode.Value == "Segment";
    [ObservableProperty] private bool _sshForward;
    [ObservableProperty] private decimal _sshPort;

    public ObservableCollection<ForwardRow> Forwards { get; } = [];

    public ObservableCollection<ShareRow> Shares { get; } = [];

    public bool IsWindowsGuest => _machine.Machine.Guest.Os is "windows" or "windows-server";

    public bool IsBridged => NetMode.Value == "Bridge";

    public bool IsNat => NetMode.Value is "User" or "Restricted";

    // USB, sound, display, input, serial
    [ObservableProperty] private Option _usbController;
    public ObservableCollection<UsbRuleRow> UsbRules { get; } = [];

    public ObservableCollection<HostDriveRow> HostDrives { get; } = [];

    /// <summary>Settings → Performance.</summary>
    public PerformanceSettingsViewModel Perf { get; }

    /// <summary>The virtual network editor, over these settings.</summary>
    [RelayCommand]
    private void OpenNetworks() => _machine.OpenNetworks();

    /// <summary>This computer's serial ports, each ticked when the machine takes it; listed on a worker when the dialog opens.</summary>
    public ObservableCollection<SerialPortRow> SerialPorts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSerialNote))]
    private string _serialNote = "";

    public bool HasSerialNote => SerialNote.Length > 0;

    public bool NoSerialPorts => SerialPorts.Count == 0;

    private void LoadSerialPorts(MachineConfig m) => _ = Task.Run(() =>
    {
        var ports = VmQontrol.Core.Devices.HostSerial.List();
        Ui.Post(() =>
        {
            SerialPorts.Clear();
            foreach (var p in ports) SerialPorts.Add(new SerialPortRow(p, m.HostSerialPorts.Any(h => h.Path == p.StablePath || h.Path == p.DevicePath), this));
            // Saved ports that are not plugged in now stay saved (an adapter in another USB port next time).
            foreach (var h in m.HostSerialPorts.Where(h => ports.All(p => p.StablePath != h.Path && p.DevicePath != h.Path)))
                SerialPorts.Add(new SerialPortRow(new VmQontrol.Core.Devices.HostSerialPort { Name = h.Path, DevicePath = h.Path, StablePath = h.Path, Description = h.Name.Length > 0 ? $"{h.Name} (not connected)" : h.Path, Accessible = true }, true, this));
            OnPropertyChanged(nameof(NoSerialPorts));
        });
    });

    /// <summary>Joins the group that owns serial ports through polkit; the membership takes effect at the next sign-in.</summary>
    public async Task GiveSerialAccessAsync(SerialPortRow row)
    {
        var group = row.Port.Group ?? "uucp";
        var user = Environment.UserName;
        if (VmQontrol.Core.Devices.HostSerial.Listed(group, user))
        {
            Ui.Post(() => SerialNote = $"You are in the {group} group already; it takes effect when you sign out and back in.");
            return;
        }

        Ui.Post(() => SerialNote = $"Asking for the password to add you to the {group} group…");
        var why = await VmQontrol.Core.Devices.HostSerial.JoinAsync(group).ConfigureAwait(false);
        Ui.Post(() => SerialNote = why is null
            ? $"You are in the {group} group now. Sign out and back in once; then the machine can use {row.Name}."
            : $"You were not added to the {group} group: {why}.");
    }

    [RelayCommand]
    private void RemoveHostDrive(HostDriveRow row) => HostDrives.Remove(row);

    /// <summary>The host drives panel, over these settings; what it saved is read back here when it closes, so the two never disagree.</summary>
    [RelayCommand]
    private void ChooseHostDrives()
    {
        var panel = _machine.OpenDrivesPanel();
        panel.Closed += () => Ui.Post(() =>
        {
            HostDrives.Clear();
            foreach (var d in _machine.Machine.HostDrives) HostDrives.Add(new HostDriveRow(d));
        });
    }

    /// <summary>A device to add by its id ("046d:c52b"), for one that is not plugged in now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddUsbRuleCommand))]
    private string _newUsbId = "";

    private bool CanAddUsbRule() => System.Text.RegularExpressions.Regex.IsMatch(NewUsbId.Trim(), "^[0-9a-fA-F]{4}:[0-9a-fA-F]{4}$")
        && UsbRules.All(r => !string.Equals(r.Id, NewUsbId.Trim(), StringComparison.OrdinalIgnoreCase));

    [RelayCommand(CanExecute = nameof(CanAddUsbRule))]
    private void AddUsbRule()
    {
        var parts = NewUsbId.Trim().ToLowerInvariant().Split(':');
        UsbRules.Add(new UsbRuleRow(new UsbDevice(parts[0], parts[1])));
        NewUsbId = "";
    }

    [RelayCommand]
    private void RemoveUsbRule(UsbRuleRow row)
    {
        UsbRules.Remove(row);
        AddUsbRuleCommand.NotifyCanExecuteChanged();
    }
    [ObservableProperty] private Option _soundCard;
    [ObservableProperty] private Option _displayBackend;
    [ObservableProperty] private Option _displayDevice;
    [ObservableProperty] private Option _gl;
    [ObservableProperty] private decimal _width;
    [ObservableProperty] private decimal _monitors = 1;
    [ObservableProperty] private bool _microphone = true;
    [ObservableProperty] private decimal _height;
    [ObservableProperty] private Option _viewer;
    [ObservableProperty] private bool _fullScreen;
    [ObservableProperty] private bool _copyPaste = true;
    [ObservableProperty] private bool _fileTransfer = true;
    [ObservableProperty] private Option _keyboard;
    [ObservableProperty] private Option _pointer;
    [ObservableProperty] private string _layout;
    [ObservableProperty] private Option _serial;

    // Boot and devices
    [ObservableProperty] private bool _rng;
    [ObservableProperty] private bool _balloon;
    [ObservableProperty] private bool _balloonAuto;
    [ObservableProperty] private Option _watchdog;
    [ObservableProperty] private bool _autoProtectOn;
    [ObservableProperty] private Option _autoProtectInterval;
    [ObservableProperty] private decimal _autoProtectKeep = 3;
    [ObservableProperty] private bool _crashReporting;
    [ObservableProperty] private Option _bootOrder;
    [ObservableProperty] private bool _bootMenu;

    // Advanced
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CommandPreview))] private string _extraArgs;

    public string CommandPreview => BuildPreview();

    // History
    public ObservableCollection<SettingsChange> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    [ObservableProperty] private string _saveNote = "";

    public event Action? Closed;

    private decimal RecommendedMemoryGb() => _host is null ? 4 : Recommendations.For(_machine.Machine.Guest, null, _host).MemoryMiB.Value / 1024m;

    private decimal RecommendedCores() => _host is null ? 2 : Recommendations.For(_machine.Machine.Guest, null, _host).Cores.Value;

    /// <summary>The settings as they stand in the dialog now.</summary>
    public MachineConfig Build()
    {
        var o = Original;
        var nic = (o.Nics.FirstOrDefault() ?? new NicConfig()) with
        {
            Mode = Enum.Parse<NetMode>(NetMode.Value!),
            Model = NicModel.Value,
            Mac = Mac.Trim().Length > 0 ? Mac.Trim() : null,
            Bridge = IsBridged ? Bridge.Trim() : null,
            Segment = IsSegment ? Segment.Trim() : null,
            SshForward = SshForward,
            SshHostPort = SshPort > 0 ? (int)SshPort : null,
            Forwards = Forwards.Select(f => f.ToConfig()).ToList(),
        };
        return o with
        {
            Name = Name.Trim().Length > 0 ? Name.Trim() : o.Name,
            Notes = Notes,
            Guest = Guest is { } g ? o.Guest with { Os = g.Os, Release = g.Release } : o.Guest,
            MemoryMiB = MemoryAuto ? null : (long)(MemoryGb * 1024),
            Cpu = new CpuConfig { Cores = CoresAuto ? null : (int)Cores, Model = CpuModel.Trim().Length > 0 ? CpuModel.Trim() : null },
            Firmware = o.Firmware with { Kind = Uefi ? FirmwareKind.Uefi : FirmwareKind.Bios, SecureBoot = Uefi && SecureBoot },
            Tpm = Tpm,
            Disks = Disks.Select(d => d.ToConfig()).ToList(),
            Media = Media.Select(m => m.ToConfig()).ToList(),
            Nics = [nic, .. o.Nics.Skip(1)],
            Usb = new UsbConfig
            {
                Controller = UsbController.Value,
                Passthrough = UsbRules.Select(r => r.Device).ToList(),
            },
            Audio = new AudioConfig { Device = SoundCard.Value, Microphone = Microphone },
            Display = new DisplayConfig
            {
                Backend = Enum.Parse<DisplayBackend>(DisplayBackend.Value!),
                Device = DisplayDevice.Value,
                Gl = Gl.Value switch { "on" => true, "off" => false, _ => null },
                Width = Width > 0 && Height > 0 ? (int)Width : null,
                Monitors = Monitors > 1 ? (int)Monitors : null,
                Height = Width > 0 && Height > 0 ? (int)Height : null,
                Viewer = Viewer.Value ?? "auto",
                FullScreen = FullScreen,
                CopyPaste = CopyPaste,
                FileTransfer = FileTransfer,
            },
            Input = new InputConfig { Keyboard = Keyboard.Value, Mouse = Pointer.Value, Layout = Layout.Trim().Length > 0 ? Layout.Trim() : "en-us" },
            Serial = Serial.Value switch { "on" => true, "off" => false, _ => null },
            ExtraArgs = ExtraArgs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Shares = Shares.Where(x => x.Path.Trim().Length > 0).Select(x => x.ToConfig()).ToList(),
            HostDrives = HostDrives.Select(d => d.Drive).ToList(),
            Performance = Perf.ToConfig(),
            HostSerialPorts = SerialPorts.Where(p => p.Connected).Select(p => new HostSerialConfig { Path = p.Port.StablePath, Name = p.Port.Description.Replace(" (not connected)", "", StringComparison.Ordinal) }).ToList(),
            Devices = new DevicesConfig
            {
                Rng = Rng ? null : false,
                Balloon = BalloonAuto ? null : Balloon,
                Watchdog = Watchdog.Value,
                CrashReporting = CrashReporting,
                BootOrder = BootOrder.Value,
                BootMenu = BootMenu,
            },
            AutoProtect = new AutoProtectConfig
            {
                Enabled = AutoProtectOn,
                IntervalMinutes = int.Parse(AutoProtectInterval.Value ?? "60", System.Globalization.CultureInfo.InvariantCulture),
                Keep = (int)Math.Clamp(AutoProtectKeep, 1, 50),
            },
        };
    }

    private string BuildPreview()
    {
        if (_host is null) return "";
        try
        {
            var launch = QemuCommandBuilder.Build(Build(), new LaunchContext { Host = _host, MachineDirectory = _machine.Folder, Runtime = new RuntimePaths("/run/user/…/vmqontrol/" + _machine.Id[..12]), FileExists = File.Exists });
            return "qemu-system-x86_64 \\\n  " + string.Join(" \\\n  ", Pairs(launch.Args)) + (launch.Errors.Count > 0 ? "\n\n" + string.Join("\n", launch.Errors.Select(e => "Cannot start: " + e)) : "");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return ex.Message;
        }
    }

    private static IEnumerable<string> Pairs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
            yield return i + 1 < args.Count && !args[i + 1].StartsWith('-') ? $"{args[i]} {args[++i]}" : args[i];
    }

    partial void OnSectionChanged(SettingsSection value)
    {
        if (value.Key == "advanced") OnPropertyChanged(nameof(CommandPreview));
    }

    // ── Commands

    [RelayCommand]
    private async Task SaveAsync()
    {
        var after = Build();
        var before = Original;
        var vmJson = Path.Combine(_machine.Folder, MachineStore.FileName);
        var historyDir = _machine.HistoryDirectory;
        try
        {
            var changes = await Task.Run(() =>
            {
                // An imported quickemu machine keeps its .conf untouched: VM Qontrol's settings for it live in its own state.
                var target = _machine.IsQuickemu ? Path.Combine(historyDir, MachineStore.FileName) : vmJson;
                var c = SettingsHistory.Save(before, after, target, historyDir);
                foreach (var d in after.Disks.Zip(before.Disks).Where(p => p.First.SizeGiB > p.Second.SizeGiB))
                    GrowDisk(d.First);
                return c;
            }).ConfigureAwait(false);
            Ui.Post(() =>
            {
                Original = after;
                SaveNote = changes.Count == 0 ? "Nothing changed." : $"Saved: {string.Join("; ", changes)}.";
                LoadHistory();
                _machine.Reload(after);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => SaveNote = $"Not saved: {ex.Message}");
        }
    }

    private void GrowDisk(DiskConfig disk)
    {
        var path = Path.IsPathRooted(disk.File) ? disk.File : Path.Combine(_machine.Folder, disk.File);
        if (!File.Exists(path) || _machine.IsActive) return;
        QemuImg.RunAsync(CancellationToken.None, "resize", "-f", disk.Format, path, disk.SizeGiB.ToString(CultureInfo.InvariantCulture) + "G").GetAwaiter().GetResult();
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();

    [RelayCommand]
    private void AddDisk()
    {
        var index = Disks.Count;
        string Name(int i) => $"disk{i}.qcow2";
        while (Disks.Any(d => d.File == Name(index))) index++;
        Disks.Add(new DiskRow(new DiskConfig { File = Name(index), SizeGiB = (long)NewDiskGb }, _machine.Folder));
    }

    [RelayCommand]
    private void RemoveDisk(DiskRow row)
    {
        if (Disks.IndexOf(row) == 0) return; // The system disk stays; it holds the operating system.
        Disks.Remove(row);
    }

    [RelayCommand]
    private void AddDrive() => Media.Add(new MediaRow(new MediaConfig { Role = MediaRole.Drivers, File = "", Connect = Connect.Always }));

    [RelayCommand]
    private void RemoveMedia(MediaRow row) => Media.Remove(row);

    /// <summary>Puts a disc in a drive; in a running machine the drive changes at once.</summary>
    public async Task ChangeDiscAsync(MediaRow row, string file)
    {
        var old = row.File;
        row.File = file;
        // A disc chosen by hand is meant to be in the drive, also once the system is installed.
        if (row.Connect.Value != nameof(Connect.Always)) row.Connect = ConnectOptions.First(o => o.Value == nameof(Connect.Always));
        if (_machine.Watcher is { } watcher)
        {
            try
            {
                var full = Path.IsPathRooted(old) ? old : Path.Combine(_machine.Folder, old);
                await LiveMedia.ChangeAsync(watcher, old.Length > 0 ? full : null, file).ConfigureAwait(false);
                Ui.Post(() => SaveNote = $"{Path.GetFileName(file)} is in the drive now. Save to keep it for the next power on.");
            }
            catch (Exception ex) when (ex is QmpException or IOException)
            {
                Ui.Post(() => SaveNote = $"The disc could not be changed now ({ex.Message}); it will be at the next power on once saved.");
            }
        }

        await RefreshMediaStatusAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Each disc's place, from QEMU's own drive list while the machine runs, else from its settings: an installer marked
    /// "until installed" stays out once the system disk is used. Reads on a worker.
    /// </summary>
    private async Task RefreshMediaStatusAsync()
    {
        var rows = Media.ToList();
        var folder = _machine.Folder;
        var watcher = _machine.Watcher;
        var disk = _machine.Machine.Disks.FirstOrDefault()?.File;
        string Full(string f) => Path.IsPathRooted(f) ? f : Path.Combine(folder, f);
        var (inDrive, installed) = await Task.Run<(HashSet<string>? Live, bool Installed)>(async () =>
        {
            HashSet<string>? live = null;
            if (watcher is not null)
            {
                try
                {
                    live = (await LiveMedia.DrivesAsync(watcher).ConfigureAwait(false)).Where(d => d.File is not null).Select(d => d.File!).ToHashSet(StringComparer.Ordinal);
                }
                catch (Exception ex) when (ex is QmpException or IOException)
                {
                    Core.Log.Warn($"{_machine.Name}: could not list the disc drives ({ex.Message}).");
                }
            }

            return (live, disk is not null && DiskState.IsUsed(Full(disk)));
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            foreach (var row in rows)
            {
                var connect = Enum.Parse<Connect>(row.Connect.Value!);
                row.InDrive = inDrive?.Contains(Full(row.File)) == true;
                row.Status = row.File.Length == 0 ? "Empty"
                    : row.InDrive ? "In the drive now"
                    : connect == Connect.Never ? "Kept out of the drive"
                    : connect == Connect.UntilInstalled && installed ? "Out of the drive: the system is installed, so this disc no longer goes in"
                    : inDrive is not null ? "Not in the drive now; it goes in at the next power on"
                    : "Goes in the drive at power on";
            }
        });
    }

    /// <summary>
    /// Takes the disc out: now, if it is in the running machine's drive, and from the next power on (Disconnected, kept
    /// once saved). Always says what happened.
    /// </summary>
    [RelayCommand]
    private async Task EjectAsync(MediaRow row)
    {
        if (row.File.Length == 0) return;
        var wasIn = row.InDrive;
        row.Connect = ConnectOptions.First(o => o.Value == nameof(Connect.Never));
        if (wasIn && _machine.Watcher is { } watcher)
        {
            try
            {
                await LiveMedia.EjectAsync(watcher, Path.IsPathRooted(row.File) ? row.File : Path.Combine(_machine.Folder, row.File)).ConfigureAwait(false);
                Ui.Post(() => SaveNote = $"{row.FileName} is out of the drive. Save to keep it out from the next power on.");
            }
            catch (Exception ex) when (ex is QmpException or IOException)
            {
                Ui.Post(() => SaveNote = $"The guest held on to {row.FileName} ({ex.Message}); eject it inside the machine first. It stays out from the next power on once saved.");
            }
        }
        else
        {
            Ui.Post(() => SaveNote = $"{row.FileName} was not in the drive; it is set to stay out. Save to keep that.");
        }

        await RefreshMediaStatusAsync().ConfigureAwait(false);
    }

    /// <summary>Adds a folder chosen by the person (the view picks it).</summary>
    public void AddShare(string path)
    {
        var tag = new string(System.IO.Path.GetFileName(path.TrimEnd('/')).ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (tag.Length == 0) tag = "share";
        while (Shares.Any(x => x.Tag == tag)) tag += "-2";
        Shares.Add(new ShareRow(new ShareConfig { Path = path, Tag = tag }));
    }

    [RelayCommand]
    private void RemoveShare(ShareRow row) => Shares.Remove(row);

    [RelayCommand]
    private void AddForward() => Forwards.Add(new ForwardRow(new PortForward("tcp", 8080, 80)));

    [RelayCommand]
    private void RemoveForward(ForwardRow row) => Forwards.Remove(row);

    [RelayCommand]
    private void NewMac()
    {
        // A locally administered address in QEMU's 52:54:00 block.
        var bytes = new byte[3];
        Random.Shared.NextBytes(bytes);
        Mac = $"52:54:00:{bytes[0]:x2}:{bytes[1]:x2}:{bytes[2]:x2}";
    }

    [RelayCommand]
    private void UseRecommendedMemory()
    {
        MemoryAuto = true;
        MemoryGb = RecommendedMemoryGb();
    }

    [RelayCommand]
    private void UseRecommendedCores()
    {
        CoresAuto = true;
        Cores = RecommendedCores();
    }

    [RelayCommand]
    private async Task RestoreAsync(SettingsChange change)
    {
        var before = SettingsHistory.Before(change);
        var current = Original;
        var historyDir = _machine.HistoryDirectory;
        var target = _machine.IsQuickemu ? Path.Combine(historyDir, MachineStore.FileName) : Path.Combine(_machine.Folder, MachineStore.FileName);
        await Task.Run(() => SettingsHistory.Save(current, before, target, historyDir)).ConfigureAwait(false);
        Ui.Post(() =>
        {
            SaveNote = $"Restored the settings from before {change.At.LocalDateTime:g}.";
            _machine.Reload(before);
            Closed?.Invoke();
        });
    }

    private void LoadHistory()
    {
        var dir = _machine.HistoryDirectory;
        _ = Task.Run(() => SettingsHistory.Read(dir)).ContinueWith(t => Ui.Post(() =>
        {
            History.Clear();
            foreach (var c in t.Result) History.Add(c);
            OnPropertyChanged(nameof(HasHistory));
        }), TaskScheduler.Default);
    }
}
