using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Import;

/// <summary>
/// Reads a libvirt domain (virt-manager, GNOME Boxes): its XML from a file, or from <c>virsh dumpxml</c> for a defined
/// machine. Only reads; the domain stays defined in libvirt.
/// </summary>
public static class LibvirtReader
{
    /// <summary>The machines libvirt knows on <paramref name="uri"/> (qemu:///session, qemu:///system), by name; empty when virsh is missing.</summary>
    public static async Task<IReadOnlyList<string>> DomainsAsync(string uri, CancellationToken ct)
    {
        var output = await VirshAsync(ct, "-c", uri, "list", "--all", "--name").ConfigureAwait(false);
        return output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }

    public static async Task<string?> DumpXmlAsync(string uri, string domain, CancellationToken ct) =>
        await VirshAsync(ct, "-c", uri, "dumpxml", "--inactive", domain).ConfigureAwait(false);

    private static async Task<string?> VirshAsync(CancellationToken ct, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("virsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static ForeignMachine Read(string xml, string source)
    {
        var doc = XDocument.Parse(xml);
        var domain = doc.Root is { Name.LocalName: "domain" } root ? root : throw new InvalidDataException("This is not a libvirt domain description.");
        XElement? Path1(params string[] names)
        {
            XElement? e = domain;
            foreach (var n in names) e = e?.Elements().FirstOrDefault(x => x.Name.LocalName == n);
            return e;
        }

        string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;
        var notes = new List<string>();
        var problems = new List<string>();

        long? memory = null;
        if (Path1("memory") is { } mem && long.TryParse(mem.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
        {
            memory = (Attr(mem, "unit") ?? "KiB") switch
            {
                "b" or "bytes" => amount / (1024 * 1024),
                "KiB" or "k" => amount / 1024,
                "MiB" or "M" => amount,
                "GiB" or "G" => amount * 1024,
                _ => amount / 1024,
            };
        }

        var cores = int.TryParse(Path1("vcpu")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;
        var os = Path1("os");
        var loader = os?.Elements().FirstOrDefault(e => e.Name.LocalName == "loader");
        var efi = Attr(os, "firmware") == "efi" || (loader is not null && (loader.Value.Contains("OVMF", StringComparison.OrdinalIgnoreCase) || loader.Value.Contains("edk2", StringComparison.OrdinalIgnoreCase)));
        var secure = Attr(loader, "secure") == "yes" || os?.Descendants().Any(e => e.Name.LocalName == "feature" && Attr(e, "name") == "secure-boot" && Attr(e, "enabled") == "yes") == true;
        // libosinfo's id names the system exactly (virt-manager writes it).
        var osId = domain.Descendants().FirstOrDefault(e => e.Name.LocalName == "os" && e.Attribute("id") is not null)?.Attribute("id")?.Value;

        var devices = Path1("devices");
        var disks = new List<ForeignDisk>();
        var discs = new List<string>();
        var windows = osId?.Contains("microsoft", StringComparison.Ordinal) == true;
        foreach (var disk in devices?.Elements().Where(e => e.Name.LocalName == "disk") ?? [])
        {
            var file = Attr(disk.Elements().FirstOrDefault(e => e.Name.LocalName == "source"), "file") ?? Attr(disk.Elements().FirstOrDefault(e => e.Name.LocalName == "source"), "dev");
            if (file is null) continue;
            if (Attr(disk, "device") == "cdrom") { if (File.Exists(file)) discs.Add(file); continue; }
            if (Attr(disk, "device") is not (null or "disk")) continue;
            if (Attr(disk, "type") == "block") notes.Add($"{file} is a block device; its contents are copied into a disk file.");
            var bus = Attr(disk.Elements().FirstOrDefault(e => e.Name.LocalName == "target"), "bus") switch
            {
                "virtio" => DiskBus.Virtio,
                "nvme" => DiskBus.Nvme,
                "scsi" when !windows => DiskBus.Virtio,
                _ => DiskBus.Sata,
            };
            disks.Add(new ForeignDisk(file, bus));
        }

        var nics = new List<NicConfig>();
        foreach (var nic in devices?.Elements().Where(e => e.Name.LocalName == "interface") ?? [])
        {
            var model = Attr(nic.Elements().FirstOrDefault(e => e.Name.LocalName == "model"), "type") switch
            {
                "virtio" => "virtio-net-pci",
                "e1000e" => "e1000e",
                "rtl8139" => "rtl8139",
                "vmxnet3" => "vmxnet3",
                _ => "e1000",
            };
            if (Attr(nic, "type") == "bridge") notes.Add("A network adapter was on a bridge; it uses NAT here until you choose the bridge in Settings.");
            nics.Add(new NicConfig { Model = model, Mac = Attr(nic.Elements().FirstOrDefault(e => e.Name.LocalName == "mac"), "address") });
        }

        if (nics.Count > 1) notes.Add($"VM Qontrol connects one network adapter; the other {nics.Count - 1} were left out.");
        var tpm = devices?.Elements().Any(e => e.Name.LocalName == "tpm") == true;
        if (tpm) notes.Add("The machine had a TPM; it gets a new, empty one. If Windows used BitLocker, have its recovery key at hand.");
        if (devices?.Elements().Any(e => e.Name.LocalName == "hostdev") == true) notes.Add("Devices passed through from this computer were left out; add them again in Settings.");
        if (disks.Count == 0) notes.Add("No disk was found in the description.");
        var name = Path1("name")?.Value ?? "Imported machine";

        return new ForeignMachine
        {
            Kind = "libvirt",
            Source = source,
            Name = Path1("title")?.Value is { Length: > 0 } title ? title : name,
            Guest = osId is not null ? ForeignGuest.From(osId) with { OsInfoId = osId } : new GuestConfig { Os = "linux" },
            SourceOs = osId ?? "",
            MemoryMiB = memory,
            Cores = cores,
            Firmware = efi ? FirmwareKind.Uefi : FirmwareKind.Bios,
            SecureBoot = efi && secure,
            Tpm = tpm,
            Disks = disks,
            Discs = discs,
            Nics = nics.Take(1).ToList(),
            Notes = notes,
            Problems = problems,
        };
    }
}
