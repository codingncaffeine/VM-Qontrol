namespace VmQontrol.Core.Hosts;

/// <summary>A PCI device of this computer, as the passthrough picker shows it.</summary>
public sealed record PciDevice
{
    public required string Address { get; init; }

    public required string Name { get; init; }

    /// <summary>"0x030000": the first two bytes are the class (03 display, 0c03 USB, 0108 NVMe, 02 network).</summary>
    public required string Class { get; init; }

    public string? Driver { get; init; }

    /// <summary>The IOMMU group: every device in it goes to the machine together. <c>null</c> = no IOMMU, no passthrough.</summary>
    public string? Group { get; init; }

    /// <summary>Why handing it over would take something this computer is using, or <c>null</c>.</summary>
    public string? HostUse { get; init; }

    /// <summary>Why the device's IOMMU group cannot go (another device in it is one this computer needs), or <c>null</c>.</summary>
    public string? GroupUse { get; init; }

    /// <summary>The reason this device cannot be handed over: its own use, or its group's.</summary>
    public string? Blocked => HostUse ?? GroupUse;

    public bool BoundToVfio => Driver == "vfio-pci";

    /// <summary>Bridges are not handed over; they stay with the host even when they share the group.</summary>
    public bool IsBridge => Class.StartsWith("0x0604", StringComparison.Ordinal) || Class.StartsWith("0x0600", StringComparison.Ordinal);

    public string Kind => Class.Length >= 6 ? Class[2..6] switch
    {
        "0300" or "0302" or "0380" => "Graphics",
        "0403" or "0401" => "Audio",
        "0c03" => "USB controller",
        "0108" => "NVMe disk",
        "0106" => "SATA controller",
        "0200" => "Network",
        "0280" => "Wireless",
        _ => "Device",
    } : "Device";
}

/// <summary>
/// This computer's PCI devices with their IOMMU groups (sysfs), named from pci.ids, and what each is doing for the host:
/// the screen it boots on, the USB devices on a controller, the disks on an NVMe drive, the network in use.
/// </summary>
public static class PciHost
{
    private static readonly Lazy<Dictionary<string, string>> Names = new(LoadPciIds);

    public static IReadOnlyList<PciDevice> List(string sys = "/sys/bus/pci/devices", IReadOnlyList<Devices.HostDrive>? drives = null)
    {
        var list = new List<PciDevice>();
        if (!Directory.Exists(sys)) return list;
        drives ??= Devices.HostDrives.List();
        foreach (var dir in Directory.EnumerateFileSystemEntries(sys).Order(StringComparer.Ordinal))
        {
            var address = Path.GetFileName(dir);
            string? Read(string f) { try { return File.Exists(Path.Combine(dir, f)) ? File.ReadAllText(Path.Combine(dir, f)).Trim() : null; } catch (IOException) { return null; } }
            string? Link(string f) { try { return File.ResolveLinkTarget(Path.Combine(dir, f), false) is { } t ? Path.GetFileName(t.FullName.TrimEnd('/')) : null; } catch (IOException) { return null; } }
            var vendor = (Read("vendor") ?? "0x0000")[2..];
            var device = (Read("device") ?? "0x0000")[2..];
            var cls = Read("class") ?? "0x000000";
            var name = Names.Value.GetValueOrDefault($"{vendor}:{device}") is { } d
                ? (Names.Value.GetValueOrDefault(vendor) is { } v ? $"{v} {d}" : d)
                : $"PCI device {vendor}:{device}";
            list.Add(new PciDevice
            {
                Address = address, Name = name, Class = cls, Driver = Link("driver"), Group = Link("iommu_group"),
                HostUse = HostUse(dir, address, cls, Read("boot_vga"), drives),
            });
        }

        // A group goes whole: a device is blocked by anything in its group this computer needs.
        return list.Select(d => d.HostUse is null && d.Group is not null && list.FirstOrDefault(o => o.Group == d.Group && o.Address != d.Address && !o.IsBridge && o.HostUse is not null) is { } needed
            ? d with { GroupUse = $"its IOMMU group also holds {needed.Name} ({needed.Address}), and {needed.HostUse}" }
            : d).ToList();
    }

    /// <summary>The devices sharing <paramref name="device"/>'s IOMMU group (bridges left out: they stay with the host).</summary>
    public static IReadOnlyList<PciDevice> GroupOf(PciDevice device, IReadOnlyList<PciDevice> all) =>
        device.Group is null ? [device] : all.Where(d => d.Group == device.Group && !d.IsBridge).ToList();

    private static string? HostUse(string dir, string address, string cls, string? bootVga, IReadOnlyList<Devices.HostDrive> drives)
    {
        if (bootVga == "1") return "it shows this computer's screen";
        // Disks below this device, whatever the bus (NVMe, SATA, USB storage): the disk's own sysfs path runs through it.
        var under = drives.Where(d => d.Parent is null && DevicePath(d.Name)?.Contains("/" + address + "/", StringComparison.Ordinal) == true).ToList();
        if (under.Any(d => d.HoldsSystem)) return "it holds this computer's system disk";
        if (under.Any(d => d.InUse)) return $"it holds disks in use here ({string.Join(", ", under.SelectMany(d => d.Mounts).Distinct())})";

        if (cls.StartsWith("0x0c03", StringComparison.Ordinal))
        {
            var products = SafeEnumerate(dir, "product").Select(f => { try { return File.ReadAllText(f).Trim(); } catch (IOException) { return ""; } })
                .Where(p => p.Length > 0).Distinct().ToList();
            if (products.Count > 0) return $"USB devices are plugged into it: {string.Join(", ", products.Take(4))}{(products.Count > 4 ? "…" : "")}";
        }

        if (cls.StartsWith("0x02", StringComparison.Ordinal))
        {
            var up = SafeEnumerate(dir, "operstate").Where(f => { try { return File.ReadAllText(f).Trim() == "up"; } catch (IOException) { return false; } }).ToList();
            if (up.Count > 0) return "this computer's network connection uses it";
        }

        return null;
    }

    private static string? DevicePath(string block)
    {
        try
        {
            return File.ResolveLinkTarget($"/sys/class/block/{block}", returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Files named <paramref name="leaf"/> under the device, a few levels down, without following loops.</summary>
    private static IEnumerable<string> SafeEnumerate(string dir, string leaf)
    {
        var found = new List<string>();
        void Walk(string d, int depth)
        {
            if (depth > 6) return;
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(d).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var e in entries)
            {
                var n = Path.GetFileName(e);
                var isLink = new FileInfo(e).LinkTarget is not null;
                if (n == leaf)
                {
                    found.Add(e);
                    continue;
                }

                if (!isLink && Directory.Exists(e) && n is not ("power" or "subsystem" or "driver" or "firmware_node" or "iommu_group" or "iommu" or "msi_irqs")) Walk(e, depth + 1);
            }
        }

        Walk(dir, 0);
        return found;
    }

    private static Dictionary<string, string> LoadPciIds()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = new[] { "/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids" }.FirstOrDefault(File.Exists);
        if (file is null) return map;
        string? vendor = null;
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length < 6 || line[0] == '#') continue;
            if (line.StartsWith("C ", StringComparison.Ordinal)) break; // the class list follows the vendors
            if (line[0] != '\t')
            {
                vendor = line[..4];
                map[vendor] = line[6..].Trim();
            }
            else if (line[1] != '\t' && vendor is not null)
            {
                map[$"{vendor}:{line.Substring(1, 4)}"] = line[7..].Trim();
            }
        }

        return map;
    }
}
