using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// Memory of a running machine through its balloon device (virtio-balloon): the guest gives memory back to this
/// computer down to a target, and takes it again up to the size it started with. The guest's balloon driver does the
/// work (Linux has it built in; Windows has it with the VirtIO drivers), so the amount it really uses is read back.
/// </summary>
public static class LiveMemory
{
    /// <summary>MiB the guest has now, or <c>null</c> when the machine has no balloon device.</summary>
    public static async Task<long?> ActualMiBAsync(MachineWatcher watcher, CancellationToken ct = default)
    {
        try
        {
            var reply = await watcher.ExecuteAsync("query-balloon", ct: ct).ConfigureAwait(false);
            return reply?["actual"]?.GetValue<long>() is { } bytes ? bytes >> 20 : null;
        }
        catch (QmpException)
        {
            return null;
        }
    }

    /// <summary>Asks the guest to hold <paramref name="mib"/> MiB (at most what it started with).</summary>
    public static Task SetAsync(MachineWatcher watcher, long mib, CancellationToken ct = default) =>
        watcher.ExecuteAsync("balloon", new JsonObject { ["value"] = mib << 20 }, ct);
}
