namespace VmQontrol.Core.Machines;

/// <summary>Checks a machine's files before power on. Each problem is one plain sentence that names the file.</summary>
public static class MachineChecks
{
    /// <summary>
    /// What is missing for the machine to start: its disk may be made at first power on, but an installer, a firmware
    /// file or a boot loader that is not there, or a "disc" that is really an error page, means it cannot boot.
    /// </summary>
    public static IReadOnlyList<string> Problems(MachineConfig m, string machineDirectory)
    {
        var problems = new List<string>();
        string Full(string f) => Path.IsPathRooted(f) ? f : Path.Combine(machineDirectory, f);
        var diskMissing = m.Disks.Count == 0 || !File.Exists(Full(m.Disks[0].File));
        var diskUnused = diskMissing || !DiskState.IsUsed(Full(m.Disks[0].File));
        foreach (var media in m.Media)
        {
            var path = Full(media.File);
            if (media.Role == MediaRole.Unattended) continue;
            if (!File.Exists(path))
            {
                // An installer is only needed until the system is installed.
                if (media.Role is MediaRole.Installer or MediaRole.Recovery && !diskUnused) continue;
                problems.Add($"The {Describe(media.Role)} {path} is missing.");
            }
            else if (media.Role is MediaRole.Installer or MediaRole.Drivers && Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase)
                     && new FileInfo(path).Length > 0 && Media.IsoVolume.Read(path) is null)
            {
                problems.Add($"The {Describe(media.Role)} {path} is not a disc image (it may be an error page saved by a failed download).");
            }
        }

        if (diskUnused && !m.Media.Any(x => x.Role is MediaRole.Installer or MediaRole.Recovery && x.Connect != Connect.Never && File.Exists(Full(x.File)))
            && !(m.Disks.Count > 0 && !diskMissing))
            problems.Add("The machine has no disk yet and nothing to install from.");
        return problems;
    }

    private static string Describe(MediaRole role) => role switch
    {
        MediaRole.Installer => "installer disc",
        MediaRole.Drivers => "driver disc",
        MediaRole.Recovery => "recovery image",
        MediaRole.Bootloader => "boot loader",
        MediaRole.Floppy => "floppy image",
        MediaRole.CloudInit => "cloud-init disc",
        _ => "file",
    };
}
