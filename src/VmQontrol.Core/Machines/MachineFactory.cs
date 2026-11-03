using System.Text;
using VmQontrol.Core.Install;
using VmQontrol.Core.Media;

namespace VmQontrol.Core.Machines;

/// <summary>Everything the New Machine wizard collects.</summary>
public sealed record NewMachineRequest
{
    public required string Name { get; init; }

    /// <summary>The library folder the machine's own folder is made in.</summary>
    public required string LibraryRoot { get; init; }

    public required GuestConfig Guest { get; init; }

    /// <summary>The installer disc, or <c>null</c> to install later.</summary>
    public string? InstallerIso { get; init; }

    /// <summary>The VirtIO driver disc for Windows (virtio-win.iso), when present.</summary>
    public string? DriversIso { get; init; }

    public long DiskGiB { get; init; } = 64;

    /// <summary><c>null</c> = recommended at every power on.</summary>
    public long? MemoryMiB { get; init; }

    public int? Cores { get; init; }

    public FirmwareKind Firmware { get; init; } = FirmwareKind.Uefi;

    public bool SecureBoot { get; init; }

    public bool Tpm { get; init; }

    /// <summary>What the system is called for people ("Ubuntu 24.04"); <c>null</c> = named from the installer.</summary>
    public string? SystemName { get; init; }

    /// <summary>Install Windows with no questions; <c>null</c> = the person installs it themselves.</summary>
    public WindowsInstallChoices? WindowsEasyInstall { get; init; }

    /// <summary>Install Linux with no questions (Ubuntu, Fedora/RHEL family, Debian discs).</summary>
    public LinuxInstallChoices? LinuxEasyInstall { get; init; }

    public string Notes { get; init; } = "";
}

/// <summary>Makes a new machine's folder from the wizard's choices.</summary>
public static class MachineFactory
{
    /// <summary>A folder name for the machine that does not exist yet in the library ("Windows 11", then "Windows 11 (2)").</summary>
    public static string FolderFor(string libraryRoot, string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '-' : c).ToArray()).Trim();
        if (clean.Length == 0) clean = "Machine";
        var folder = Path.Combine(libraryRoot, clean);
        for (var i = 2; Directory.Exists(folder) || File.Exists(folder); i++) folder = Path.Combine(libraryRoot, $"{clean} ({i})");
        return folder;
    }

    /// <summary>Creates the folder, vm.json and any answer-file disc; the disk itself is made at first power on.</summary>
    public static (MachineConfig Machine, string Directory) Create(NewMachineRequest r)
    {
        var dir = FolderFor(r.LibraryRoot, r.Name);
        Directory.CreateDirectory(dir);
        var windows = r.Guest.Os is "windows" or "windows-server";
        var drivers = windows && r.DriversIso is not null;
        var media = new List<MediaConfig>();
        if (r.InstallerIso is { } iso) media.Add(new MediaConfig { Role = MediaRole.Installer, File = iso, Connect = Connect.UntilInstalled });
        if (r.DriversIso is { } d && windows) media.Add(new MediaConfig { Role = MediaRole.Drivers, File = d });
        var answer = WriteAnswers(r, dir, drivers);
        media.AddRange(answer.Media);

        var machine = new MachineConfig
        {
            KernelBoot = answer.KernelBoot,
            Name = r.Name,
            Notes = r.Notes,
            Guest = answer.Account is null ? r.Guest : r.Guest with { Account = answer.Account },
            MemoryMiB = r.MemoryMiB,
            Cpu = new CpuConfig { Cores = r.Cores },
            Firmware = new FirmwareConfig { Kind = r.Firmware, SecureBoot = r.SecureBoot },
            Tpm = r.Tpm,
            // Without the VirtIO drivers Windows Setup cannot see a VirtIO disk or network: use the ones it knows.
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = r.DiskGiB, Bus = windows && !drivers ? DiskBus.Sata : DiskBus.Auto }],
            Nics = [new NicConfig { Model = windows && !drivers ? "e1000" : null }],
            Media = media,
            EasyInstall = answer.EasyInstall,
        };
        MachineStore.Save(machine, Path.Combine(dir, MachineStore.FileName));
        return (machine, dir);
    }

    /// <summary>
    /// Whether Easy Install can be set up on a machine that exists: its system disk is still empty, it has an installer
    /// disc, and no answer disc yet (a machine added from a folder, or made with Easy Install off).
    /// </summary>
    public static bool CanAddEasyInstall(MachineConfig m, string dir)
    {
        if (m.IsTemplate || m.EasyInstall is not null || m.Media.Any(x => x.Role == MediaRole.Unattended) || m.KernelBoot is not null) return false;
        if (m.Guest.Os is not ("windows" or "windows-server" or "linux")) return false;
        if (m.Disks.Count == 0 || DiskState.IsUsed(Path.Combine(dir, m.Disks[0].File))) return false;
        return m.Media.FirstOrDefault(x => x.Role == MediaRole.Installer) is { } installer && File.Exists(Path.Combine(dir, installer.File));
    }

    /// <summary>
    /// Easy Install for a machine that exists (<see cref="CanAddEasyInstall"/>): writes its answer disc into the machine's
    /// folder and saves the settings Easy Install needs, exactly as <see cref="Create"/> would have. The disc, disks and
    /// hardware stay; the installer may be swapped for <see cref="NewMachineRequest.InstallerIso"/>.
    /// </summary>
    public static MachineConfig AddEasyInstall(MachineConfig m, string dir, NewMachineRequest r)
    {
        var windows = r.Guest.Os is "windows" or "windows-server";
        var drivers = windows && (r.DriversIso is not null || m.Media.Any(x => x.Role == MediaRole.Drivers));
        var answer = WriteAnswers(r with { Firmware = m.Firmware.Kind }, dir, drivers);
        if (answer.EasyInstall is null) throw new InvalidOperationException("Easy Install was not asked for, or does not know this disc's installer.");
        var media = m.Media.Where(x => x.Role is not MediaRole.Unattended).ToList();
        if (r.InstallerIso is { } iso)
        {
            media.RemoveAll(x => x.Role == MediaRole.Installer);
            media.Insert(0, new MediaConfig { Role = MediaRole.Installer, File = iso, Connect = Connect.UntilInstalled });
        }

        if (r.DriversIso is { } d && windows && !media.Any(x => x.Role == MediaRole.Drivers)) media.Add(new MediaConfig { Role = MediaRole.Drivers, File = d });
        media.AddRange(answer.Media);
        var machine = m with
        {
            Guest = m.Guest with { Os = r.Guest.Os, Release = r.Guest.Release, OsInfoId = r.Guest.OsInfoId ?? m.Guest.OsInfoId, Account = answer.Account ?? m.Guest.Account },
            KernelBoot = answer.KernelBoot,
            Media = media,
            EasyInstall = answer.EasyInstall,
            // Setup must see the disk and network without VirtIO drivers (as Create does).
            Disks = windows && !drivers ? [.. m.Disks.Select((x, i) => i == 0 && x.Bus == DiskBus.Auto ? x with { Bus = DiskBus.Sata } : x)] : m.Disks,
            Nics = windows && !drivers ? [.. m.Nics.Select((n, i) => i == 0 && n.Model is null ? n with { Model = "e1000" } : n)] : m.Nics,
        };
        // A signal from an earlier, abandoned attempt would date this install wrongly.
        File.Delete(Path.Combine(dir, InstallSignal.FileName));
        MachineStore.Save(machine, Path.Combine(dir, MachineStore.FileName));
        return machine;
    }

    private sealed record Answers(List<MediaConfig> Media, KernelBootConfig? KernelBoot, string? Account, EasyInstallConfig? EasyInstall);

    /// <summary>The answer disc (Windows) or answer files and installer kernel (Linux) in <paramref name="dir"/>, as the request asks.</summary>
    private static Answers WriteAnswers(NewMachineRequest r, string dir, bool drivers)
    {
        var windows = r.Guest.Os is "windows" or "windows-server";
        if (r.WindowsEasyInstall is { } choices && windows)
        {
            var xml = WindowsAnswerFile.Build(choices with { VirtioDrivers = drivers, Uefi = r.Firmware == FirmwareKind.Uefi }, r.Guest.Release);
            var files = new Dictionary<string, byte[]> { ["autounattend.xml"] = Encoding.UTF8.GetBytes(xml) };
            if (choices.Lean) files[WindowsLean.ScriptName] = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(WindowsLean.Script().ReplaceLineEndings("\r\n"))).ToArray();
            IsoWriter.Write(Path.Combine(dir, "unattended.iso"), "UNATTEND", files);
            return new([new MediaConfig { Role = MediaRole.Unattended, File = "unattended.iso", Connect = Connect.UntilInstalled }], null, null,
                new EasyInstallConfig { System = choices.EditionName.Length > 0 ? choices.EditionName : $"Windows {r.Guest.Release}".Trim(), UserName = choices.UserName, Lean = choices.Lean });
        }

        if (r.LinuxEasyInstall is { } linux && r.InstallerIso is { } linuxIso && !windows)
        {
            var installer = LinuxEasyInstall.Detect(linuxIso);
            if (installer == LinuxInstaller.None) throw new InvalidOperationException("Easy Install does not know this disc's installer.");
            var linuxName = installer switch
            {
                LinuxInstaller.Ubuntu => "Ubuntu",
                LinuxInstaller.Debian => "Debian",
                _ when r.Guest.OsInfoId?.Contains("fedoraproject", StringComparison.Ordinal) == true => "Fedora",
                _ => null,
            };
            var (extra, boot) = LinuxEasyInstall.Prepare(installer, linuxIso, dir, linux);
            return new([.. extra], boot, LinuxEasyInstall.AccountName(linux.UserName),
                new EasyInstallConfig { System = r.SystemName ?? linuxName ?? "Linux", UserName = linux.UserName });
        }

        return new([], null, null, null);
    }
}
