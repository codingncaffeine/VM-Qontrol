using System.Text;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// Looks after running machines when this computer shuts down or sleeps (rootless: a user's own logind locks, D36):
/// while any machine runs it holds a warning lock naming them (the desktop's shut-down dialog and <c>systemctl</c> show it;
/// this user's own shutdown is never refused) and a delay lock; when the computer goes down anyway it saves every machine
/// it can in the time logind allows (Suspend, as Workstation's) and asks the others to shut down. After sleep it sets the
/// guests' clocks. One per account, started with the first machine as a process of its own; it ends once none run.
/// </summary>
public sealed class ShutdownGuard
{
    public const string Argument = "--shutdown-guard";
    public const string Who = "VM Qontrol";

    /// <summary>Time kept back from logind's delay for QEMU to quit and the state to take its name.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(700);

    private readonly QontrolPaths _paths;
    private readonly Logind _logind;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SafeFileHandle? _warning;
    private string? _warningWhy;
    private SafeFileHandle? _delay;
    private bool _goingDown;

    private ShutdownGuard(QontrolPaths paths, Logind logind)
    {
        _paths = paths;
        _logind = logind;
    }

    /// <summary>Tests: how often the machines are looked at (5 s), and the wait before an empty list ends the guard (20 s).</summary>
    internal static TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(5);

    internal static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(20);

    public static string LockFile(QontrolPaths paths) => Path.Combine(paths.Runtime, "shutdown-guard.lock");

    public static bool IsRunning(QontrolPaths paths)
    {
        var file = LockFile(paths);
        if (!File.Exists(file)) return false;
        try
        {
            using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Starts the guard unless one runs. Only VM Qontrol's own programs start it (a test host runs <see cref="RunAsync"/>
    /// itself, against a stand-in logind, so a test never takes locks on this computer's logind).
    /// </summary>
    public static void EnsureRunning(QontrolPaths paths)
    {
        if (!Detached.CanStartSelf || IsRunning(paths)) return;
        if (!Detached.StartSelf([Argument], out var failure))
            Log.Warn($"The shutdown guard could not start ({failure}); running machines are not saved if this computer shuts down.");
    }

    /// <summary>The entry point for <see cref="Argument"/>.</summary>
    public static int Main(QontrolPaths paths)
    {
        try
        {
            RunAsync(paths, Logind.ForThisComputer(), CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            Log.Flush(TimeSpan.FromSeconds(2));
        }

        return 0;
    }

    /// <summary>Guards the account's machines until none run (or <paramref name="ct"/> ends it).</summary>
    public static async Task RunAsync(QontrolPaths paths, Logind logind, CancellationToken ct)
    {
        var lockPath = LockFile(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        FileStream held;
        try
        {
            held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return; // another process guards them
        }

        await using (held)
        {
            held.SetLength(0);
            await held.WriteAsync(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
            await held.FlushAsync(ct).ConfigureAwait(false);
            var guard = new ShutdownGuard(paths, logind);
            try
            {
                await guard.GuardAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DBusConnectionException or DBusErrorReplyException)
            {
                Log.Warn($"Shutdown guard: logind is not reachable ({ex.Message}); running machines are not saved if this computer shuts down.");
            }
            finally
            {
                guard.Release();
            }
        }

        try
        {
            File.Delete(lockPath);
        }
        catch (IOException)
        {
        }
    }

    private async Task GuardAsync(CancellationToken ct)
    {
        await _logind.ConnectAsync().ConfigureAwait(false);
        using var shutdown = await _logind.WatchAsync("PrepareForShutdown", going => _ = Task.Run(() => OnShutdownAsync(going), CancellationToken.None)).ConfigureAwait(false);
        using var sleep = await _logind.WatchAsync("PrepareForSleep", going =>
        {
            if (!going) _ = Task.Run(SetClocksAsync, CancellationToken.None);
        }).ConfigureAwait(false);
        Log.Info("Shutdown guard: watching the running machines.");
        var started = DateTime.UtcNow;
        var empty = 0;
        while (!ct.IsCancellationRequested)
        {
            var running = MachineRunner.ListRunning(_paths);
            empty = running.Count == 0 ? empty + 1 : 0;
            if (empty >= 2 && DateTime.UtcNow - started > Grace) break;
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (!_goingDown) await HoldAsync(running, AppSettings.Load(_paths)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                await Task.Delay(Poll, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Log.Info("Shutdown guard: no machine runs any more; stopped.");
    }

    /// <summary>The locks the running machines and the settings call for, the warning re-taken when its names change.</summary>
    private async Task HoldAsync(IReadOnlyList<RunningMachine> running, AppSettings settings)
    {
        var why = running.Count == 0 || !settings.WarnAtHostShutdown ? null : WarningText(running, settings.SaveAtHostShutdown);
        if (why != _warningWhy)
        {
            // The new lock first, then the old one goes: never a moment with machines running and no warning.
            var next = why is null ? null : await _logind.InhibitWarningAsync("shutdown", Who, why).ConfigureAwait(false);
            _warning?.Dispose();
            _warning = next;
            _warningWhy = why;
        }

        var wantDelay = running.Count > 0 && settings.SaveAtHostShutdown;
        if (wantDelay && _delay is null) _delay = await _logind.InhibitAsync("shutdown", Who, "Saving the running machines", "delay").ConfigureAwait(false);
        else if (!wantDelay && _delay is not null)
        {
            _delay.Dispose();
            _delay = null;
        }
    }

    /// <summary>What the desktop's shut-down dialog says about VM Qontrol.</summary>
    public static string WarningText(IReadOnlyList<RunningMachine> running, bool saving)
    {
        var names = string.Join(", ", running.Select(r => r.Name).Order(StringComparer.CurrentCultureIgnoreCase));
        var what = running.Count == 1 ? $"The machine {names} is running" : $"{running.Count} machines are running ({names})";
        return saving
            ? $"{what}. Shutting down now saves as much as there is time for; suspend or shut them down in VM Qontrol first to be sure."
            : $"{what}. Shutting down now turns them off as a power cut would.";
    }

    private async Task OnShutdownAsync(bool going)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!going)
            {
                // The shutdown was called off: the next look takes the delay lock again.
                Log.Info("Shutdown guard: the shutdown was called off.");
                _goingDown = false;
                return;
            }

            _goingDown = true;
            if (_delay is null) return; // saving is off
            var budget = await _logind.DelayBudgetAsync().ConfigureAwait(false);
            var running = MachineRunner.ListRunning(_paths);
            Log.Info($"Shutdown guard: this computer is shutting down; saving {running.Count} machine(s) within {budget.TotalSeconds:0.#} s.");
            Log.Flush(TimeSpan.FromMilliseconds(200));
            using var deadline = new CancellationTokenSource(budget > Margin ? budget - Margin : TimeSpan.Zero);
            await Task.WhenAll(running.Select(r => SaveAsync(_paths, r, deadline.Token))).ConfigureAwait(false);
            _delay.Dispose();
            _delay = null;
            Log.Flush(TimeSpan.FromMilliseconds(300));
        }
        catch (Exception ex) when (ex is DBusConnectionException or DBusErrorReplyException)
        {
            Log.Warn($"Shutdown guard: {ex.Message}");
            _delay?.Dispose();
            _delay = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Suspends one machine; one that cannot be suspended (a host drive) is asked to shut down instead.</summary>
    private static async Task SaveAsync(QontrolPaths paths, RunningMachine running, CancellationToken deadline)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await MachineSuspend.SuspendAsync(running, ct: deadline).ConfigureAwait(false);
            Log.Info($"Shutdown guard: {running.Name} saved in {watch.Elapsed.TotalSeconds:0.0} s.");
            Note(paths, running, $"This computer shut down: the machine was saved in {watch.Elapsed.TotalSeconds:0.0} s and carries on at the next power on.");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Shutdown guard: {running.Name} could not be saved in the time this computer allows; it turns off with it.");
            Note(paths, running, "This computer shut down: there was not time to save the machine, so it turned off as at a power cut. Suspend or shut it down first next time.");
        }
        catch (Exception ex) when (ex is QmpException or IOException or TimeoutException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Log.Warn($"Shutdown guard: {running.Name} could not be saved ({ex.Message}); asking it to shut down.");
            Note(paths, running, $"This computer shut down: the machine could not be saved ({ex.Message}), so it was asked to shut down.");
            try
            {
                await MachineRunner.ShutDownAsync(running, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception inner) when (inner is QmpException or IOException or TimeoutException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>After sleep the guests' clocks are behind by the time asleep: each with guest tools is set to now.</summary>
    private async Task SetClocksAsync()
    {
        await Task.WhenAll(MachineRunner.ListRunning(_paths).Select(async r =>
        {
            var agent = new GuestAgentClient(r.Paths.Agent);
            for (var i = 0; i < 10; i++)
            {
                if (await agent.SetTimeAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false))
                {
                    Log.Info($"Shutdown guard: {r.Name}'s clock set after sleep.");
                    Note(_paths, r, "This computer woke from sleep: the guest's clock was set to the right time.");
                    return;
                }

                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
    }

    /// <summary>The file on the machine's Log tab where the guard says what it did.</summary>
    public static string NotesFile(QontrolPaths paths, string machineId) => Path.Combine(paths.State, "machines", machineId, "shutdown.log");

    private static void Note(QontrolPaths paths, RunningMachine running, string text)
    {
        try
        {
            var file = NotesFile(paths, running.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {text}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Release()
    {
        _warning?.Dispose();
        _warning = null;
        _delay?.Dispose();
        _delay = null;
    }
}
