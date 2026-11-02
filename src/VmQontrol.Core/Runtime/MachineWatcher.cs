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
