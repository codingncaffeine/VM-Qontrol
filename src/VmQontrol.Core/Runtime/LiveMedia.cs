using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>A removable drive of a running machine, as QEMU reports it.</summary>
public sealed record LiveDrive(string Device, string? QdevPath, string? File);

/// <summary>
/// Changes and ejects discs in a running machine, as a computer's CD drive does. Drives are found through QEMU's own
/// list (query-block), matched by the disc they hold, so no device names need to be planned ahead.
/// </summary>
public static class LiveMedia
{
    public static async Task<IReadOnlyList<LiveDrive>> DrivesAsync(MachineWatcher watcher, CancellationToken ct = default)
    {
        var drives = new List<LiveDrive>();
        if (await watcher.ExecuteAsync("query-block", ct: ct).ConfigureAwait(false) is not JsonArray blocks) return drives;
        foreach (var b in blocks.OfType<JsonObject>())
        {
            if (b["removable"]?.GetValue<bool>() != true) continue;
            drives.Add(new LiveDrive(b["device"]?.GetValue<string>() ?? "", b["qdev"]?.GetValue<string>(), b["inserted"]?["file"]?.GetValue<string>()));
        }

        return drives;
    }

    /// <summary>Puts <paramref name="newFile"/> in the drive that holds <paramref name="currentFile"/> (or the first empty one).</summary>
    public static async Task ChangeAsync(MachineWatcher watcher, string? currentFile, string newFile, CancellationToken ct = default)
    {
        var drives = await DrivesAsync(watcher, ct).ConfigureAwait(false);
        var drive = drives.FirstOrDefault(d => currentFile is not null && d.File == currentFile) ?? drives.FirstOrDefault(d => d.File is null)
                    ?? throw new QmpException("blockdev-change-medium", "NoDrive", "The machine has no free disc drive. Add one in Settings, then power the machine off and on.");
        var args = new JsonObject { ["filename"] = newFile, ["format"] = "raw" };
        if (drive.Device.Length > 0) args["device"] = drive.Device;
        else args["id"] = drive.QdevPath;
        await watcher.ExecuteAsync("blockdev-change-medium", args, ct).ConfigureAwait(false);
    }

    public static async Task EjectAsync(MachineWatcher watcher, string currentFile, CancellationToken ct = default)
    {
        var drive = (await DrivesAsync(watcher, ct).ConfigureAwait(false)).FirstOrDefault(d => d.File == currentFile);
        if (drive is null) return;
        var args = new JsonObject { ["force"] = true };
        if (drive.Device.Length > 0) args["device"] = drive.Device;
        else args["id"] = drive.QdevPath;
        await watcher.ExecuteAsync("eject", args, ct).ConfigureAwait(false);
    }
}
