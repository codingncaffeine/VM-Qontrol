using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// Suspend and resume, as Workstation's: the running machine's memory and devices are written to a file in its folder and
/// QEMU ends; the next power on loads them and the guest carries on where it was. This is QEMU's migration to a file
/// (what libvirt's managed save does), so it leaves the snapshot list alone and works with any disk format.
/// </summary>
public static class MachineSuspend
{
    public const string StateFile = "suspended.vmstate";

    /// <summary>QEMU's command line when it was suspended: the state fits only that hardware, so resume uses it again.</summary>
    public const string CommandFile = "suspended.args.json";

    /// <summary>Tests only: a wait between QEMU quitting and the state taking its name, so a page that looks too early is seen.</summary>
    internal static TimeSpan NamingDelay { get; set; }

    public static string StatePath(string machineDirectory) => Path.Combine(machineDirectory, StateFile);

    /// <summary>
    /// The command line the suspended machine ran with (without -daemonize, -S and -incoming), or <c>null</c> when none
    /// was kept. Settings changed since then apply after this state is used or discarded.
    /// </summary>
    public static IReadOnlyList<string>? SavedCommand(string machineDirectory)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(machineDirectory, CommandFile))) is JsonArray a
                ? a.Select(n => n!.GetValue<string>()).ToList()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>A command line's arguments as QEMU runs them, minus the ones that belong to one start only.</summary>
    public static List<string> HardwareArgs(IEnumerable<string> args)
    {
        var list = new List<string>();
        var skipNext = false;
        foreach (var a in args)
        {
            if (skipNext) { skipNext = false; continue; }
            if (a is "-daemonize" or "-S") continue;
            if (a == "-incoming") { skipNext = true; continue; }
            list.Add(a);
        }

        return list;
    }

    public static bool IsSuspended(string machineDirectory) => File.Exists(StatePath(machineDirectory));

    /// <summary>
    /// Pauses the machine, writes its state and turns it off. On failure (a device QEMU cannot save, such as a shared
    /// folder or a passed-through USB device) the machine carries on running and nothing is left behind.
    /// </summary>
    public static async Task SuspendAsync(RunningMachine running, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var file = StatePath(running.MachineDirectory);
        var partial = file + ".partial";
        await using var qmp = await MachineRunner.ControlAsync(running, ct).ConfigureAwait(false);
        if ((await LiveHostDrives.ListAsync(qmp, ct).ConfigureAwait(false)).Count > 0)
        {
            // The host may change the drive while the machine sleeps, and the guest would wake to a disk that moved under it.
            throw new QmpException("migrate", "HostDrive", "A machine using a drive of this computer cannot be suspended: the drive could change while it sleeps. Disconnect the host drives first, or shut the machine down.");
        }

        var wasRunning = (await qmp.ExecuteAsync("query-status", ct: ct).ConfigureAwait(false))?["status"]?.GetValue<string>() == "running";
        try
        {
            // Stopped first: the state is written once, at disk speed, instead of chasing a running guest's memory.
            await qmp.ExecuteAsync("stop", ct: ct).ConfigureAwait(false);
            progress?.Report("Saving the machine's memory…");
            File.Delete(partial);
            await qmp.ExecuteAsync("migrate", new JsonObject { ["uri"] = "file:" + partial }, ct).ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                var info = await qmp.ExecuteAsync("query-migrate", ct: ct).ConfigureAwait(false);
                var status = info?["status"]?.GetValue<string>();
                if (status == "completed") break;
                if (status is "failed" or "canceled")
                    throw new QmpException("migrate", "MigrationFailed", info?["error-desc"]?.GetValue<string>() ?? "QEMU could not save the machine's state.");
                if (info?["ram"] is JsonObject ram && ram["total"]?.GetValue<long>() is > 0 and var total)
                    progress?.Report($"Saving the machine's memory… {100 * (ram["transferred"]?.GetValue<long>() ?? 0) / total}%");
            }

            // The exact command line, from QEMU itself: the program name first, then each argument.
            var cmdline = (await File.ReadAllTextAsync($"/proc/{running.Pid}/cmdline", ct).ConfigureAwait(false)).Split('\0', StringSplitOptions.None);
            var args = HardwareArgs(cmdline.Skip(1).Take(cmdline.Length - 2));
            await File.WriteAllTextAsync(Path.Combine(running.MachineDirectory, CommandFile), new JsonArray(args.Select(a => (JsonNode)a).ToArray()).ToJsonString(), ct).ConfigureAwait(false);

            // The state takes its final name only once QEMU has gone: a state file beside a guest that is still running
            // would be resumed later onto disks that moved on from it.
            await qmp.ExecuteExpectingExitAsync("quit", ct).ConfigureAwait(false);
            if (!await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
                throw new QmpException("quit", "StillRunning", "The machine's state was saved, but QEMU did not turn off; the state was thrown away and the machine carries on.");
        }
        catch
        {
            File.Delete(partial);
            File.Delete(Path.Combine(running.MachineDirectory, CommandFile));
            if (wasRunning && MachineRunner.IsRunning(running))
            {
                try { await qmp.ExecuteAsync("cont", ct: CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or QmpException or ObjectDisposedException) { Log.Warn($"{running.Name}: could not carry on after a failed suspend ({ex.Message})"); }
            }

            throw;
        }

        if (NamingDelay > TimeSpan.Zero) await Task.Delay(NamingDelay, CancellationToken.None).ConfigureAwait(false);
        File.Move(partial, file, overwrite: true);
        Log.Info($"{running.Name}: suspended to {file}");
    }

    /// <summary>
    /// After QEMU started with <c>-incoming defer</c>: loads the state and waits for it, deletes the file (a state is used once: the
    /// disks move on from it) and sets the guest's clock, which stood still while suspended. With <paramref name="run"/> the
    /// machine carries on once loaded; the state itself says "paused", because suspending pauses first.
    /// </summary>
    public static async Task FinishResumeAsync(RunningMachine running, bool run = true, CancellationToken ct = default)
    {
        await using (var qmp = await MachineRunner.ControlAsync(running, ct).ConfigureAwait(false))
        {
            // QEMU waits (-incoming defer) for this; with exit-on-error off a state that does not fit stays here to report.
            await qmp.ExecuteAsync("migrate-incoming", new JsonObject { ["uri"] = "file:" + StatePath(running.MachineDirectory), ["exit-on-error"] = false }, ct).ConfigureAwait(false);
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            while (true)
            {
                var info = await qmp.ExecuteAsync("query-migrate", ct: ct).ConfigureAwait(false);
                var status = info?["status"]?.GetValue<string>();
                if (status == "completed") break;
                if (status is "failed" or "canceled")
                    throw new StartRefusedException([$"The suspended state could not be loaded ({info?["error-desc"]?.GetValue<string>() ?? "QEMU gave no reason"}). Discard it to start the machine afresh."]);
                if (DateTime.UtcNow > deadline) throw new StartRefusedException(["The suspended state took more than ten minutes to load."]);
                await Task.Delay(200, ct).ConfigureAwait(false);
            }

            if (run) await qmp.ExecuteAsync("cont", ct: ct).ConfigureAwait(false);
        }

        File.Delete(StatePath(running.MachineDirectory));
        File.Delete(Path.Combine(running.MachineDirectory, CommandFile));
        Log.Info($"{running.Name}: resumed");
        _ = Task.Run(async () =>
        {
            // The agent needs a moment after the guest wakes; the clock is a courtesy, so a silent agent is fine.
            var agent = new GuestAgentClient(running.Paths.Agent);
            for (var i = 0; i < 10; i++)
            {
                if (await agent.SetTimeAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false)) return;
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    /// <summary>Throws away the saved state: the next power on starts the machine afresh, as after a power cut.</summary>
    public static void Discard(string machineDirectory)
    {
        File.Delete(StatePath(machineDirectory));
        File.Delete(StatePath(machineDirectory) + ".partial");
        File.Delete(Path.Combine(machineDirectory, CommandFile));
    }
}
