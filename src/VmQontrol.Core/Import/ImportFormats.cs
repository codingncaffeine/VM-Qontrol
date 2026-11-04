namespace VmQontrol.Core.Import;

/// <summary>One kind of file VM Qontrol brings a machine in from, in words for the wizard and the import page.</summary>
/// <param name="From">Where such a file comes from ("VMware Workstation, Player, Fusion").</param>
/// <param name="Extensions">Its extensions, lowercase with the dot.</param>
/// <param name="Brings">What comes across with it, in words.</param>
/// <param name="IsDisk">A disk on its own rather than a machine's settings file.</param>
public sealed record ImportFormat(string From, IReadOnlyList<string> Extensions, string Brings, bool IsDisk = false);

/// <summary>
/// Every file VM Qontrol imports a machine from: the one list the wizard, the import page, the file pickers and
/// drag-and-drop read (they once each kept their own and disagreed). Most used first: VMware and VirtualBox.
/// </summary>
public static class ImportFormats
{
    public static IReadOnlyList<ImportFormat> All { get; } =
    [
        new("VMware Workstation, Player and Fusion", [".vmx"], "processors, memory, firmware (Secure Boot, TPM), disks, network and the system"),
        new("VirtualBox", [".vbox"], "processors, memory, firmware (Secure Boot, TPM), disks, discs, network and the system"),
        new("An appliance (OVF, from VMware, VirtualBox and others)", [".ova", ".ovf"], "processors, memory, firmware, disks and the system"),
        new("Hyper-V", [".vmcx"], "its disk; firmware and system recognized from the disk (Hyper-V's settings file is readable by Hyper-V only)"),
        new("virt-manager, GNOME Boxes and libvirt", [".xml"], "processors, memory, firmware (Secure Boot, TPM), disks, network and the system"),
        new("VM Qontrol (an export)", [".tar"], "everything: disks, firmware, TPM, snapshots and history"),
        new("A disk on its own", [".vmdk", ".vdi", ".vhdx", ".vhd", ".qcow2", ".qcow", ".qed", ".hdd", ".img", ".raw"],
            "firmware and system recognized from the disk's partitions", IsDisk: true),
    ];

    /// <summary>Every machine-file extension (not disks).</summary>
    public static IReadOnlyList<string> MachineExtensions { get; } = [.. All.Where(f => !f.IsDisk).SelectMany(f => f.Extensions)];

    /// <summary>Every disk extension.</summary>
    public static IReadOnlyList<string> DiskExtensions { get; } = [.. All.Where(f => f.IsDisk).SelectMany(f => f.Extensions)];

    public static IReadOnlyList<string> AllExtensions { get; } = [.. MachineExtensions, .. DiskExtensions];

    /// <summary>File picker patterns ("*.vmx", "*.VMX") for some extensions.</summary>
    public static IReadOnlyList<string> Patterns(IEnumerable<string> extensions) =>
        [.. extensions.SelectMany(e => new[] { "*" + e, "*" + e.ToUpperInvariant() })];

    /// <summary>"vmx, vbox, ova…" for a sentence.</summary>
    public static string Names(IEnumerable<string> extensions) => string.Join(", ", extensions);
}
