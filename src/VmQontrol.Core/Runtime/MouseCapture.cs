using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// Whether a viewer can be holding the machine's mouse. SPICE runs in "server" mouse mode while the guest drives a
/// relative mouse (firmware, boot pickers, installers before their USB driver): a click in the viewer then captures the
/// pointer until the release keys. Once the guest drives the tablet, the mode is "client" and the pointer moves freely.
/// </summary>
public static class MouseCapture
{
    /// <summary>The viewers' release keys (spice-gtk's grab sequence; remote-viewer and spicy both use it).</summary>
    public const string ReleaseKeys = "Ctrl+Alt";

    /// <summary>From query-spice: a viewer is connected and the mouse mode is "server".</summary>
    public static bool CanCapture(JsonNode? spice) =>
        spice?["enabled"]?.GetValue<bool>() == true
        && spice["mouse-mode"]?.GetValue<string>() == "server"
        && spice["channels"] is JsonArray { Count: > 0 };

    /// <summary>From query-spice: a viewer is connected to the machine's screen.</summary>
    public static bool ViewerConnected(JsonNode? spice) => spice?["enabled"]?.GetValue<bool>() == true && spice["channels"] is JsonArray { Count: > 0 };

    /// <summary>One query: whether a viewer is connected, and whether it can hold the mouse.</summary>
    public static async Task<(bool Viewer, bool Capture)> ReadAsync(MachineWatcher watcher, CancellationToken ct = default)
    {
        var spice = await watcher.ExecuteAsync("query-spice", ct: ct).ConfigureAwait(false);
        return (ViewerConnected(spice), CanCapture(spice));
    }

    public static async Task<bool> CanCaptureAsync(MachineWatcher watcher, CancellationToken ct = default) =>
        CanCapture(await watcher.ExecuteAsync("query-spice", ct: ct).ConfigureAwait(false));
}
