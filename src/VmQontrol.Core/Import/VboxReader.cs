using System.Globalization;
using System.Xml.Linq;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Import;

/// <summary>Reads a VirtualBox machine (.vbox XML: Machine, Hardware, StorageControllers, MediaRegistry).</summary>
public static class VboxReader
{
    public static ForeignMachine Read(string vbox)
    {
        vbox = Path.GetFullPath(vbox);
        var dir = Path.GetDirectoryName(vbox)!;
        var doc = XDocument.Load(vbox);
        IEnumerable<XElement> All(string name) => doc.Descendants().Where(e => e.Name.LocalName == name);
        XElement? One(string name) => All(name).FirstOrDefault();
        string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;
        string Resolve(string f) => Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(dir, f));
        var notes = new List<string>();
        var problems = new List<string>();

        var machine = One("Machine") ?? throw new InvalidDataException("This is not a VirtualBox machine file.");
        var osType = Attr(machine, "OSType") ?? "Other";

        // Every disc and disk image the machine knows, by uuid; a differencing image (a snapshot) sits inside its parent.
        var images = new Dictionary<string, (string Location, string Format, bool Child)>(StringComparer.OrdinalIgnoreCase);
        foreach (var hd in All("HardDisk"))
        {
            if (Attr(hd, "uuid") is not { } id || Attr(hd, "location") is not { } loc) continue;
            images[id] = (Resolve(loc), Attr(hd, "format") ?? "VDI", hd.Parent?.Name.LocalName == "HardDisk");
        }

        foreach (var img in All("DVDImages").SelectMany(d => d.Elements().Where(e => e.Name.LocalName == "Image")))
            if (Attr(img, "uuid") is { } id && Attr(img, "location") is { } loc) images[id] = (Resolve(loc), "RAW", false);

        var disks = new List<ForeignDisk>();
        var discs = new List<string>();
        foreach (var controller in All("StorageController"))
        {
            var type = Attr(controller, "type") ?? "AHCI";
            var bus = type switch
            {
                "NVMe" => DiskBus.Nvme,
                "VirtioSCSI" => DiskBus.Virtio,
                _ => DiskBus.Sata,
            };
            if (type is "LsiLogic" or "BusLogic" or "LsiLogicSas")
                notes.Add($"The disk on VirtualBox's {type} SCSI controller is connected as SATA here. If it does not boot, choose another disk type in Settings.");
            foreach (var device in controller.Elements().Where(e => e.Name.LocalName == "AttachedDevice"))
            {
                var uuid = Attr(device.Elements().FirstOrDefault(e => e.Name.LocalName == "Image"), "uuid");
                if (uuid is null || !images.TryGetValue(uuid, out var image)) continue;
                if (Attr(device, "type") == "DVD")
                {
                    if (File.Exists(image.Location)) discs.Add(image.Location);
                }
                else if (Attr(device, "type") == "HardDisk")
                {
                    if (image.Child && image.Format.Equals("VDI", StringComparison.OrdinalIgnoreCase))
                        problems.Add("The machine has VirtualBox snapshots, whose disk changes QEMU cannot read. In VirtualBox, delete the snapshots (Snapshots, Delete) or clone the machine with \"Current machine state\", then import that.");
                    else
                        disks.Add(new ForeignDisk(image.Location, bus));
                }
            }
        }

        var nics = new List<NicConfig>();
        foreach (var adapter in All("Adapter").Where(a => Attr(a, "enabled") == "true"))
        {
            var type = Attr(adapter, "type") ?? "82540EM";
            var model = type switch
            {
                "virtio" => "virtio-net-pci",
                "Am79C970A" or "Am79C973" or "Am79C960" => "pcnet",
                _ => "e1000",
            };
            if (adapter.Elements().Any(e => e.Name.LocalName == "BridgedInterface")) notes.Add("A network adapter was bridged; it uses NAT here until you choose a bridge in Settings.");
            if (adapter.Elements().Any(e => e.Name.LocalName is "HostOnlyInterface" or "InternalNetwork"))
                notes.Add("A network adapter was on a host-only or internal network; it uses NAT here (a private segment in Settings is the nearest match).");
            var mac = Attr(adapter, "MACAddress");
            nics.Add(new NicConfig { Model = model, Mac = mac is { Length: 12 } ? string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))).ToLowerInvariant() : null });
        }

        if (nics.Count > 1) notes.Add($"VM Qontrol connects one network adapter; the other {nics.Count - 1} were left out.");
        var efi = Attr(One("Firmware"), "type") is { } fw && fw.StartsWith("EFI", StringComparison.OrdinalIgnoreCase);
        var tpm = Attr(One("TrustedPlatformModule"), "type") is { } t && t != "None";
        if (tpm) notes.Add("The machine had a virtual TPM; it gets a new, empty one. If Windows used BitLocker, have its recovery key at hand.");
        if (osType.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || osType.Contains("Linux", StringComparison.OrdinalIgnoreCase) || osType.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase))
            notes.Add("VirtualBox Guest Additions stay installed in the guest; they do nothing here. Uninstall them and install the QEMU guest tools (Summary, Guest tools).");
        if (Attr(machine, "stateFile") is { Length: > 0 }) notes.Add("The machine was saved (suspended) in VirtualBox; it starts afresh here. Start and shut it down in VirtualBox first to keep what was open.");

        return new ForeignMachine
        {
            Kind = "VirtualBox",
            Source = vbox,
            Name = Attr(machine, "name") ?? Path.GetFileNameWithoutExtension(vbox),
            Guest = ForeignGuest.From(osType),
            SourceOs = osType,
            MemoryMiB = long.TryParse(Attr(One("Memory"), "RAMSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mem) ? mem : null,
            Cores = int.TryParse(Attr(One("CPU"), "count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpus) ? cpus : 1,
            Firmware = efi ? FirmwareKind.Uefi : FirmwareKind.Bios,
            SecureBoot = efi && One("SecureBoot") is { } sb && Attr(sb, "enabled") == "true",
            Tpm = tpm,
            Disks = disks,
            Discs = discs,
            Nics = nics.Take(1).ToList(),
            Notes = notes,
            Problems = problems,
        };
    }
}
