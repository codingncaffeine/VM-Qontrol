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

        if (r.WindowsEasyInstall is { } choices && windows)
        {
            var xml = WindowsAnswerFile.Build(choices with { VirtioDrivers = drivers, Uefi = r.Firmware == FirmwareKind.Uefi }, r.Guest.Release);
            IsoWriter.Write(Path.Combine(dir, "unattended.iso"), "UNATTEND", new Dictionary<string, byte[]> { ["autounattend.xml"] = Encoding.UTF8.GetBytes(xml) });
            media.Add(new MediaConfig { Role = MediaRole.Unattended, File = "unattended.iso", Connect = Connect.UntilInstalled });
        }

        KernelBootConfig? kernelBoot = null;
        if (r.LinuxEasyInstall is { } linux && r.InstallerIso is { } linuxIso && !windows)
        {
            var installer = LinuxEasyInstall.Detect(linuxIso);
            if (installer == LinuxInstaller.None) throw new InvalidOperationException("Easy Install does not know this disc's installer.");
            var (extra, boot) = LinuxEasyInstall.Prepare(installer, linuxIso, dir, linux);
            media.AddRange(extra);
            kernelBoot = boot;
        }

        var machine = new MachineConfig
        {
            KernelBoot = kernelBoot,
            Name = r.Name,
            Notes = r.Notes,
            Guest = r.Guest,
            MemoryMiB = r.MemoryMiB,
            Cpu = new CpuConfig { Cores = r.Cores },
            Firmware = new FirmwareConfig { Kind = r.Firmware, SecureBoot = r.SecureBoot },
            Tpm = r.Tpm,
            // Without the VirtIO drivers Windows Setup cannot see a VirtIO disk or network: use the ones it knows.
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = r.DiskGiB, Bus = windows && !drivers ? DiskBus.Sata : DiskBus.Auto }],
            Nics = [new NicConfig { Model = windows && !drivers ? "e1000" : null }],
            Media = media,
        };
        MachineStore.Save(machine, Path.Combine(dir, MachineStore.FileName));
        return (machine, dir);
    }
}
