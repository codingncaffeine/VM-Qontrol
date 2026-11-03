using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>What a running machine's processors are: how many it has, and how many empty slots it can still take.</summary>
/// <param name="Now">Processors the machine has.</param>
/// <param name="Most">The most it can have (its own plus the empty slots).</param>
/// <param name="PerSlot">Processors one slot adds (a core with its threads).</param>
public sealed record ProcessorSlots(int Now, int Most, int PerSlot)
{
    public bool CanAdd => Now < Most;
}

/// <summary>
/// Processors added to a running machine (CPU hot-plug): QEMU lists the empty slots (query-hotpluggable-cpus) and a CPU
/// device plugged into one (device_add) appears in the guest like a newly fitted processor. Linux brings it online
/// (most distributions at once, through udev); Windows desktop editions do not take processors while running.
/// </summary>
public static class LiveProcessors
{
    /// <summary>The machine's slots; <c>null</c> when it was started without room for more.</summary>
    public static async Task<ProcessorSlots?> SlotsAsync(MachineWatcher watcher, CancellationToken ct = default)
    {
        JsonNode? reply;
        try
        {
            reply = await watcher.ExecuteAsync("query-hotpluggable-cpus", ct: ct).ConfigureAwait(false);
        }
        catch (QmpException)
        {
            return null;
        }

        if (reply is not JsonArray slots || slots.Count == 0) return null;
        var perSlot = slots[0]?["vcpus-count"]?.GetValue<int>() ?? 1;
        var used = slots.Count(x => x?["qom-path"] is not null);
        return new ProcessorSlots(used * perSlot, slots.Count * perSlot, perSlot);
    }

    /// <summary>Plugs processors into empty slots until the machine has <paramref name="target"/> (rounded up to whole slots); returns how many it has.</summary>
    public static async Task<int> AddAsync(MachineWatcher watcher, int target, CancellationToken ct = default)
    {
        var reply = await watcher.ExecuteAsync("query-hotpluggable-cpus", ct: ct).ConfigureAwait(false) as JsonArray ?? [];
        var perSlot = reply.Count > 0 ? reply[0]?["vcpus-count"]?.GetValue<int>() ?? 1 : 1;
        var now = reply.Count(x => x?["qom-path"] is not null) * perSlot;
        // QEMU lists slots highest first; fill them lowest first, as a computer's sockets are.
        foreach (var slot in reply.Reverse().Where(x => x?["qom-path"] is null))
        {
            if (now >= target) break;
            var props = slot!["props"]!.AsObject();
            var args = new JsonObject { ["driver"] = slot["type"]!.GetValue<string>(), ["id"] = "vmqcpu-" + string.Join("-", props.Select(p => p.Value!.ToString())) };
            foreach (var (key, value) in props) args[key] = value!.DeepClone();
            await watcher.ExecuteAsync("device_add", args, ct).ConfigureAwait(false);
            now += perSlot;
        }

        return now;
    }
}
