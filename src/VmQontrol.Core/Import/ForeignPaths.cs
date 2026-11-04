namespace VmQontrol.Core.Import;

/// <summary>
/// What an imported machine's own files may name. A descriptor (.ovf, .vmx, .vbox, libvirt .xml) is someone else's file
/// and may come from anywhere: a path in it is copied into the new machine (or attached as a disc), which would hand the
/// guest any host file it names. So a disk or disc it names must be an image by its kind (its extension), a regular
/// file (not a device, nor a link to something else), and not in the system's own folders.
/// </summary>
public static class ForeignPaths
{
    private static readonly IReadOnlyList<string> DiskKinds = [.. ImportFormats.DiskExtensions, ".avhdx"];
    private static readonly string[] DiscKinds = [".iso"];
    private static readonly string[] SystemFolders = ["/etc", "/proc", "/sys", "/dev", "/boot", "/root", "/usr", "/bin", "/sbin", "/lib", "/lib64", "/var/log", "/var/spool", "/var/mail", "/var/cache", "/var/run"];

    /// <summary>Why <paramref name="path"/> is refused as a disk (or, with <paramref name="disc"/>, a disc); <c>null</c> = fine.</summary>
    public static string? Problem(string path, bool disc = false)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full) && new FileInfo(full).LinkTarget is null) return $"{path} is not a file (it is not there).";
            // A link is judged by where it leads.
            if (File.ResolveLinkTarget(full, returnFinalTarget: true) is { } target) full = target.FullName;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return $"{path} cannot be looked at ({ex.Message}).";
        }

        var kinds = disc ? DiscKinds : DiskKinds;
        if (!kinds.Any(k => full.EndsWith(k, StringComparison.OrdinalIgnoreCase)))
            return $"{path} is not a {(disc ? "disc" : "disk")} image ({string.Join(", ", kinds)}), so it is not copied.";
        // /run holds sockets and runtime state, except /run/media, where other drives are mounted.
        if (SystemFolders.Any(f => full == f || full.StartsWith(f + "/", StringComparison.Ordinal))
            || full.StartsWith("/run/", StringComparison.Ordinal) && !full.StartsWith("/run/media/", StringComparison.Ordinal))
            return $"{path} is in a system folder, so it is not copied.";
        try
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0 || !File.Exists(full)) return $"{path} is not a file.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path} cannot be looked at ({ex.Message}).";
        }

        return null;
    }

    /// <summary>
    /// Whether a disk this account cannot read may be copied through the system's password prompt: only one the person
    /// picked themselves, or one in libvirt's own image folder named by a libvirt machine (libvirt keeps those root's).
    /// </summary>
    public static bool MayElevate(ForeignMachine machine, string path) =>
        machine.Kind == "disk"
        || machine.Kind == "libvirt" && Path.GetFullPath(path).StartsWith("/var/lib/libvirt/", StringComparison.Ordinal);
}
