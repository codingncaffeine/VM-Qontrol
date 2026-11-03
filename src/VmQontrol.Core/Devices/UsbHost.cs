using System.Globalization;
using System.Text.Json.Nodes;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Devices;

/// <summary>A USB device plugged into this computer.</summary>
public sealed record HostUsbDevice(string VendorId, string ProductId, string Name, string Bus, string Device, string Port, int SpeedMbps, bool Writable, bool IsHub)
{
    /// <summary>"046d:c52b"</summary>
    public string Id => $"{VendorId}:{ProductId}";

    public string DevicePath => $"/dev/bus/usb/{Bus}/{Device}";

    /// <summary>The QEMU device id used while it is attached to a machine: by its port, which a device keeps when it re-enumerates.</summary>
    public string QemuId => $"usb-{VendorId}-{ProductId}-{Port}";

    /// <summary>The port within the bus as QEMU's usb-host hostport takes it ("3.3" for sysfs "10-3.3").</summary>
    public string HostPort => Port.Contains('-') ? Port[(Port.IndexOf('-') + 1)..] : Port;

    /// <summary>The QEMU device id of the machine's rule for this kind of device (see <see cref="UsbHost.RuleId"/>).</summary>
    public string RuleId => UsbHost.RuleId(VendorId, ProductId);

    public string Speed => SpeedMbps switch { >= 10000 => "USB 3.2", >= 5000 => "USB 3", >= 480 => "USB 2", _ => "USB 1" };

    /// <summary>The USB classes of the device and its interfaces (3 input, 8 storage, 0x0e video…), from sysfs.</summary>
    public IReadOnlyList<int> Classes { get; init; } = [];

    /// <summary>What to know before handing this kind of device to a machine, or <c>null</c> for nothing special.</summary>
    public string? Advice => UsbHost.Advice(Classes);
}

/// <summary>
/// The USB devices plugged into this computer (from sysfs, named from usb.ids when the device does not name itself),
/// and handing one to a running machine through QMP or taking it back. A device node the person's account may not open
/// is opened to the person through spice-gtk's polkit helper (<see cref="UsbAccess"/>), as virt-viewer does.
/// </summary>
public static class UsbHost
{
    private const string SysUsb = "/sys/bus/usb/devices";
    private static readonly Lazy<Dictionary<string, string>> Names = new(LoadUsbIds);

    /// <summary>
    /// Advice by device class, the most telling first: what the person loses on this computer, and what the guest needs.
    /// </summary>
    public static string? Advice(IReadOnlyList<int> classes) =>
        classes.Contains(0x08) ? "A storage device: it leaves this computer, so unmount it first. Host drives can hand over the disk alone, read-only if you like."
        : classes.Contains(0x0e) ? "A webcam: video over a passed-through device needs a USB 3 port to run smoothly; the guest uses its own camera driver."
        : classes.Contains(0x01) ? "An audio device: its sound plays in the guest only, and may crackle when this computer is busy."
        : classes.Contains(0x06) ? "A phone or camera: the guest opens it (MTP). Unlock the phone and allow file access when it asks."
        : classes.Contains(0xe0) ? "A Bluetooth adapter: this computer has no Bluetooth while the guest has it."
        : classes.Contains(0x0b) ? "A smart card reader: the guest needs its smart card driver; this computer cannot use the card meanwhile."
        : classes.Contains(0x07) ? "A printer: the guest needs the printer's own driver."
        : classes.Contains(0x02) || classes.Contains(0x0a) ? "A serial or network adapter: the guest needs its driver. Settings, Serial port, can hand over just the port instead."
        : classes.Contains(0x03) ? "It counts as an input device (a keyboard, mouse, controller, or a lighting or control panel): this computer cannot use it while the machine has it."
        : null;

    public static IReadOnlyList<HostUsbDevice> List(string sysRoot = SysUsb, Func<string, bool>? writable = null)
    {
        writable ??= CanWrite;
        var devices = new List<HostUsbDevice>();
        if (!Directory.Exists(sysRoot)) return devices;
        foreach (var dir in Directory.EnumerateDirectories(sysRoot))
        {
            var port = Path.GetFileName(dir);
            if (port.Contains(':') || port.StartsWith("usb", StringComparison.Ordinal)) continue; // interfaces and root hubs
            string Read(string f) => File.Exists(Path.Combine(dir, f)) ? File.ReadAllText(Path.Combine(dir, f)).Trim() : "";
            var vendor = Read("idVendor");
            var product = Read("idProduct");
            if (vendor.Length == 0) continue;
            var bus = int.TryParse(Read("busnum"), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b.ToString("000", CultureInfo.InvariantCulture) : "";
            var dev = int.TryParse(Read("devnum"), NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d.ToString("000", CultureInfo.InvariantCulture) : "";
            var name = DeviceName(Read("manufacturer"), Read("product"));
            if (name.Length == 0 || name.All(c => c is '-' or '_'))
                name = Names.Value.GetValueOrDefault($"{vendor}:{product}") ?? Names.Value.GetValueOrDefault(vendor) ?? $"USB device {vendor}:{product}";
            var speed = int.TryParse(Read("speed").Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sp) ? sp : 12;
            var classes = new List<int>();
            if (int.TryParse(Read("bDeviceClass"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var deviceClass) && deviceClass != 0) classes.Add(deviceClass);
            foreach (var iface in Directory.EnumerateDirectories(sysRoot, port + ":*"))
            {
                var f = Path.Combine(iface, "bInterfaceClass");
                if (File.Exists(f) && int.TryParse(File.ReadAllText(f).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) && !classes.Contains(c)) classes.Add(c);
            }

            devices.Add(new HostUsbDevice(vendor, product, name, bus, dev, port, speed, writable($"/dev/bus/usb/{bus}/{dev}"), deviceClass == 9) { Classes = classes });
        }

        return devices.Where(d => !d.IsHub).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The QEMU device id of a rule: a usb-host device matched by vendor and product rather than by port, which QEMU
    /// connects whenever such a device is plugged in (and again after it is unplugged and plugged back).
    /// </summary>
    public static string RuleId(string vendorId, string productId) => $"usbauto-{vendorId}-{productId}";

    /// <summary>Whether a QEMU device id is a rule's (as opposed to one device handed over by its port).</summary>
    public static bool IsRuleId(string id) => id.StartsWith("usbauto-", StringComparison.Ordinal);

    /// <summary>Adds a rule to the running machine: every device of this kind connects to it, now and when plugged in later.</summary>
    public static async Task AddRuleAsync(MachineWatcher watcher, string vendorId, string productId, CancellationToken ct = default)
    {
        // A device of this kind plugged in now is opened to this account first, so QEMU can take it at once.
        var present = List().Where(d => d.VendorId == vendorId && d.ProductId == productId).ToList();
        await UsbAccess.GrantAllAsync(present, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var bus = present.Count > 0 ? await PassBusAsync(watcher, present[0], ct).ConfigureAwait(false) : null;
        var rule = new JsonObject
        {
            ["driver"] = "usb-host",
            ["id"] = RuleId(vendorId, productId),
            ["vendorid"] = int.Parse(vendorId, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ["productid"] = int.Parse(productId, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        };
        if (bus is not null) rule["bus"] = bus;
        await watcher.ExecuteAsync("device_add", rule, ct).ConfigureAwait(false);
    }

    /// <summary>Takes the rule out of the running machine; a device it holds goes back to this computer.</summary>
    public static async Task RemoveRuleAsync(MachineWatcher watcher, string vendorId, string productId, CancellationToken ct = default) =>
        await watcher.ExecuteAsync("device_del", new JsonObject { ["id"] = RuleId(vendorId, productId) }, ct).ConfigureAwait(false);

    /// <summary>
    /// "Corsair" + "CORSAIR K100 RGB" reads "CORSAIR K100 RGB", not "Corsair CORSAIR K100 RGB": a product that already
    /// starts with its maker's first word keeps its own name. Public for tests.
    /// </summary>
    public static string DeviceName(string manufacturer, string product)
    {
        manufacturer = manufacturer.Trim();
        product = product.Trim();
        var maker = manufacturer.Split(' ', 2)[0];
        if (maker.Length > 0 && product.StartsWith(maker, StringComparison.OrdinalIgnoreCase)) return product;
        return (manufacturer + " " + product).Trim();
    }

    /// <summary>Hands the device to the running machine; it leaves this computer until it is taken back or the machine stops.</summary>
    public static async Task AttachAsync(MachineWatcher watcher, HostUsbDevice d, CancellationToken ct = default, string sysRoot = SysUsb)
    {
        // Attached by port, QEMU would wait quietly for a device that is gone: say so instead.
        if (!File.Exists(Path.Combine(sysRoot, d.Port, "devnum")))
            throw new IOException($"{d.Name} is no longer plugged in.");
        var bus = await PassBusAsync(watcher, d, ct).ConfigureAwait(false);
        if (await UsbAccess.GrantAsync(d, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false) is { } refused)
            throw new IOException($"{d.Name} could not be opened for the machine: {refused}.");
        // By port, not by device number: a device that re-enumerates (many receivers do when they are taken over) is
        // found again where it was, and the hot-plug watcher opens its new node to QEMU.
        var args = new JsonObject
        {
            ["driver"] = "usb-host",
            ["id"] = d.QemuId,
            ["hostbus"] = int.Parse(d.Bus, CultureInfo.InvariantCulture),
            ["hostport"] = d.HostPort,
        };
        if (bus is not null) args["bus"] = bus;
        await watcher.ExecuteAsync("device_add", args, ct).ConfigureAwait(false);
    }

    /// <summary>The id of the controller VM Qontrol adds for handed-over devices (see the command builder).</summary>
    public const string HostPassBus = "hostpass.0";

    /// <summary>
    /// The bus a device goes on: VM Qontrol's xHCI controller when the machine has it. Without it (a machine imported
    /// from quickemu) a device slower than USB 2 cannot attach to a USB 2 bus, and the reason is said before QEMU tries.
    /// </summary>
    private static async Task<string?> PassBusAsync(MachineWatcher watcher, HostUsbDevice d, CancellationToken ct)
    {
        try
        {
            if (await watcher.ExecuteAsync("qom-list", new JsonObject { ["path"] = "/machine/peripheral" }, ct).ConfigureAwait(false) is JsonArray items
                && items.OfType<JsonObject>().Any(i => i["name"]?.GetValue<string>() == "hostpass"))
                return HostPassBus;
        }
        catch (QmpException)
        {
        }

        return null;
    }

    public static async Task DetachAsync(MachineWatcher watcher, HostUsbDevice d, CancellationToken ct = default) =>
        await watcher.ExecuteAsync("device_del", new JsonObject { ["id"] = d.QemuId }, ct).ConfigureAwait(false);

    /// <summary>The ids of the host USB devices and rules attached to the machine now (from its device tree).</summary>
    public static async Task<IReadOnlySet<string>> AttachedAsync(MachineWatcher watcher, CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (await watcher.ExecuteAsync("qom-list", new JsonObject { ["path"] = "/machine/peripheral" }, ct).ConfigureAwait(false) is JsonArray items)
                foreach (var i in items.OfType<JsonObject>())
                    if (i["name"]?.GetValue<string>() is { } n && (n.StartsWith("usb-", StringComparison.Ordinal) || IsRuleId(n))) ids.Add(n);
        }
        catch (QmpException)
        {
        }

        return ids;
    }

    private static bool CanWrite(string node)
    {
        try
        {
            using var _ = new FileStream(node, FileMode.Open, FileAccess.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>usb.ids (hwdata): vendor names ("046d") and product names ("046d:c52b").</summary>
    private static Dictionary<string, string> LoadUsbIds()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = new[] { "/usr/share/hwdata/usb.ids", "/usr/share/misc/usb.ids", "/usr/share/usb.ids" }.FirstOrDefault(File.Exists);
        if (file is null) return names;
        string? vendor = null, vendorName = null;
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length < 6 || line[0] == '#') continue;
            if (line.StartsWith("C ", StringComparison.Ordinal)) break; // device classes follow the vendor list
            if (char.IsAsciiHexDigit(line[0]) && line[4] == ' ')
            {
                vendor = line[..4];
                vendorName = line[6..].Trim();
                names[vendor] = vendorName;
            }
            else if (line[0] == '\t' && line.Length > 7 && line[1] != '\t' && vendor is not null)
            {
                names[$"{vendor}:{line[1..5]}"] = $"{vendorName} {line[7..].Trim()}";
            }
        }

        return names;
    }
}
