using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.WindowsApps;

/// <summary>Where this computer reaches a machine's Remote Desktop.</summary>
/// <param name="Target">FreeRDP's /v: value: a UNIX socket path, or address:port.</param>
/// <param name="Socket">Whether it is a UNIX socket (no TCP port anywhere).</param>
public sealed record RdpRoute(string Target, bool Socket);

/// <summary>
/// D43 steps 20c/20e: opening a Windows program as a window on this desktop. One FreeRDP client per machine (Windows
/// client editions have one interactive session and each connection takes it over), started with the first program;
/// every later program starts inside that session through the launcher, at once. The client reaches Windows through a
/// UNIX socket QEMU forwards to the guest's port 3389 (hostfwd=unix, added live), so no TCP port is opened at all; an
/// older QEMU without it gets a 127.0.0.1 port instead. The certificate is pinned to the fingerprint the guest agent read
/// from Windows itself, and the password reaches FreeRDP on its standard input, never on a command line.
/// </summary>
public static class WindowsAppsClient
{
    /// <summary>FreeRDP 3's X11 client (it runs on Wayland through XWayland; the SDL client has no RemoteApp).</summary>
    public static string? FindFreeRdp()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, "xfreerdp3"))) return Path.Combine(dir, "xfreerdp3");
        }

        // Fedora names FreeRDP 3's client plain xfreerdp.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var plain = Path.Combine(dir, "xfreerdp");
            if (File.Exists(plain) && MajorVersion(plain) >= 3) return plain;
        }

        return null;
    }

    private static int MajorVersion(string program)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(program, "/version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return 0;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var at = text.IndexOf("version ", StringComparison.Ordinal);
            return at >= 0 && int.TryParse(text.AsSpan(at + 8, 1), out var major) ? major : 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    /// <summary>How to install the client here.</summary>
    public static string FreeRdpInstall => Hosts.Doctor.InstallCommand(Hosts.Doctor.DetectThisDistro(), "freerdp");

    public static string SocketPath(RuntimePaths runtime) => Path.Combine(runtime.Directory, "rdp.sock");

    private static string StateFile(RuntimePaths runtime) => Path.Combine(runtime.Directory, "windows-apps.json");

    /// <summary>Beside the machine's other logs (kept after power off; the Log tab shows it). The password never goes in.</summary>
    public static string LogFile(QontrolPaths paths, MachineConfig config) => Path.Combine(MachineRunner.LogDirectory(paths, config), "windows-apps.log");

    private sealed record ClientState(int Pid, string Route, string Account);

    /// <summary>The account the machine's last client signed in as.</summary>
    public static string? ClientAccount(RuntimePaths runtime)
    {
        try { return File.Exists(StateFile(runtime)) ? JsonSerializer.Deserialize<ClientState>(File.ReadAllText(StateFile(runtime)))?.Account : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The machine's running client, if one is up.</summary>
    public static int? RunningClient(RuntimePaths runtime)
    {
        try
        {
            if (!File.Exists(StateFile(runtime))) return null;
            var state = JsonSerializer.Deserialize<ClientState>(File.ReadAllText(StateFile(runtime)));
            if (state is null) return null;
            var cmdline = File.Exists($"/proc/{state.Pid}/cmdline") ? File.ReadAllText($"/proc/{state.Pid}/cmdline") : "";
            return cmdline.Contains("freerdp", StringComparison.Ordinal) ? state.Pid : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The way in: QEMU's user network forwards a UNIX socket in the machine's private runtime folder to the guest's
    /// port 3389 (added while the machine runs: HMP hostfwd_add, whose reply is empty on success and the error otherwise,
    /// the one place VM Qontrol reads HMP text). A bridged machine is reached at the address its agent reports.
    /// </summary>
    public static async Task<(RdpRoute? Route, string? Why)> RouteAsync(MachineConfig config, RunningMachine running, CancellationToken ct)
    {
        var nic = config.Nics.FirstOrDefault();
        if (nic is null || nic.Mode is NetMode.None)
            return (null, "This machine has no network adapter, and Windows apps reach Windows through one. Add one in Settings, Network.");
        if (nic.Mode is NetMode.Segment)
            return (null, "This machine is on a private segment, which this computer cannot reach. Use NAT or bridged networking for Windows apps.");
        if (nic.Mode is NetMode.Bridge)
        {
            var addresses = await new GuestAgentClient(running.Paths.Agent).AddressesAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var v4 = addresses?.FirstOrDefault(a => !a.IPv6 && !a.Address.StartsWith("169.254.", StringComparison.Ordinal) && !a.Address.StartsWith("127.", StringComparison.Ordinal));
            return v4 is null ? (null, "Windows has no network address yet; wait until it is on the network.") : (new RdpRoute(v4.Address, false), null);
        }

        var socket = SocketPath(running.Paths);
        if (File.Exists(socket)) return (new RdpRoute(socket, true), null);
        await using var qmp = await QmpClient.ConnectAsync(running.Paths.Control, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var reply = await HmpAsync(qmp, $"hostfwd_add nic unix:{socket}-:3389", ct).ConfigureAwait(false);
        if (reply.Length == 0 && File.Exists(socket)) return (new RdpRoute(socket, true), null);

        // An older QEMU or libslirp without UNIX forwards: a port on 127.0.0.1 only.
        if (PortAllocator.FirstFree(23389, 200) is not { } port) return (null, "No free port was found for Remote Desktop.");
        reply = await HmpAsync(qmp, $"hostfwd_add nic tcp:127.0.0.1:{port}-:3389", ct).ConfigureAwait(false);
        return reply.Length == 0
            ? (new RdpRoute($"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}", false), null)
            : (null, $"QEMU would not forward Remote Desktop ({reply.Trim()}).");
    }

    private static async Task<string> HmpAsync(QmpClient qmp, string command, CancellationToken ct) =>
        (await qmp.ExecuteAsync("human-monitor-command", new System.Text.Json.Nodes.JsonObject { ["command-line"] = command }, ct).ConfigureAwait(false)).Str() ?? "";

    /// <summary>The account FreeRDP signs in as: the name without the computer part ("WIN-TEST\tester" → "tester").</summary>
    public static string AccountName(string signedIn) => signedIn.Split('\\')[^1];

    /// <summary>
    /// The password an Easy Install machine was made with, from its answer disc (kept in the machine folder, readable by
    /// this account only); <c>null</c> for any other machine.
    /// </summary>
    public static string? EasyInstallPassword(MachineConfig config, string machineDirectory, string account)
    {
        if (config.EasyInstall is not { } easy || !easy.UserName.Equals(account, StringComparison.OrdinalIgnoreCase)) return null;
        var iso = config.Media.FirstOrDefault(m => m.Role is MediaRole.Unattended)?.File ?? "unattended.iso";
        var path = Path.IsPathRooted(iso) ? iso : Path.Combine(machineDirectory, iso);
        try
        {
            using var files = IsoFiles.Open(path);
            if (files?.Read("autounattend.xml", 1 << 20) is not { } bytes) return null;
            XNamespace u = "urn:schemas-microsoft-com:unattend";
            var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
            return doc.Descendants(u + "AutoLogon").Elements(u + "Password").Elements(u + "Value").FirstOrDefault()?.Value is { Length: > 0 } pw ? pw : null;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>FreeRDP's arguments, one per line for /args-from:stdin (the password among them, so never on a command line).</summary>
    public static IReadOnlyList<string> Arguments(RdpRoute route, string account, string password, string certificateSha256, WindowsApp app, string windowClass) =>
    [
        "/v:" + route.Target,
        "/u:" + account,
        "/p:" + password,
        "/cert:fingerprint:sha256:" + certificateSha256,
        // Windows' own accounts sign in with NTLM inside NLA; without this FreeRDP first asks a Kerberos realm that is not there.
        "/auth-pkg-list:!kerberos",
        AppOption(app),
        "/wm-class:" + windowClass,
        "+clipboard",
        .. (SoundServer() ? ["/sound:sys:pulse"] : Array.Empty<string>()),
        "/log-level:WARN",
    ];

    /// <summary>
    /// The first program of a new client. A Store app is not a program RemoteApp can start, so the launcher itself is
    /// started as the session's program and activates the app as Start would (never explorer.exe: it would become the
    /// session's shell and put a Windows desktop and taskbar over this computer's screen; seen s14).
    /// </summary>
    public static string AppOption(WindowsApp app)
    {
        static string Clean(string text) => text.Replace(",", " ", StringComparison.Ordinal);
        var name = Clean(app.Name);
        if (app.Program is not { } exe) return $"/app:program:{WindowsAppsLauncher.Exe},cmd:activate-here {Clean(app.Id)},name:{name}";
        return string.IsNullOrWhiteSpace(app.Arguments) ? $"/app:program:{Clean(exe)},name:{name}" : $"/app:program:{Clean(exe)},cmd:{Clean(app.Arguments)},name:{name}";
    }

    /// <summary>
    /// Whether this session has a PulseAudio server (PipeWire's pulse socket counts). FreeRDP fails the whole connection
    /// when the sound channel it was asked for cannot open, so sound is asked for only when there is somewhere to play it.
    /// </summary>
    public static bool SoundServer()
    {
        if (Environment.GetEnvironmentVariable("PULSE_SERVER") is { Length: > 0 } server)
            return !server.StartsWith("unix:", StringComparison.Ordinal) || File.Exists(server[5..]);
        return Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run && File.Exists(Path.Combine(run, "pulse", "native"));
    }

    /// <summary>The window class every window of a machine's apps carries (FreeRDP gives all of a client's windows one).</summary>
    public static string WindowClass(MachineConfig config) => "vm-qontrol-" + config.Id[..Math.Min(12, config.Id.Length)];

    /// <summary>What happened when a program was asked for.</summary>
    /// <param name="Opened">Whether the program's window is on its way.</param>
    /// <param name="Why">Why not, in words.</param>
    /// <param name="PasswordRefused">Windows refused the account's password.</param>
    /// <param name="Kept">After a new password: "" when it was kept in the keyring, else why it was not; null = nothing new to keep.</param>
    public sealed record OpenResult(bool Opened, string? Why, bool PasswordRefused = false, string? Kept = null);

    /// <summary>
    /// Opens <paramref name="app"/>: inside the machine's open client when there is one, else by starting the client with
    /// it. <paramref name="password"/> is asked only when a new client must sign in.
    /// </summary>
    public static async Task<OpenResult> OpenAsync(MachineConfig config, QontrolPaths paths, RunningMachine running, WindowsApp app,
        Func<string, CancellationToken, Task<string?>> password, CancellationToken ct)
    {
        var agent = new GuestAgentClient(running.Paths.Agent);
        if (RunningClient(running.Paths) is not null)
        {
            if (await WindowsAppsSession.EnsureLauncherAsync(agent, ct).ConfigureAwait(false) is { } noLauncher) return new(false, noLauncher);
            for (var attempt = 0; ; attempt++)
            {
                var why = await WindowsAppsSession.RunAsync(agent, app, ct).ConfigureAwait(false);
                if (why is null) return new(true, null);
                // The client may still be signing in; give it a little time before starting another.
                if (attempt >= 10 || RunningClient(running.Paths) is null) break;
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }

        if (FindFreeRdp() is not { } freeRdp)
            return new(false, $"Windows apps need FreeRDP 3, which is not installed ({FreeRdpInstall}).");
        if (Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 })
            return new(false, "Windows apps open through X11 (XWayland on Wayland), and this session has none.");
        var ready = await WindowsAppsSetup.PrepareAsync(agent, ct).ConfigureAwait(false);
        if (!ready.Ready) return new(false, ready.Why);
        if (ready.CertificateSha256 is not { } cert) return new(false, "Windows did not report its Remote Desktop certificate.");
        var account = config.WindowsApps?.Account ?? (ready.SignedIn is { } who ? AccountName(who) : config.EasyInstall?.UserName);
        if (account is null) return new(false, "Nobody has signed in to this Windows yet, so the account to use is not known. Sign in on its screen once.");
        var (route, noRoute) = await RouteAsync(config, running, ct).ConfigureAwait(false);
        if (route is null) return new(false, noRoute);
        if (await password(account, ct).ConfigureAwait(false) is not { } secret) return new(false, "No password was given.");

        if (await WindowsAppsSession.EnsureLauncherAsync(agent, ct).ConfigureAwait(false) is { } noStarter) return new(false, noStarter);
        await WindowsAppsSession.DisconnectConsoleAsync(agent, ct).ConfigureAwait(false);
        var log = LogFile(paths, config);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var start = new ProcessStartInfo("setsid") { UseShellExecute = false, RedirectStandardInput = true };
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec \"$0\" /args-from:stdin >> \"$1\" 2>&1");
        start.ArgumentList.Add(freeRdp);
        start.ArgumentList.Add(log);
        File.AppendAllText(log, $"{DateTimeOffset.Now:O} opening {app.Name} as {account} through {route.Target}{Environment.NewLine}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FreeRDP did not start.");
        await process.StandardInput.WriteAsync(string.Join('\n', Arguments(route, account, secret, cert, app, WindowClass(config))) + "\n").ConfigureAwait(false);
        process.StandardInput.Close();
        File.WriteAllText(StateFile(running.Paths), JsonSerializer.Serialize(new ClientState(process.Id, route.Target, account)));

        // A refused password ends the client within seconds; a good one leaves it running.
        for (var waited = 0; waited < 15 && !process.HasExited; waited++)
        {
            if (await WindowsAppsSession.ActiveSessionAsync(agent, ct).ConfigureAwait(false) is not null) return new(true, null);
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        if (!process.HasExited) return new(true, null);
        var tail = File.Exists(log) ? File.ReadLines(log).TakeLast(30).ToList() : [];
        var refused = tail.Any(l => l.Contains("LOGON_FAILURE", StringComparison.Ordinal) || l.Contains("AUTHENTICATION_FAILED", StringComparison.Ordinal) || l.Contains("STATUS_LOGON_FAILURE", StringComparison.Ordinal));
        return refused
            ? new(false, $"Windows refused the password for {account}.", PasswordRefused: true)
            : new(false, $"FreeRDP stopped ({tail.LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal))?.Trim() ?? "see windows-apps.log"}).");
    }
}
