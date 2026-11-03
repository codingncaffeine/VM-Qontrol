namespace VmQontrol.Core.Runtime;

public enum PowerState
{
    Off,
    Running,
    Paused,

    /// <summary>The guest was asked to shut down and has not finished yet.</summary>
    ShuttingDown,

    /// <summary>The guest reported a crash (a Windows stop error, a Linux panic).</summary>
    Crashed,
}

/// <summary>
/// Holds a machine's supervisor QMP connection for as long as it runs and turns QEMU's events into a
/// <see cref="PowerState"/>. <see cref="StateChanged"/> is raised on a worker thread: a view posts it to the UI thread.
/// </summary>
public sealed class MachineWatcher : IAsyncDisposable
{
    private readonly QmpClient _qmp;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private MachineWatcher(RunningMachine machine, QmpClient qmp, PowerState initial)
    {
        Machine = machine;
        _qmp = qmp;
        State = initial;
    }

    public RunningMachine Machine { get; }

    public PowerState State { get; private set; }

    /// <summary>The last event's detail for a crash or a shutdown ("guest-shutdown", "host-qmp-quit"...).</summary>
    public string? Reason { get; private set; }

    public event Action<MachineWatcher, PowerState>? StateChanged;

    /// <summary>Completes when QEMU has gone (the socket closed).</summary>
    public Task Exited => _exited.Task;

    public static async Task<MachineWatcher> AttachAsync(RunningMachine machine, CancellationToken ct = default)
    {
        var qmp = await QmpClient.ConnectAsync(machine.Paths.Qmp, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var status = (await qmp.ExecuteAsync("query-status", ct: ct).ConfigureAwait(false))?["status"]?.GetValue<string>();
        var watcher = new MachineWatcher(machine, qmp, FromStatus(status));
        _ = Task.Run(watcher.PumpAsync, CancellationToken.None);
        return watcher;
    }

    /// <summary>
    /// A command on the watcher's own connection. The app's periodic work (screens, performance) goes through here, so
    /// the control socket stays free for vmq.
    /// </summary>
    public Task<System.Text.Json.Nodes.JsonNode?> ExecuteAsync(string command, System.Text.Json.Nodes.JsonObject? arguments = null, CancellationToken ct = default) =>
        _qmp.ExecuteAsync(command, arguments, ct);

    /// <summary>Saves the screen as a PNG through the watcher's connection.</summary>
    public Task ScreenshotAsync(string pngFile, CancellationToken ct = default) =>
        _qmp.ExecuteAsync("screendump", new System.Text.Json.Nodes.JsonObject { ["filename"] = Path.GetFullPath(pngFile), ["format"] = "png" }, ct);

    public static PowerState FromStatus(string? status) => status switch
    {
        "running" => PowerState.Running,
        "paused" or "prelaunch" or "suspended" or "inmigrate" or "postmigrate" or "save-vm" or "restore-vm" or "debug" => PowerState.Paused,
        "guest-panicked" or "internal-error" or "io-error" => PowerState.Crashed,
        "shutdown" => PowerState.ShuttingDown,
        _ => PowerState.Running,
    };

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var e in _qmp.Events.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var next = e.Name switch
                {
                    "STOP" => PowerState.Paused,
                    "RESUME" or "RESET" => PowerState.Running,
                    "POWERDOWN" => PowerState.ShuttingDown,
                    "SHUTDOWN" => PowerState.ShuttingDown,
                    "GUEST_PANICKED" or "GUEST_CRASHLOADED" => PowerState.Crashed,
                    _ => State,
                };
                if (e.Name is "SHUTDOWN" or "GUEST_PANICKED") Reason = e.Data?["reason"]?.GetValue<string>() ?? e.Data?["action"]?.GetValue<string>();
                // The power button pressed: a guest that ignores it keeps running, so the state goes back to what QEMU says
                // after a while instead of "Shutting down" for ever.
                if (e.Name == "POWERDOWN") _ = RevertIfStillRunningAsync();
                if (e.Name == "BLOCK_IO_ERROR") Log.Warn($"{Machine.Name}: a disk reported an error ({e.Data?["reason"]?.GetValue<string>() ?? "no detail"}). The host's disk may be full.");
                Set(next);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await _qmp.Closed.ConfigureAwait(false);
        Set(PowerState.Off);
        _exited.TrySetResult();
    }

    /// <summary>How long a guest may take to act on the power button before the state shows it still running.</summary>
    internal static TimeSpan PowerButtonGrace { get; set; } = TimeSpan.FromSeconds(90);

    private async Task RevertIfStillRunningAsync()
    {
        try
        {
            await Task.Delay(PowerButtonGrace, _stop.Token).ConfigureAwait(false);
            if (State != PowerState.ShuttingDown) return;
            var status = (await _qmp.ExecuteAsync("query-status", ct: _stop.Token).ConfigureAwait(false))?["status"]?.GetValue<string>();
            if (status == "running")
            {
                Log.Info($"{Machine.Name}: the guest did not act on the power button; it is still running.");
                Set(PowerState.Running);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or QmpException or InvalidOperationException)
        {
        }
    }

    private void Set(PowerState next)
    {
        if (next == State) return;
        State = next;
        StateChanged?.Invoke(this, next);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _qmp.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
