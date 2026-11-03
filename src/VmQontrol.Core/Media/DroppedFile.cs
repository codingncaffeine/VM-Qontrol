namespace VmQontrol.Core.Media;

/// <summary>What a file dropped on the window becomes.</summary>
public enum DropKind
{
    None,

    /// <summary>An installer disc: the New machine wizard opens with it.</summary>
    Installer,

    /// <summary>A machine from other software, a disk, or a VM Qontrol export: Import opens with it read.</summary>
    Import,
}

/// <summary>Sorts a dropped file by what VM Qontrol can do with it (Workstation and VirtualBox take the same drops).</summary>
public static class DroppedFile
{
    private static readonly string[] ImportExtensions = [".vmx", ".vbox", ".ovf", ".ova", ".xml", ".vmdk", ".vdi", ".vhd", ".vhdx", ".qcow2", ".tar"];

    /// <summary>From the name alone, for the hint while dragging (no file is opened on the interface thread).</summary>
    public static DropKind Guess(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".iso" ? DropKind.Installer
            : ext == ".img" || ImportExtensions.Contains(ext) ? DropKind.Import
            : DropKind.None;
    }

    /// <summary>
    /// The decision, reading the file where the name cannot tell: a .img holding an ISO 9660 volume is an installer
    /// (many distributions ship hybrid images under that name), any other .img is a disk. Call it on a worker.
    /// </summary>
    public static DropKind Classify(string path)
    {
        if (!File.Exists(path)) return DropKind.None;
        var guess = Guess(path);
        if (Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase))
            return IsoVolume.Read(path) is not null ? DropKind.Installer : DropKind.Import;
        return guess;
    }
}
