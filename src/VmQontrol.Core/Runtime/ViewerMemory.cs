using System.Buffers.Binary;
using System.Text.Json;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Runtime;

/// <summary>The screen as the person last left it: the guest's resolution, and whether the viewer was full screen.</summary>
public sealed record ViewerState(int Width, int Height, bool FullScreen);

/// <summary>
/// Remembers how each machine's screen was left (D35 item 3, option a), as Workstation reopens a machine at its last
/// size: when the viewer window closes on a running machine, the guest's resolution is read (one screendump) and kept
/// in the machine's folder. The next start gives the guest that resolution, so the viewer, which sizes its window to
/// the guest, opens at it; a resolution exactly the size of one of this computer's monitors means the viewer was full
/// screen (a window's title bar always leaves the guest smaller), and it reopens full screen. The machine's own Display
/// settings (a fixed resolution, Full screen) always win.
/// </summary>
public static class ViewerMemory
{
    public const string FileName = "viewer.json";

    /// <summary>The monitors' sizes in pixels, kept current by the app (the viewer runs on the same desktop).</summary>
    public static IReadOnlyList<(int Width, int Height)> Monitors { get; set; } = [];

    public static ViewerState? Read(string machineDirectory)
    {
        try
        {
            var file = Path.Combine(machineDirectory, FileName);
            return File.Exists(file) ? JsonSerializer.Deserialize<ViewerState>(File.ReadAllText(file)) is { Width: >= 320, Height: >= 200 } s ? s : null : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The resolution the guest starts with: the machine's own setting, else the remembered one, else 1280×800.</summary>
    public static (int Width, int Height) StartResolution(MachineConfig m, string? machineDirectory) =>
        m.Display is { Width: { } w, Height: { } h } ? (w, h)
        : machineDirectory is not null && m.Display.Monitors is null or 1 && Read(machineDirectory) is { } s ? (s.Width, s.Height)
        : (1280, 800);

    public static bool OpensFullScreen(MachineConfig m, string machineDirectory) =>
        m.Display.FullScreen || m.Display.Monitors is null or 1 && Read(machineDirectory)?.FullScreen == true;

    /// <summary>Waits for the viewer to close and keeps how the screen was left, if the machine still runs then.</summary>
    public static async Task RememberWhenClosedAsync(System.Diagnostics.Process viewer, RunningMachine running, MachineConfig m)
    {
        using var _ = viewer;
        try
        {
            await viewer.WaitForExitAsync().ConfigureAwait(false);
            if (m.Display.Monitors is > 1 || !MachineRunner.IsRunning(running)) return; // closed by the machine shutting down
            var shot = Path.Combine(running.RuntimeDirectory, "viewer-size.png");
            await MachineRunner.ScreenshotAsync(running, shot).ConfigureAwait(false);
            var size = Size(shot);
            File.Delete(shot);
            if (size is not var (w, h)) return;
            var state = new ViewerState(w, h, Monitors.Contains((w, h)));
            File.WriteAllText(Path.Combine(running.MachineDirectory, FileName), JsonSerializer.Serialize(state));
            Log.Info($"{running.Name}: the screen was left at {w}×{h}{(state.FullScreen ? ", full screen" : "")}; it reopens so.");
        }
        catch (Exception ex) when (ex is IOException or QmpException or TimeoutException or ObjectDisposedException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug($"{running.Name}: the screen's size was not kept ({ex.Message})");
        }
    }

    /// <summary>A PNG's width and height from its header (IHDR), or <c>null</c> when the file is not one.</summary>
    internal static (int Width, int Height)? Size(string png)
    {
        Span<byte> head = stackalloc byte[24];
        using var f = File.OpenRead(png);
        if (f.Read(head) < 24 || head[12] != (byte)'I' || head[13] != (byte)'H' || head[14] != (byte)'D' || head[15] != (byte)'R') return null;
        return (BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]));
    }
}
