using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Machines;

/// <summary>What happens to a machine's changes when it powers off.</summary>
public enum DiscardMode
{
    /// <summary>Kept, as on a real computer (the default).</summary>
    Keep,

    /// <summary>Thrown away: every power on starts from the disks as they were (Parallels' rollback mode, UTM's
    /// disposable mode, Workstation's nonpersistent disks).</summary>
    Discard,

    /// <summary>The app asks at power off; a session nobody answered for is thrown away at the next power on.</summary>
    Ask,
}

/// <summary>
/// The throwaway layer behind <see cref="DiscardMode"/>: at power on every disk gets an overlay in the machine's
/// <c>session/</c> folder, backed by the real disk, and QEMU writes only to the overlay. Discard deletes the folder;
/// Keep merges each overlay into its disk (<c>qemu-img commit</c>). The real disks are opened read-only by QEMU while
/// a session runs, so nothing can change them until the person chooses Keep.
/// </summary>
public static class DiscardSession
{
    public const string FolderName = "session";

    public static string Folder(string machineDirectory) => Path.Combine(machineDirectory, FolderName);

    /// <summary>A session's overlays are on disk: the machine runs one, or one ended and waits for Keep or Discard.</summary>
    public static bool Exists(string machineDirectory) =>
        Directory.Exists(Folder(machineDirectory)) && Directory.EnumerateFiles(Folder(machineDirectory), "*.qcow2").Any();

    private static string Overlay(string machineDirectory, int index) => Path.Combine(Folder(machineDirectory), $"disk{index}.qcow2");

    private static string Full(string machineDirectory, string file) => Path.IsPathRooted(file) ? file : Path.Combine(machineDirectory, file);

    /// <summary>
    /// Starts a session: throws away one left from before (nobody chose Keep), makes a fresh overlay for each disk that
    /// exists, and returns the settings QEMU runs with (the disks swapped for their overlays). A disk not made yet is
    /// left as it is: it is created empty at this power on and has nothing to protect.
    /// </summary>
    public static async Task<MachineConfig> BeginAsync(MachineConfig m, string machineDirectory, CancellationToken ct = default)
    {
        Discard(machineDirectory);
        Directory.CreateDirectory(Folder(machineDirectory));
        var disks = new List<DiskConfig>();
        for (var i = 0; i < m.Disks.Count; i++)
        {
            var disk = m.Disks[i];
            var real = Full(machineDirectory, disk.File);
            if (!File.Exists(real))
            {
                disks.Add(disk);
                continue;
            }

            var overlay = Overlay(machineDirectory, i);
            await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", Path.GetFullPath(real), "-F", disk.Format, overlay).ConfigureAwait(false);
            disks.Add(disk with { File = overlay, Format = "qcow2" });
        }

        return m with { Disks = disks };
    }

    /// <summary>Throws the session's changes away: the next power on sees the disks as they were before it.</summary>
    public static void Discard(string machineDirectory)
    {
        if (Directory.Exists(Folder(machineDirectory))) Directory.Delete(Folder(machineDirectory), recursive: true);
    }

    /// <summary>Writes the session's changes into the real disks, then removes the overlays.</summary>
    public static async Task KeepAsync(MachineConfig m, string machineDirectory, CancellationToken ct = default)
    {
        for (var i = 0; i < m.Disks.Count; i++)
        {
            var overlay = Overlay(machineDirectory, i);
            if (File.Exists(overlay)) await QemuImg.RunAsync(ct, "commit", "-q", overlay).ConfigureAwait(false);
        }

        Discard(machineDirectory);
    }
}
