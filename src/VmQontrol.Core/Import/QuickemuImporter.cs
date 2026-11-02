using System.Globalization;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Import;

/// <summary>The result of reading a quickemu machine: the machine, where its files live, and what could not be carried over.</summary>
public sealed record QuickemuImport(MachineConfig Machine, string MachineDirectory, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a quickemu <c>.conf</c> into a machine that points at the quickemu machine's own files where they are.
/// Nothing in the quickemu folder is written: the import only reads.
/// </summary>
public static class QuickemuImporter
{
    private static readonly HashSet<string> Known =
    [
        "guest_os", "disk_img", "disk_size", "disk_format", "preallocation", "iso", "fixed_iso", "img", "floppy",
        "boot", "secureboot", "tpm", "ram", "cpu_cores", "macos_release", "network", "macaddr", "bridge",
        "port_forwards", "usb_devices", "usb_controller", "keyboard", "keyboard_layout", "mouse", "sound_card",
        "display", "width", "height", "ssh_port", "spice_port", "extra_args", "serial", "monitor", "viewer", "arch",
    ];

    public static QuickemuImport Import(string confFile)
    {
        confFile = Path.GetFullPath(confFile);
        var conf = QuickemuConf.Load(confFile);
        var baseDir = Path.GetDirectoryName(confFile)!;
        var name = Path.GetFileNameWithoutExtension(confFile);
        var notes = new List<string>();

        string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));

        var os = (conf["guest_os"] ?? "linux").ToLowerInvariant();
        var format = conf["disk_format"] ?? "qcow2";
        var disk = conf["disk_img"] is { } d ? Resolve(d) : Path.Combine(baseDir, name, $"disk.{format}");
        var vmDir = Path.GetDirectoryName(disk)!;

        var media = new List<MediaConfig>();
        if (conf["iso"] is { } iso) media.Add(new MediaConfig { Role = MediaRole.Installer, File = Resolve(iso), Connect = Connect.UntilInstalled });
        if (conf["img"] is { } img)
            media.Add(new MediaConfig { Role = os == "macos" ? MediaRole.Recovery : MediaRole.Installer, File = Resolve(img), Connect = Connect.UntilInstalled });
        if (conf["fixed_iso"] is { } fixedIso) media.Add(new MediaConfig { Role = MediaRole.Drivers, File = Resolve(fixedIso) });
        if (conf["floppy"] is { } floppy) media.Add(new MediaConfig { Role = MediaRole.Floppy, File = Resolve(floppy) });
        // quickget makes the Windows answer-file disc beside the disk; quickemu attaches it while installing.
        if (os == "windows")
            media.Add(new MediaConfig { Role = MediaRole.Unattended, File = Path.Combine(vmDir, "unattended.iso"), Connect = Connect.UntilInstalled });
        if (os == "macos")
            media.Add(new MediaConfig { Role = MediaRole.Bootloader, File = Path.Combine(vmDir, "OpenCore.qcow2") });

        var firmware = new FirmwareConfig
        {
            Kind = conf["boot"] is { } boot && !boot.Contains("efi", StringComparison.OrdinalIgnoreCase) && os != "macos" ? FirmwareKind.Bios : FirmwareKind.Uefi,
            SecureBoot = conf["secureboot"] == "on",
            VarsFile = Path.Combine(vmDir, "OVMF_VARS.fd"),
        };
        if (os == "macos")
        {
            // quickget puts macOS's own firmware beside the disk, with vars sized for the chosen resolution.
            var vars = File.Exists(Path.Combine(vmDir, "OVMF_VARS-1024x768.fd")) ? "OVMF_VARS-1024x768.fd" : "OVMF_VARS-1920x1080.fd";
            firmware = firmware with { CodeFile = Path.Combine(vmDir, "OVMF_CODE.fd"), VarsFile = Path.Combine(vmDir, vars) };
        }

        var network = conf["network"] ?? conf["bridge"];
        var nic = new NicConfig
        {
            Mode = network switch
            {
                null => NetMode.User,
                "none" => NetMode.None,
                "restrict" => NetMode.Restricted,
                _ => NetMode.Bridge,
            },
            Bridge = network is not (null or "none" or "restrict") ? network : null,
            Mac = conf["macaddr"],
            SshHostPort = Int(conf["ssh_port"]),
            Forwards = conf.Array("port_forwards").SelectMany(Forward).ToList(),
        };

        var display = new DisplayConfig
        {
            Width = Int(conf["width"]),
            Height = Int(conf["height"]),
            Backend = conf["display"] switch
            {
                "sdl" => DisplayBackend.Sdl,
                "gtk" => DisplayBackend.Gtk,
                "spice-app" => DisplayBackend.SpiceApp,
                "none" => DisplayBackend.None,
                // quickemu's own default is a GTK window; VM Qontrol shows every machine through SPICE and a viewer.
                _ => DisplayBackend.Spice,
            },
        };

        var machine = new MachineConfig
        {
            // Stable for the same .conf, so its runtime folder and anything keyed by id survive a re-read.
            Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(confFile)))[..32].ToLowerInvariant(),
            Name = name,
            Guest = new GuestConfig { Os = os, Release = conf["macos_release"] ?? WindowsRelease(os, name) },
            Firmware = firmware,
            Cpu = new CpuConfig { Cores = Int(conf["cpu_cores"]) },
            MemoryMiB = conf["ram"] is { } ram ? MemoryMiB(ram) : null,
            Disks = [new DiskConfig { File = disk, Format = format, SizeGiB = SizeGiB(conf["disk_size"]), Preallocation = conf["preallocation"] ?? "off" }],
            Media = media,
            Nics = [nic],
            Display = display,
            Audio = new AudioConfig { Device = conf["sound_card"] },
            Input = new InputConfig { Keyboard = conf["keyboard"], Mouse = conf["mouse"], Layout = conf["keyboard_layout"] ?? "en-us" },
            Usb = new UsbConfig
            {
                Controller = conf["usb_controller"],
                Passthrough = conf.Array("usb_devices").Select(u => u.Split(':')).Where(p => p.Length == 2).Select(p => new UsbDevice(p[0], p[1])).ToList(),
            },
            Tpm = conf["tpm"] == "on",
            Serial = conf["serial"] switch { "none" => false, "socket" or "telnet" => true, _ => null },
            ExtraArgs = conf["extra_args"]?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [],
            ImportedFrom = new ImportSource("quickemu", confFile),
        };

        foreach (var key in conf.Values.Keys.Concat(conf.Arrays.Keys).Where(k => !Known.Contains(k)))
            notes.Add($"The setting '{key}' is not used by VM Qontrol and was left out.");
        foreach (var line in conf.Unread) notes.Add($"The line '{line}' is not a plain setting and was left out.");
        if (conf["arch"] is { } arch && arch != "x86_64") notes.Add($"The {arch} architecture is not supported yet; the machine is treated as x86_64.");
        return new QuickemuImport(machine, vmDir, notes);
    }

    /// <summary>quickemu's own rule (its issue 1423): a Windows machine named for Windows 11 needs two cores at least.</summary>
    private static string WindowsRelease(string os, string name) =>
        os == "windows" && (name.Contains("windows-11", StringComparison.Ordinal) || name.Contains("win11", StringComparison.Ordinal)) ? "11" : "";

    /// <summary>"8080:80" forwards TCP and UDP, as quickemu does.</summary>
    private static IEnumerable<PortForward> Forward(string spec)
    {
        var parts = spec.Split(':');
        if (parts.Length != 2 || Int(parts[0]) is not { } host || Int(parts[1]) is not { } guest) yield break;
        yield return new PortForward("tcp", host, guest);
        yield return new PortForward("udp", host, guest);
    }

    private static int? Int(string? s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>quickemu sizes: "8G", "4096M", "16" (GiB).</summary>
    public static long? MemoryMiB(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var unit = char.ToUpperInvariant(text[^1]);
        var number = char.IsAsciiDigit(unit) ? text : text[..^1];
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        return unit switch { 'M' => n, 'T' => n * 1024 * 1024, 'K' => Math.Max(1, n / 1024), _ => n * 1024 };
    }

    private static long SizeGiB(string? text) => text is null ? 16 : (MemoryMiB(text) ?? 16 * 1024) / 1024;
}
