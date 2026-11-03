using System.Formats.Tar;
using System.Globalization;
using System.Xml.Linq;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Import;

/// <summary>
/// Reads an OVF appliance (.ovf with its disks beside it) or an .ova (the same, in one tar file, unpacked into a folder
/// the caller names, inside the library so a large disk never lands in /tmp). OVF 1.x and 2.x; VMware's and
/// VirtualBox's extensions where they say more (vmw:osType, vmw:Config firmware).
/// </summary>
public static class OvfReader
{
    /// <summary>Unpacks <paramref name="ova"/> into <paramref name="folder"/> and returns the .ovf in it.</summary>
    public static string Unpack(string ova, string folder)
    {
        Directory.CreateDirectory(folder);
        using var stream = File.OpenRead(ova);
        using var reader = new TarReader(stream);
        string? ovf = null;
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            // Only plain names: an appliance's files sit side by side; anything with a path is refused.
            var name = Path.GetFileName(entry.Name);
            if (name.Length == 0 || name != entry.Name.TrimStart('.', '/')) throw new InvalidDataException($"The appliance holds a file with a path ({entry.Name}); it was not unpacked.");
            var target = Path.Combine(folder, name);
            entry.ExtractToFile(target, overwrite: true);
            if (name.EndsWith(".ovf", StringComparison.OrdinalIgnoreCase)) ovf ??= target;
        }

        return ovf ?? throw new InvalidDataException("The .ova holds no .ovf description.");
    }

    public static ForeignMachine Read(string ovf, string? temporaryFolder = null, string? source = null)
    {
        ovf = Path.GetFullPath(ovf);
        var dir = Path.GetDirectoryName(ovf)!;
        var doc = XDocument.Load(ovf);
        IEnumerable<XElement> All(XContainer c, string name) => c.Descendants().Where(e => e.Name.LocalName == name);
        string? AttrLocal(XElement? e, string name) => e?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
        string? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
        var notes = new List<string>();
        var problems = new List<string>();

        var files = All(doc, "File").Where(f => AttrLocal(f, "id") is not null).ToDictionary(f => AttrLocal(f, "id")!, f => AttrLocal(f, "href") ?? "", StringComparer.Ordinal);
        var diskFiles = All(doc, "Disk").Where(d => AttrLocal(d, "diskId") is not null)
            .ToDictionary(d => AttrLocal(d, "diskId")!, d => files.TryGetValue(AttrLocal(d, "fileRef") ?? "", out var href) ? href : "", StringComparer.Ordinal);
        var system = All(doc, "VirtualSystem").FirstOrDefault() ?? throw new InvalidDataException("The appliance describes no machine.");
        if (All(doc, "VirtualSystem").Count() > 1) notes.Add("The appliance holds several machines; the first is imported.");

        var os = All(system, "OperatingSystemSection").FirstOrDefault();
        var osType = AttrLocal(os, "osType") ?? (os is null ? null : Child(os, "Description")) ?? "other";
        var items = All(system, "Item").Concat(All(system, "StorageItem")).Concat(All(system, "EthernetPortItem")).ToList();
        string Kind(XElement item) => Child(item, "ResourceType") ?? "";
        var controllers = items.Where(i => Kind(i) is "5" or "6" or "20").ToDictionary(i => Child(i, "InstanceID") ?? "", i => i, StringComparer.Ordinal);

        long? memory = null;
        int? cores = null;
        var disks = new List<ForeignDisk>();
        var nics = new List<NicConfig>();
        foreach (var item in items)
        {
            switch (Kind(item))
            {
                case "3":
                    cores = int.TryParse(Child(item, "VirtualQuantity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : null;
                    break;
                case "4":
                    if (long.TryParse(Child(item, "VirtualQuantity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q))
                    {
                        var units = (Child(item, "AllocationUnits") ?? "byte * 2^20").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
                        memory = units switch
                        {
                            "byte*2^30" or "gigabytes" => q * 1024,
                            "byte*2^10" or "kilobytes" => q / 1024,
                            "byte" or "bytes" => q / (1024 * 1024),
                            _ => q,
                        };
                    }

                    break;
                case "17":
                {
                    var host = Child(item, "HostResource") ?? "";
                    var id = host.Split('/').Last();
                    var href = diskFiles.TryGetValue(id, out var d) ? d : files.TryGetValue(id, out var f) ? f : "";
                    if (href.Length == 0) break;
                    var path = Path.Combine(dir, href);
                    if (!File.Exists(path)) { problems.Add($"The disk {href} is not beside the .ovf."); break; }
                    var parent = controllers.TryGetValue(Child(item, "Parent") ?? "", out var p) ? p : null;
                    var sub = (parent is null ? "" : Child(parent, "ResourceSubType") ?? "").ToLowerInvariant();
                    var bus = parent is null ? DiskBus.Sata : Kind(parent) switch
                    {
                        "6" when sub.Contains("virtualscsi", StringComparison.Ordinal) || sub.Contains("pvscsi", StringComparison.Ordinal) => DiskBus.Pvscsi,
                        _ when sub.Contains("nvme", StringComparison.Ordinal) => DiskBus.Nvme,
                        _ => DiskBus.Sata,
                    };
                    disks.Add(new ForeignDisk(path, bus));
                    break;
                }

                case "10":
                {
                    var sub = (Child(item, "ResourceSubType") ?? "E1000").ToLowerInvariant();
                    var model = sub switch
                    {
                        "vmxnet3" => "vmxnet3",
                        "e1000e" => "e1000e",
                        "pcnet32" => "pcnet",
                        "virtio" => "virtio-net-pci",
                        _ => "e1000",
                    };
                    nics.Add(new NicConfig { Model = model, Mac = Child(item, "Address")?.ToLowerInvariant() });
                    break;
                }
            }
        }

        if (nics.Count > 1) notes.Add($"VM Qontrol connects one network adapter; the other {nics.Count - 1} were left out.");
        var config = All(system, "Config").ToDictionary(e => AttrLocal(e, "key") ?? "", e => AttrLocal(e, "value") ?? "", StringComparer.OrdinalIgnoreCase);
        var efi = config.TryGetValue("firmware", out var fw) && fw.Equals("efi", StringComparison.OrdinalIgnoreCase);
        if (!efi && All(doc, "Firmware").FirstOrDefault() is { } vboxFw && (AttrLocal(vboxFw, "type") ?? "").StartsWith("EFI", StringComparison.OrdinalIgnoreCase)) efi = true;

        return new ForeignMachine
        {
            Kind = "OVF",
            Source = source ?? ovf,
            Name = Child(system, "Name") ?? AttrLocal(system, "id") ?? Path.GetFileNameWithoutExtension(ovf),
            Guest = ForeignGuest.From(osType),
            SourceOs = osType,
            MemoryMiB = memory,
            Cores = cores,
            Firmware = efi ? FirmwareKind.Uefi : FirmwareKind.Bios,
            SecureBoot = efi && config.TryGetValue("uefi.secureBoot.enabled", out var sb) && sb.Equals("true", StringComparison.OrdinalIgnoreCase),
            Disks = disks,
            Nics = nics.Take(1).ToList(),
            Notes = notes,
            Problems = problems,
            TemporaryFolder = temporaryFolder,
        };
    }
}
