using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Snapshots;

/// <summary>
/// AutoProtect, as Workstation's: while a machine runs, a snapshot (memory included) every so often, and only the newest
/// few of those kept. The snapshots the person takes are theirs and are never deleted by it.
/// </summary>
public static class AutoProtect
{
    /// <summary>Due when the interval has passed since the last automatic snapshot, or since the machine started.</summary>
    public static bool IsDue(AutoProtectConfig c, SnapshotList list, DateTimeOffset runningSince, DateTimeOffset now)
    {
        if (!c.Enabled || c.IntervalMinutes <= 0) return false;
        var lastAuto = list.Snapshots.Where(s => s.Auto).Select(s => s.Taken).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var from = lastAuto > runningSince ? lastAuto : runningSince;
        return now - from >= TimeSpan.FromMinutes(c.IntervalMinutes);
    }

    /// <summary>The automatic snapshots beyond the newest <see cref="AutoProtectConfig.Keep"/>, oldest first.</summary>
    public static IReadOnlyList<Snapshot> ToPrune(AutoProtectConfig c, SnapshotList list) =>
        list.Snapshots.Where(s => s.Auto).OrderByDescending(s => s.Taken).Skip(Math.Max(1, c.Keep)).OrderBy(s => s.Taken).ToList();

    /// <summary>Takes an automatic snapshot of the running machine when one is due and prunes the old ones; the new one, or <c>null</c>.</summary>
    public static async Task<Snapshot?> RunIfDueAsync(SnapshotStore store, AutoProtectConfig c, MachineWatcher watcher, DateTimeOffset runningSince, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (!IsDue(c, store.Load(), runningSince, now)) return null;
        var taken = await store.TakeAsync($"AutoProtect {now.ToLocalTime():yyyy-MM-dd HH:mm}", "Taken automatically; the oldest of these are deleted as new ones are taken.",
            watcher, ct, auto: true).ConfigureAwait(false);
        foreach (var old in ToPrune(c, store.Load())) await store.DeleteAsync(old, watcher, ct).ConfigureAwait(false);
        Log.Info($"AutoProtect: took {taken.Name}");
        return taken;
    }
}
