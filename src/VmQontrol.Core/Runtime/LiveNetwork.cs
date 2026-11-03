using System.Text.Json.Nodes;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// The network adapter of a running machine: its cable plugged in or pulled out (Workstation's "Connected", virt-manager's
/// link state), and its traffic written to a .pcap file for Wireshark (VirtualBox's network trace). QEMU names the
/// adapter's back end "nic" on VM Qontrol's command lines; a quickemu-style "-nic" line names it "__org.qemu.nic0".
/// </summary>
public static class LiveNetwork
{
    private static readonly string[] BackEnds = ["nic", "__org.qemu.nic0"];

    /// <summary>The marker a machine keeps in its runtime folder while its cable is out (QEMU has no query for the link).</summary>
    public static string UnpluggedMarker(RuntimePaths runtime) => Path.Combine(runtime.Directory, "cable-unplugged");

    /// <summary>The capture file being written, kept in the runtime folder while a capture runs.</summary>
    public static string CaptureMarker(RuntimePaths runtime) => Path.Combine(runtime.Directory, "capture.path");

    public static bool IsUnplugged(RuntimePaths runtime) => File.Exists(UnpluggedMarker(runtime));

    /// <summary>The file a running capture writes, or <c>null</c>.</summary>
    public static string? CapturePath(RuntimePaths runtime)
    {
        try
        {
            return File.Exists(CaptureMarker(runtime)) ? File.ReadAllText(CaptureMarker(runtime)).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Plugs the cable in or pulls it out; the guest sees the link go up or down as on a real adapter.</summary>
    public static async Task SetCableAsync(Func<string, JsonObject?, Task<JsonNode?>> execute, RuntimePaths runtime, bool connected, CancellationToken ct = default)
    {
        await OnBackEndAsync(name => execute("set_link", new JsonObject { ["name"] = name, ["up"] = connected })).ConfigureAwait(false);
        if (connected) File.Delete(UnpluggedMarker(runtime));
        else await File.WriteAllTextAsync(UnpluggedMarker(runtime), "", ct).ConfigureAwait(false);
    }

    /// <summary>Starts writing every packet the machine sends and receives to <paramref name="pcap"/> (libpcap format).</summary>
    public static async Task StartCaptureAsync(Func<string, JsonObject?, Task<JsonNode?>> execute, RuntimePaths runtime, string pcap, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pcap))!);
        await OnBackEndAsync(name => execute("object-add", new JsonObject
        {
            ["qom-type"] = "filter-dump",
            ["id"] = "vmq-capture",
            ["netdev"] = name,
            ["file"] = Path.GetFullPath(pcap),
        })).ConfigureAwait(false);
        await File.WriteAllTextAsync(CaptureMarker(runtime), Path.GetFullPath(pcap), ct).ConfigureAwait(false);
    }

    /// <summary>Stops the capture; QEMU closes the file, which Wireshark then opens whole.</summary>
    public static async Task StopCaptureAsync(Func<string, JsonObject?, Task<JsonNode?>> execute, RuntimePaths runtime)
    {
        try
        {
            await execute("object-del", new JsonObject { ["id"] = "vmq-capture" }).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(CaptureMarker(runtime));
        }
    }

    /// <summary>Tries each name QEMU may have given the adapter's back end; a machine without a network adapter says so.</summary>
    private static async Task OnBackEndAsync(Func<string, Task<JsonNode?>> call)
    {
        foreach (var name in BackEnds)
        {
            try
            {
                await call(name).ConfigureAwait(false);
                return;
            }
            catch (QmpException ex) when (ex.Class == "DeviceNotFound" || ex.Description.Contains("not found", StringComparison.OrdinalIgnoreCase) || ex.Description.Contains("can't find", StringComparison.OrdinalIgnoreCase))
            {
                // the next name
            }
        }

        throw new QmpException("set_link", "DeviceNotFound", "This machine has no network adapter.");
    }
}
