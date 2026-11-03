using System.Text.Json;
using System.Text.Json.Nodes;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Snapshots;

/// <summary>A snapshot: its name, what the person wrote about it, when, whether it holds the running state, and its parent.</summary>
public sealed record Snapshot
{
    public required string Tag { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = "";

    public DateTimeOffset Taken { get; init; }

    /// <summary>Taken while the machine ran: restoring returns to that running moment, programs and all.</summary>
    public bool WithMemory { get; init; }

    /// <summary>The snapshot this one was taken after (the tree's parent); <c>null</c> for the first.</summary>
    public string? Parent { get; init; }

    /// <summary>A screenshot of the moment, beside the list (file name).</summary>
    public string? Screenshot { get; init; }

    /// <summary>Taken by AutoProtect, which deletes its own oldest ones; the person's own snapshots are never deleted for them.</summary>
    public bool Auto { get; init; }
}

/// <summary>The snapshot list and which one the machine stands on now ("You are here").</summary>
public sealed record SnapshotList
{
    public List<Snapshot> Snapshots { get; init; } = [];

    public string? Current { get; init; }
}

/// <summary>
/// Snapshots stored inside the machine's qcow2 disks (QEMU's internal snapshots, as virt-manager uses). With the machine
/// off, qemu-img takes, restores and deletes them; while it runs, QMP's snapshot jobs do, memory included. The list,
/// with names, descriptions, screenshots and the tree, is kept in snapshots.json in a folder VM Qontrol owns.
/// </summary>
public sealed class SnapshotStore(MachineConfig machine, string machineDirectory, string metadataDirectory)
{
    public const string FileName = "snapshots.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private string MetadataFile => Path.Combine(metadataDirectory, FileName);

    public string ScreenshotDirectory => Path.Combine(metadataDirectory, "snapshots");

    private IEnumerable<string> Disks => machine.Disks.Where(d => d.Format == "qcow2").Select(d => Path.IsPathRooted(d.File) ? d.File : Path.Combine(machineDirectory, d.File));

    public SnapshotList Load()
    {
        try
        {
            return File.Exists(MetadataFile) ? JsonSerializer.Deserialize<SnapshotList>(File.ReadAllText(MetadataFile), Json) ?? new SnapshotList() : new SnapshotList();
        }
        catch (JsonException ex)
        {
            // The disks still hold every snapshot; only the names and notes are unreadable. The file is moved aside, never
            // written over, so they can be put back by hand.
            var kept = $"{MetadataFile}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(MetadataFile, kept);
            }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"The snapshot list ({MetadataFile}) is damaged and could not be set aside: {move.Message}", ex);
            }

            Log.Warn($"{machine.Name}: the snapshot list was damaged ({ex.Message}); kept as {kept}.");
            return new SnapshotList();
        }
    }

    private void Save(SnapshotList list)
    {
        Directory.CreateDirectory(metadataDirectory);
        var temp = $"{MetadataFile}.{Environment.ProcessId}.new";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, list, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, MetadataFile, overwrite: true);
    }

    /// <summary>Takes a snapshot; <paramref name="watcher"/> is the running machine's connection, or <c>null</c> when it is off.</summary>
    public async Task<Snapshot> TakeAsync(string name, string description, MachineWatcher? watcher, CancellationToken ct = default, bool auto = false)
    {
        if (machine.OnPowerOff != DiscardMode.Keep)
            throw new InvalidOperationException("This machine throws its changes away at power off, and a snapshot would go with them. Set Settings → Hard disks → Changes at power off to Keep to take snapshots.");
        if (machine.Disks.Any(d => d.Format != "qcow2"))
            throw new InvalidOperationException("Snapshots need every disk in qcow2 format; this machine has a raw disk.");
        var list = Load();
        var tag = $"vmq-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        string? screenshot = null;
        if (watcher is not null)
        {
            Directory.CreateDirectory(ScreenshotDirectory);
            screenshot = tag + ".png";
            try
            {
                await watcher.ScreenshotAsync(Path.Combine(ScreenshotDirectory, screenshot), ct).ConfigureAwait(false);
            }
            catch (QmpException)
            {
                screenshot = null;
            }

            var nodes = await WritableNodesAsync(watcher, ct).ConfigureAwait(false);
            await RunJobAsync(watcher, "snapshot-save", new JsonObject
            {
                ["tag"] = tag,
                ["vmstate"] = nodes[0],
                ["devices"] = new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()),
            }, ct).ConfigureAwait(false);
        }
        else
        {
            // All disks or none: a disk that refuses (full, damaged) takes the snapshot back off the disks that had it, so no
            // disk keeps a snapshot the list does not name.
            var taken = new List<string>();
            try
            {
                foreach (var disk in Disks)
                {
                    await QemuImg.RunAsync(ct, "snapshot", "-c", tag, disk).ConfigureAwait(false);
                    taken.Add(disk);
                }
            }
            catch
            {
                foreach (var disk in taken)
                {
                    try { await QemuImg.RunAsync(CancellationToken.None, "snapshot", "-d", tag, disk).ConfigureAwait(false); }
                    catch (IOException undo) { Log.Warn($"{machine.Name}: snapshot {tag} could not be taken back off {disk}: {undo.Message}"); }
                }

                throw;
            }
        }

        var snapshot = new Snapshot { Tag = tag, Name = name, Description = description, Taken = DateTimeOffset.Now, WithMemory = watcher is not null, Parent = list.Current, Screenshot = screenshot, Auto = auto };
        Save(list with { Snapshots = [.. list.Snapshots, snapshot], Current = tag });
        return snapshot;
    }

    /// <summary>Returns the machine to a snapshot. A running machine needs a snapshot taken while running (its memory).</summary>
    public async Task RestoreAsync(Snapshot snapshot, MachineWatcher? watcher, CancellationToken ct = default)
    {
        if (watcher is not null)
        {
            if (!snapshot.WithMemory)
                throw new InvalidOperationException("This snapshot was taken with the machine off. Power the machine off to restore it.");
            var nodes = await WritableNodesAsync(watcher, ct).ConfigureAwait(false);
            await RunJobAsync(watcher, "snapshot-load", new JsonObject
            {
                ["tag"] = snapshot.Tag,
                ["vmstate"] = nodes[0],
                ["devices"] = new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()),
            }, ct).ConfigureAwait(false);
        }
        else
        {
            // The saved memory belongs to the disks as they are; under another state it would wreck the guest's files.
            if (MachineSuspend.IsSuspended(machineDirectory))
                throw new InvalidOperationException("The machine is suspended. Power it on and shut it down, or discard its suspended state, before going back to a snapshot.");
            await RestoreOfflineAsync(snapshot.Tag, ct).ConfigureAwait(false);
        }

        var list = Load();
        Save(list with { Current = snapshot.Tag });
    }

    /// <summary>
    /// All disks or none, as taking one is: each disk first keeps its present state under a hidden tag (on qcow2 that is
    /// metadata, not a copy), so when one disk refuses (snapshot missing, disk full, damaged) the disks already returned go
    /// back to how they were. The hidden tags are deleted either way.
    /// </summary>
    private async Task RestoreOfflineAsync(string tag, CancellationToken ct)
    {
        var disks = Disks.ToList();
        var undo = $"vmq-undo-{Guid.NewGuid().ToString("N")[..12]}";
        var kept = new List<string>();
        var restored = new List<string>();
        try
        {
            foreach (var disk in disks)
            {
                await QemuImg.RunAsync(ct, "snapshot", "-c", undo, disk).ConfigureAwait(false);
                kept.Add(disk);
            }

            foreach (var disk in disks)
            {
                restored.Add(disk);
                await QemuImg.RunAsync(ct, "snapshot", "-a", tag, disk).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (var disk in restored)
            {
                try { await QemuImg.RunAsync(CancellationToken.None, "snapshot", "-a", undo, disk).ConfigureAwait(false); }
                catch (IOException back) { Log.Warn($"{machine.Name}: {disk} could not be returned to its state before the restore ({back.Message}); its hidden snapshot {undo} is kept."); kept.Remove(disk); }
            }

            throw;
        }
        finally
        {
            foreach (var disk in kept)
            {
                try { await QemuImg.RunAsync(CancellationToken.None, "snapshot", "-d", undo, disk).ConfigureAwait(false); }
                catch (IOException gone) { Log.Warn($"{machine.Name}: hidden snapshot {undo} could not be deleted from {disk}: {gone.Message}"); }
            }
        }
    }

    /// <summary>Deletes a snapshot; its children take its parent, so the tree stays whole.</summary>
    public async Task DeleteAsync(Snapshot snapshot, MachineWatcher? watcher, CancellationToken ct = default)
    {
        if (watcher is not null)
        {
            var nodes = await WritableNodesAsync(watcher, ct).ConfigureAwait(false);
            await RunJobAsync(watcher, "snapshot-delete", new JsonObject { ["tag"] = snapshot.Tag, ["devices"] = new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()) }, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var disk in Disks) await QemuImg.RunAsync(ct, "snapshot", "-d", snapshot.Tag, disk).ConfigureAwait(false);
        }

        var list = Load();
        var kept = list.Snapshots.Where(s => s.Tag != snapshot.Tag).Select(s => s.Parent == snapshot.Tag ? s with { Parent = snapshot.Parent } : s).ToList();
        Save(list with { Snapshots = kept, Current = list.Current == snapshot.Tag ? snapshot.Parent : list.Current });
        if (snapshot.Screenshot is { } shot && File.Exists(Path.Combine(ScreenshotDirectory, shot))) File.Delete(Path.Combine(ScreenshotDirectory, shot));
    }

    /// <summary>The block nodes of the machine's writable qcow2 disks (the first also stores the running state).</summary>
    private static async Task<List<string>> WritableNodesAsync(MachineWatcher watcher, CancellationToken ct)
    {
        var nodes = new List<string>();
        if (await watcher.ExecuteAsync("query-block", ct: ct).ConfigureAwait(false) is JsonArray blocks)
        {
            foreach (var b in blocks.OfType<JsonObject>())
            {
                if (b["inserted"] is not JsonObject inserted || inserted["ro"]?.GetValue<bool>() == true) continue;
                if (inserted["drv"]?.GetValue<string>() != "qcow2") continue;
                if (inserted["node-name"]?.GetValue<string>() is { } node) nodes.Add(node);
            }
        }

        return nodes.Count > 0 ? nodes : throw new InvalidOperationException("The running machine has no qcow2 disk to keep a snapshot in.");
    }

    /// <summary>Runs a QMP snapshot job to the end and turns its failure into an exception with QEMU's reason.</summary>
    /// <summary>The longest a snapshot job may run (saving a large memory to a slow disk takes minutes, never this long).</summary>
    internal static TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(30);

    private static async Task RunJobAsync(MachineWatcher watcher, string command, JsonObject arguments, CancellationToken ct)
    {
        var id = $"vmq-{Guid.NewGuid():N}"[..20];
        arguments["job-id"] = id;
        await watcher.ExecuteAsync(command, arguments, ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + JobTimeout;
        while (true)
        {
            if (DateTime.UtcNow > deadline)
                throw new IOException($"QEMU's {command} has not finished after {JobTimeout.TotalMinutes:0} minutes; the machine may be stuck. Its job is {id}.");
            await Task.Delay(100, ct).ConfigureAwait(false);
            var jobs = await watcher.ExecuteAsync("query-jobs", ct: ct).ConfigureAwait(false) as JsonArray;
            var job = jobs?.OfType<JsonObject>().FirstOrDefault(j => j["id"]?.GetValue<string>() == id);
            if (job is null) return;
            if (job["status"]?.GetValue<string>() != "concluded") continue;
            var error = job["error"]?.GetValue<string>();
            await watcher.ExecuteAsync("job-dismiss", new JsonObject { ["id"] = id }, ct).ConfigureAwait(false);
            if (error is not null) throw new QmpException(command, "JobFailed", error);
            return;
        }
    }
}
