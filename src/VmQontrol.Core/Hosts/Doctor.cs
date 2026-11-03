namespace VmQontrol.Core.Hosts;

public enum CheckStatus
{
    Ok,

    /// <summary>Works, but something is missing that a feature needs (shared folders, the download catalog).</summary>
    Optional,

    /// <summary>Works badly (no KVM: very slow).</summary>
    Warning,

    /// <summary>Machines cannot run until it is fixed.</summary>
    Missing,
}

/// <summary>One finding: what was checked, what was found, why it matters, and the exact fix for this distribution.</summary>
public sealed record DoctorCheck(string Title, CheckStatus Status, string Detail, string? Fix = null);

/// <summary>The Linux distribution family, from /etc/os-release, for install commands.</summary>
public enum DistroFamily
{
    Unknown,
    Arch,
    Debian,
    Fedora,
    Alpine,
    OpenSuse,
}

/// <summary>
/// The host check: everything VM Qontrol needs from this computer, each finding with the reason it matters and the exact
/// command that fixes it here. VM Qontrol never asks for root itself; it shows the command to run.
/// </summary>
public static class Doctor
{
    private static readonly Dictionary<string, Dictionary<DistroFamily, string>> Packages = new()
    {
        ["qemu"] = new() { [DistroFamily.Arch] = "qemu-desktop", [DistroFamily.Debian] = "qemu-system-x86 qemu-utils", [DistroFamily.Fedora] = "qemu-system-x86 qemu-img", [DistroFamily.Alpine] = "qemu-system-x86_64 qemu-img", [DistroFamily.OpenSuse] = "qemu-x86 qemu-tools" },
        ["ovmf"] = new() { [DistroFamily.Arch] = "edk2-ovmf", [DistroFamily.Debian] = "ovmf", [DistroFamily.Fedora] = "edk2-ovmf", [DistroFamily.Alpine] = "ovmf", [DistroFamily.OpenSuse] = "qemu-ovmf-x86_64" },
        ["swtpm"] = new() { [DistroFamily.Arch] = "swtpm", [DistroFamily.Debian] = "swtpm swtpm-tools", [DistroFamily.Fedora] = "swtpm swtpm-tools", [DistroFamily.Alpine] = "swtpm", [DistroFamily.OpenSuse] = "swtpm" },
        ["usb-helper"] = new() { [DistroFamily.Arch] = "spice-gtk", [DistroFamily.Debian] = "spice-client-glib-usb-acl-helper", [DistroFamily.Fedora] = "spice-glib", [DistroFamily.Alpine] = "spice-gtk", [DistroFamily.OpenSuse] = "spice-gtk" },
        ["viewer"] = new() { [DistroFamily.Arch] = "virt-viewer", [DistroFamily.Debian] = "virt-viewer", [DistroFamily.Fedora] = "virt-viewer", [DistroFamily.Alpine] = "virt-viewer", [DistroFamily.OpenSuse] = "virt-viewer" },
        ["osinfo"] = new() { [DistroFamily.Arch] = "osinfo-db", [DistroFamily.Debian] = "osinfo-db", [DistroFamily.Fedora] = "osinfo-db", [DistroFamily.Alpine] = "osinfo-db", [DistroFamily.OpenSuse] = "osinfo-db" },
        ["ffmpeg"] = new() { [DistroFamily.Arch] = "ffmpeg", [DistroFamily.Debian] = "ffmpeg", [DistroFamily.Fedora] = "ffmpeg-free", [DistroFamily.Alpine] = "ffmpeg", [DistroFamily.OpenSuse] = "ffmpeg" },
        ["virtiofsd"] = new() { [DistroFamily.Arch] = "virtiofsd", [DistroFamily.Debian] = "virtiofsd", [DistroFamily.Fedora] = "virtiofsd", [DistroFamily.Alpine] = "virtiofsd", [DistroFamily.OpenSuse] = "virtiofsd" },
        ["passt"] = new() { [DistroFamily.Arch] = "passt", [DistroFamily.Debian] = "passt", [DistroFamily.Fedora] = "passt", [DistroFamily.Alpine] = "passt", [DistroFamily.OpenSuse] = "passt" },
        ["windows-download"] = new() { [DistroFamily.Arch] = "wimlib libisoburn libarchive", [DistroFamily.Debian] = "wimtools xorriso libarchive-tools", [DistroFamily.Fedora] = "wimlib-utils xorriso bsdtar", [DistroFamily.Alpine] = "wimlib xorriso libarchive-tools", [DistroFamily.OpenSuse] = "wimlib xorriso bsdtar" },
        ["quickemu"] = new() { [DistroFamily.Arch] = "quickemu", [DistroFamily.Debian] = "quickemu", [DistroFamily.Fedora] = "quickemu" },
    };

    public static DistroFamily DetectDistro(string osRelease)
    {
        var fields = osRelease.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim('"', '\''));
        var ids = $"{fields.GetValueOrDefault("ID")} {fields.GetValueOrDefault("ID_LIKE")}".Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var id in ids)
        {
            switch (id)
            {
                case "arch": return DistroFamily.Arch;
                case "debian" or "ubuntu": return DistroFamily.Debian;
                case "fedora" or "rhel" or "centos": return DistroFamily.Fedora;
                case "alpine": return DistroFamily.Alpine;
                case "opensuse" or "suse" or "opensuse-tumbleweed" or "opensuse-leap": return DistroFamily.OpenSuse;
            }
        }

        return DistroFamily.Unknown;
    }

    /// <summary>The command that installs a component here, or a sentence when the distribution is not known.</summary>
    public static string InstallCommand(DistroFamily distro, string component)
    {
        if (!Packages.TryGetValue(component, out var names) || !names.TryGetValue(distro, out var pkg))
            return $"Install your distribution's package for {component}.";
        return distro switch
        {
            DistroFamily.Arch => $"sudo pacman -S --needed {pkg}",
            DistroFamily.Debian => $"sudo apt install {pkg}",
            DistroFamily.Fedora => $"sudo dnf install {pkg}",
            DistroFamily.Alpine => $"sudo apk add {pkg}",
            DistroFamily.OpenSuse => $"sudo zypper install {pkg}",
            _ => $"Install your distribution's package for {component}.",
        };
    }

    /// <summary>Runs every check. <paramref name="onPath"/> and <paramref name="exists"/> are the system's unless a test supplies its own.</summary>
    public static IReadOnlyList<DoctorCheck> Run(HostCapabilities host, DistroFamily distro, string libraryRoot,
        Func<string, bool>? onPath = null, Func<string, bool>? exists = null, Func<string, long?>? freeBytes = null, Func<string, bool>? setuid = null)
    {
        onPath ??= OnPath;
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        freeBytes ??= FreeBytes;
        var checks = new List<DoctorCheck>();
        var hasVmx = host.CpuFlags.Contains("vmx") || host.CpuFlags.Contains("svm");

        if (host.KvmUsable)
            checks.Add(new("Hardware acceleration (KVM)", CheckStatus.Ok, "Machines run at near-native speed."));
        else if (!exists("/dev/kvm"))
            checks.Add(new("Hardware acceleration (KVM)", CheckStatus.Warning,
                hasVmx
                    ? "The processor supports virtualization but /dev/kvm is missing, so machines run in slow software emulation. Load the KVM module."
                    : "Virtualization is switched off in this computer's firmware (BIOS/UEFI), so machines run in slow software emulation. Turn on \"SVM\" (AMD) or \"VT-x\" (Intel) in the firmware setup.",
                hasVmx ? $"sudo modprobe kvm_{(host.IsAmd ? "amd" : "intel")}" : null));
        else
            checks.Add(new("Hardware acceleration (KVM)", CheckStatus.Warning,
                "/dev/kvm exists but your account may not use it, so machines run in slow software emulation. Add yourself to the kvm group, then log out and back in.",
                "sudo usermod -aG kvm $USER"));

        checks.Add(host.QemuVersion.Major == 0
            ? new("QEMU", CheckStatus.Missing, "QEMU runs every virtual machine and is not installed.", InstallCommand(distro, "qemu"))
            : host.QemuVersion < new Version(7, 0)
                ? new("QEMU", CheckStatus.Warning, $"QEMU {host.QemuVersion} is old; version 7.0 or newer is recommended.", InstallCommand(distro, "qemu"))
                : new("QEMU", CheckStatus.Ok, $"Version {host.QemuVersion}."));
        if (!onPath("qemu-img")) checks.Add(new("Disk tool (qemu-img)", CheckStatus.Missing, "Needed to create, resize and snapshot disks.", InstallCommand(distro, "qemu")));

        var plain = FirmwareLocator.Find(host.FirmwareFiles, secureBoot: false);
        var secure = FirmwareLocator.Find(host.FirmwareFiles, secureBoot: true);
        checks.Add(plain is null
            ? new("UEFI firmware (OVMF)", CheckStatus.Missing, "Modern systems (Windows 11, most Linux) start with UEFI firmware.", InstallCommand(distro, "ovmf"))
            : new("UEFI firmware (OVMF)", CheckStatus.Ok, plain.Code));
        checks.Add(secure is not null
            ? new("Secure Boot firmware", CheckStatus.Ok, secure.Code)
            : distro == DistroFamily.Alpine
                ? new("Secure Boot firmware", CheckStatus.Optional, "Alpine's firmware package has no Secure Boot build. Machines run without Secure Boot; current Windows 11 discs refuse to install without it.")
                : new("Secure Boot firmware", CheckStatus.Optional, "Windows 11 machines need it (current discs refuse to install without Secure Boot), and any machine that turns Secure Boot on.", InstallCommand(distro, "ovmf")));

        checks.Add(onPath("swtpm")
            ? new("TPM emulator (swtpm)", CheckStatus.Ok, "Windows 11 machines get the TPM they require.")
            : new("TPM emulator (swtpm)", CheckStatus.Missing, "Windows 11 requires a TPM; without swtpm it will not install.", InstallCommand(distro, "swtpm")));

        var viewer = new[] { "remote-viewer", "spicy" }.FirstOrDefault(onPath);
        checks.Add(viewer is not null
            ? new("Screen viewer", CheckStatus.Ok, $"{viewer} shows each machine's screen.")
            : new("Screen viewer", CheckStatus.Missing, "Machines run, but their screen cannot be shown without a SPICE viewer.", InstallCommand(distro, "viewer")));
        checks.Add(Devices.UsbAccess.Available
            ? new("USB devices", CheckStatus.Ok, "USB devices can be handed to machines without a password (spice-gtk's helper).")
            : new("USB devices", CheckStatus.Optional, "Handing USB devices to machines needs spice-gtk's USB helper, the same one virt-viewer uses.", InstallCommand(distro, "usb-helper")));

        checks.Add(exists("/usr/share/osinfo") || exists("/usr/local/share/osinfo")
            ? new("Operating-system database", CheckStatus.Ok, "Installer discs are recognized and machines get the right defaults.")
            : new("Operating-system database", CheckStatus.Optional, "Without it you choose the operating system yourself when creating a machine.", InstallCommand(distro, "osinfo")));

        checks.Add(onPath("virtiofsd") || exists("/usr/lib/virtiofsd") || exists("/usr/libexec/virtiofsd")
            ? new("Shared folders (virtiofsd)", CheckStatus.Ok, "Folders can be shared with machines at full speed.")
            : new("Shared folders (virtiofsd)", CheckStatus.Optional, "Shared folders fall back to a slower method without it.", InstallCommand(distro, "virtiofsd")));
        checks.Add(onPath("passt")
            ? new("Fast networking (passt)", CheckStatus.Ok, "Machines get faster networking with IPv6.")
            : new("Fast networking (passt)", CheckStatus.Optional, "Networking uses QEMU's built-in method without it.", InstallCommand(distro, "passt")));
        var windowsTools = Downloads.WindowsDownload.MissingTools(onPath);
        checks.Add(windowsTools.Count == 0
            ? new("Windows downloads (wimlib, xorriso)", CheckStatus.Ok, "Windows can be downloaded from Microsoft and made into an installer disc.")
            : new("Windows downloads (wimlib, xorriso)", CheckStatus.Optional, $"Downloading Windows needs {string.Join(" and ", windowsTools)} to turn Microsoft's file into an installer disc; your own Windows discs work without them.", InstallCommand(distro, "windows-download")));
        checks.Add(onPath("quickget")
            ? new("Download catalog (quickget)", CheckStatus.Ok, "Operating systems can be downloaded from the New Machine wizard.")
            : new("Download catalog (quickget)", CheckStatus.Optional,
                distro == DistroFamily.Alpine ? "quickemu is not packaged for Alpine; use your own installer discs, or install quickemu from its project page." : "Without it you supply installer discs yourself.",
                distro == DistroFamily.Alpine ? null : InstallCommand(distro, "quickemu")));

        // Bridged networking: QEMU's setuid helper, and a bridge it is allowed to use.
        var helper = new[] { "/usr/lib/qemu/qemu-bridge-helper", "/usr/libexec/qemu-bridge-helper" }.FirstOrDefault(exists);
        if (helper is null)
            checks.Add(new("Bridged networking", CheckStatus.Optional, "Machines can share this computer's connection (NAT) without it; putting them on your network directly needs QEMU's bridge helper.", InstallCommand(distro, "qemu")));
        else if (!(setuid ?? IsSetuid)(helper))
            checks.Add(new("Bridged networking", CheckStatus.Optional, "QEMU's bridge helper is installed but not allowed to set up a bridge for you. Run once:", $"sudo chmod u+s {helper}"));
        else if (!exists("/etc/qemu/bridge.conf"))
            checks.Add(new("Bridged networking", CheckStatus.Optional, "No bridge is allowed yet. Allow one (br0 here; use your bridge's name) once:", "echo 'allow br0' | sudo tee -a /etc/qemu/bridge.conf"));
        else
            checks.Add(new("Bridged networking", CheckStatus.Ok, "Machines can be put on your network through an allowed bridge (/etc/qemu/bridge.conf)."));

        if (freeBytes(libraryRoot) is { } free && free < 20L * 1024 * 1024 * 1024)
            checks.Add(new("Free disk space", CheckStatus.Warning, $"Only {free / (1024 * 1024 * 1024)} GB free where machines are kept ({libraryRoot}); a Windows machine needs about 64 GB."));
        return checks;
    }

    /// <summary>This system's own name from os-release (PRETTY_NAME, else NAME), e.g. "Ubuntu 24.04.3 LTS"; <c>null</c> if unknown.</summary>
    public static string? ThisSystemName()
    {
        try
        {
            return SystemName(File.ReadAllText("/etc/os-release"));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string? SystemName(string osRelease)
    {
        var fields = osRelease.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1].Trim('"', '\''));
        return fields.GetValueOrDefault("PRETTY_NAME") is { Length: > 0 } pretty ? pretty : fields.GetValueOrDefault("NAME") is { Length: > 0 } name ? name : null;
    }

    public static DistroFamily DetectThisDistro()
    {
        try
        {
            return DetectDistro(File.ReadAllText("/etc/os-release"));
        }
        catch (IOException)
        {
            return DistroFamily.Unknown;
        }
    }

    internal static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, name)));

    private static bool IsSetuid(string file)
    {
        try
        {
            return (File.GetUnixFileMode(file) & UnixFileMode.SetUser) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static long? FreeBytes(string path)
    {
        try
        {
            for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
                if (dir.Exists) return new DriveInfo(dir.FullName).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
