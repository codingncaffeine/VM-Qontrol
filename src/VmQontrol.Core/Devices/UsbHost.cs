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

    /// <summary>The QEMU device id used while it is attached to a machine.</summary>
    public string QemuId => $"usb-{VendorId}-{ProductId}-{Bus}-{Device}";

    public string Speed => SpeedMbps switch { >= 10000 => "USB 3.2", >= 5000 => "USB 3", >= 480 => "USB 2", _ => "USB 1" };
}

/// <summary>
/// The USB devices plugged into this computer (from sysfs, named from usb.ids when the device does not name itself),
/// and handing one to a running machine through QMP or taking it back. A device node the person's account may not open
/// gets the exact one-time udev rule that allows it; VM Qontrol never asks for root itself.
/// </summary>
public static class UsbHost
{
    private const string SysUsb = "/sys/bus/usb/devices";
    private static readonly Lazy<Dictionary<string, string>> Names = new(LoadUsbIds);

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
            var name = (Read("manufacturer") + " " + Read("product")).Trim();
            if (name.Length == 0 || name.All(c => c is '-' or '_'))
                name = Names.Value.GetValueOrDefault($"{vendor}:{product}") ?? Names.Value.GetValueOrDefault(vendor) ?? $"USB device {vendor}:{product}";
            var speed = int.TryParse(Read("speed").Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sp) ? sp : 12;
            devices.Add(new HostUsbDevice(vendor, product, name, bus, dev, port, speed, writable($"/dev/bus/usb/{bus}/{dev}"), Read("bDeviceClass") == "09"));
        }

        return devices.Where(d => !d.IsHub).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The one-time command that lets the person's account hand this device to machines (a udev rule).</summary>
    public static string PermissionFix(HostUsbDevice d) =>
        $"echo 'SUBSYSTEM==\"usb\", ATTR{{idVendor}}==\"{d.VendorId}\", ATTR{{idProduct}}==\"{d.ProductId}\", TAG+=\"uaccess\"' | sudo tee /etc/udev/rules.d/70-vmqontrol-{d.VendorId}-{d.ProductId}.rules && sudo udevadm control --reload && sudo udevadm trigger --attr-match=idVendor={d.VendorId}";

    /// <summary>Hands the device to the running machine; it leaves this computer until it is taken back or the machine stops.</summary>
    public static async Task AttachAsync(MachineWatcher watcher, HostUsbDevice d, CancellationToken ct = default) =>
        await watcher.ExecuteAsync("device_add", new JsonObject
        {
            ["driver"] = "usb-host",
            ["id"] = d.QemuId,
            ["hostbus"] = int.Parse(d.Bus, CultureInfo.InvariantCulture),
            ["hostaddr"] = int.Parse(d.Device, CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);

    public static async Task DetachAsync(MachineWatcher watcher, HostUsbDevice d, CancellationToken ct = default) =>
        await watcher.ExecuteAsync("device_del", new JsonObject { ["id"] = d.QemuId }, ct).ConfigureAwait(false);

    /// <summary>The ids of the host USB devices attached to the machine now (from its device tree).</summary>
    public static async Task<IReadOnlySet<string>> AttachedAsync(MachineWatcher watcher, CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (await watcher.ExecuteAsync("qom-list", new JsonObject { ["path"] = "/machine/peripheral" }, ct).ConfigureAwait(false) is JsonArray items)
                foreach (var i in items.OfType<JsonObject>())
                    if (i["name"]?.GetValue<string>() is { } n && n.StartsWith("usb-", StringComparison.Ordinal)) ids.Add(n);
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
