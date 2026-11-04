using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.WindowsApps;

/// <summary>
/// Where a Windows account's password comes from, in order: the desktop's keyring, then (an Easy Install machine) the
/// answer disc VM Qontrol wrote, then the person, asked once; a password Windows accepted is kept in the keyring, one it
/// refused is forgotten and asked again. Nothing is written to VM Qontrol's own files.
/// </summary>
public static class WindowsAppsPasswords
{
    public static string Label(MachineConfig config) => $"VM Qontrol: Windows apps on {config.Name}";

    public static async Task<string?> StoredAsync(MachineConfig config, string account, CancellationToken ct)
    {
        try
        {
            var keyring = Keyring.ForThisAccount();
            return await keyring.AvailableAsync().ConfigureAwait(false) ? await keyring.LookupAsync(Keyring.Attributes(config.Id, account), ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Tmds.DBus.Protocol.DBusConnectionException or Tmds.DBus.Protocol.DBusErrorReplyException)
        {
            Log.Warn($"The keyring could not be read ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Keeps the password in the keyring; null when kept, else why not (nothing is kept anywhere else).</summary>
    public static async Task<string?> RememberAsync(MachineConfig config, string account, string password, CancellationToken ct)
    {
        try
        {
            var keyring = Keyring.ForThisAccount();
            if (await keyring.WhyUnavailableAsync().ConfigureAwait(false) is { } why) return $"this desktop has no keyring (Secret Service: {why}), so it will be asked again next time";
            await keyring.StoreAsync(Label(config), Keyring.Attributes(config.Id, account), password, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Tmds.DBus.Protocol.DBusConnectionException or Tmds.DBus.Protocol.DBusErrorReplyException)
        {
            Log.Warn($"The password could not be kept in the keyring ({ex.Message}).");
            return $"the keyring did not take it ({ex.Message})";
        }
    }

    public static async Task ForgetAsync(MachineConfig config, string account, CancellationToken ct)
    {
        try
        {
            var keyring = Keyring.ForThisAccount();
            if (await keyring.AvailableAsync().ConfigureAwait(false)) await keyring.DeleteAsync(Keyring.Attributes(config.Id, account), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Tmds.DBus.Protocol.DBusConnectionException or Tmds.DBus.Protocol.DBusErrorReplyException)
        {
            Log.Warn($"The password could not be removed from the keyring ({ex.Message}).");
        }
    }

    /// <summary>
    /// Opens <paramref name="app"/> with the password chain: stored first, <paramref name="ask"/> only when none is
    /// stored or Windows refused it (asked at most twice). <paramref name="ask"/> gets the account and whether the last
    /// one was refused, and returns null when the person gave up.
    /// </summary>
    public static async Task<WindowsAppsClient.OpenResult> OpenAsync(MachineConfig config, string machineDirectory, QontrolPaths paths, RunningMachine running, WindowsApp app,
        Func<string, bool, CancellationToken, Task<string?>> ask, CancellationToken ct)
    {
        string? given = null;
        var refused = false;
        for (var round = 0; round < 3; round++)
        {
            var asked = false;
            var result = await WindowsAppsClient.OpenAsync(config, paths, running, app, async (account, token) =>
            {
                if (!refused && round == 0)
                {
                    if (await StoredAsync(config, account, token).ConfigureAwait(false) is { } stored) return stored;
                    if (WindowsAppsClient.EasyInstallPassword(config, machineDirectory, account) is { } answer) return given = answer;
                }

                asked = true;
                return given = await ask(account, refused, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            var account = config.WindowsApps?.Account ?? ReadAccount(running);
            if (result.Opened)
            {
                return given is not null && account is not null ? result with { Kept = await RememberAsync(config, account, given, ct).ConfigureAwait(false) ?? "" } : result;
            }

            if (!result.PasswordRefused) return result;
            refused = true;
            if (account is not null) await ForgetAsync(config, account, ct).ConfigureAwait(false);
            if (asked && given is null) return result;
        }

        return new(false, "Windows refused the password three times.", PasswordRefused: true);
    }

    private static string? ReadAccount(RunningMachine running) => WindowsAppsClient.ClientAccount(running.Paths);
}

/// <summary>Bringing a machine to the point where its Windows programs can open.</summary>
public static class WindowsAppsPower
{
    /// <summary>
    /// The running machine: started without a viewer when off (a suspended one carries on where it was), then waited on
    /// until Windows' guest agent answers. <paramref name="progress"/> hears each step in words.
    /// </summary>
    public static async Task<(RunningMachine? Running, string? Why)> ReadyAsync(MachineConfig config, string machineDirectory, QontrolPaths paths,
        Func<Hosts.HostCapabilities> host, IProgress<string> progress, CancellationToken ct)
    {
        if (!config.Guest.Os.StartsWith("windows", StringComparison.Ordinal))
            return (null, $"{config.Name} is not a Windows machine.");
        var running = MachineRunner.Find(config, paths);
        if (running is null)
        {
            progress.Report(MachineSuspend.IsSuspended(machineDirectory) ? $"Resuming {config.Name}…" : $"Starting {config.Name}…");
            try
            {
                running = await MachineRunner.StartAsync(config, machineDirectory, paths, host(), new StartOptions { Headless = true }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or MachineBusyException)
            {
                return (null, $"{config.Name} could not start: {ex.Message}");
            }
        }

        var agent = new GuestAgentClient(running.Paths.Agent);
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        var said = false;
        while (!await agent.PingAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
        {
            if (!MachineRunner.IsRunning(running)) return (null, $"{config.Name} stopped while Windows was starting.");
            if (DateTime.UtcNow > until) return (null, "Windows' guest agent did not answer within four minutes. It comes with the guest tools (Settings, Guest tools).");
            if (!said) progress.Report("Waiting for Windows…");
            said = true;
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        return (running, null);
    }
}
