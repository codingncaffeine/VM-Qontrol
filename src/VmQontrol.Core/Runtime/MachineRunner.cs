using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Core.Runtime;

/// <summary>A machine that is running: QEMU's process, its runtime folder and its forwarded SSH port.</summary>
public sealed record RunningMachine(string Id, string Name, int Pid, string RuntimeDirectory, string MachineDirectory, DateTimeOffset Started, int? SshPort)
{
    /// <summary>Work that helps the machine through its first seconds (pressing a key for a Windows disc); a short-lived caller waits for it.</summary>
    public Task BootAssist { get; init; } = Task.CompletedTask;

    public RuntimePaths Paths => new(RuntimeDirectory);
}

/// <summary>The machine could not be started; <see cref="Reasons"/> are plain sentences for the user.</summary>
public sealed class StartRefusedException(IReadOnlyList<string> reasons) : Exception(string.Join(" ", reasons))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

public sealed record StartOptions
{
    /// <summary>No screen and no sound (background work and tests).</summary>
    public bool Headless { get; init; }

    /// <summary>Start paused, before the firmware runs (power on to firmware setup, tests).</summary>
    public bool Paused { get; init; }
}

/// <summary>
/// Starts, finds and stops machines. QEMU runs as a daemon of its own (as Workstation's machines outlive its window): a
/// machine keeps running when the app or vmq exits, and is found again through its runtime folder. Nothing here runs on
/// the UI thread.
/// </summary>
public static class MachineRunner
{
    public static string QemuProgram { get; set; } = "qemu-system-x86_64";

    public static string SwtpmProgram { get; set; } = "swtpm";

    private static readonly JsonSerializerOptions LockJson = new() { WriteIndented = true };

    /// <summary>Starts the machine and returns once QEMU is ready (or throws with QEMU's own reason).</summary>
    public static async Task<RunningMachine> StartAsync(MachineConfig m, string machineDirectory, QontrolPaths paths, HostCapabilities host, StartOptions options, CancellationToken ct = default)
    {
        if (Find(m, paths) is { } already) throw new StartRefusedException([$"{m.Name} is already running."]);
        var problems = MachineChecks.Problems(m, machineDirectory);
        if (problems.Count > 0) throw new StartRefusedException(problems);

        var runtime = new RuntimePaths(paths.RuntimeFor(m.Id));
        if (Directory.Exists(runtime.Directory)) Directory.Delete(runtime.Directory, recursive: true);
        Directory.CreateDirectory(runtime.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var logs = LogDirectory(paths, m);
        Directory.CreateDirectory(logs);

        string Full(string f) => Path.IsPathRooted(f) ? f : Path.Combine(machineDirectory, f);
        if (m.Disks.Count > 0 && !File.Exists(Full(m.Disks[0].File)))
            await QemuImg.CreateAsync(Full(m.Disks[0].File), m.Disks[0].Format, m.Disks[0].SizeGiB, m.Disks[0].Preallocation, ct).ConfigureAwait(false);
        for (var i = 1; i < m.Disks.Count; i++)
        {
            if (!File.Exists(Full(m.Disks[i].File)))
                await QemuImg.CreateAsync(Full(m.Disks[i].File), m.Disks[i].Format, m.Disks[i].SizeGiB, m.Disks[i].Preallocation, ct).ConfigureAwait(false);
        }

        var nic = m.Nics.FirstOrDefault();
        var ssh = nic is { SshForward: true, Mode: NetMode.User or NetMode.Restricted } ? nic.SshHostPort ?? PortAllocator.FirstFree(22220) : null;
        var launch = QemuCommandBuilder.Build(m, new LaunchContext
        {
            Host = host,
            MachineDirectory = machineDirectory,
            Runtime = runtime,
            SshHostPort = ssh ?? 22220,
            SystemDiskUsed = m.Disks.Count > 0 && DiskState.IsUsed(Full(m.Disks[0].File)),
            Headless = options.Headless,
        });
        if (launch.Errors.Count > 0) throw new StartRefusedException(launch.Errors);
        foreach (var w in launch.Warnings) Log.Warn($"{m.Name}: {w}");
        if (launch.VarsToCreate is { } vars)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(vars.Destination)!);
            File.Copy(vars.Template, vars.Destination);
            // Distribution templates are read-only; the machine's own copy must be writable.
            File.SetUnixFileMode(vars.Destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        int? swtpmPid = null;
        if (launch.Swtpm is { } tpm)
        {
            Directory.CreateDirectory(tpm.StateDirectory);
            var pidFile = Path.Combine(runtime.Directory, "swtpm.pid");
            await RunDaemonAsync(SwtpmProgram, [.. tpm.Args, "--daemon", "--pid", $"file={pidFile}", "--log", $"file={Path.Combine(logs, "swtpm.log")}"], "swtpm", ct).ConfigureAwait(false);
            await WaitForAsync(() => File.Exists(tpm.Socket), TimeSpan.FromSeconds(5), "swtpm did not open its socket", ct).ConfigureAwait(false);
            swtpmPid = ReadPid(pidFile);
        }

        var args = new List<string>(launch.Args);
        if (options.Paused) args.Add("-S");
        // QEMU detaches once it is ready; until then its errors come back on stderr and the exit code says whether it started.
        args.Add("-daemonize");
        try
        {
            var stderr = await RunDaemonAsync(QemuProgram, args, "QEMU", ct).ConfigureAwait(false);
            if (stderr.Length > 0) await File.WriteAllTextAsync(Path.Combine(logs, "qemu.log"), stderr, ct).ConfigureAwait(false);
        }
        catch
        {
            if (swtpmPid is { } p) TryKill(p);
            // QEMU opens its sockets before it can fail on a device; a refused start leaves nothing behind.
            TryDelete(runtime.Directory);
            throw;
        }

        var pid = ReadPid(runtime.Pid) ?? throw new StartRefusedException(["QEMU started but wrote no process id."]);
        var installing = !(m.Disks.Count > 0 && DiskState.IsUsed(Full(m.Disks[0].File)))
                         && m.Media.Any(x => x.Role == MediaRole.Installer && x.Connect != Connect.Never);
        var assist = installing && m.Guest.Os is "windows" or "windows-server" && m.Firmware.Kind == FirmwareKind.Uefi && !options.Paused
            ? PressKeyToBootTheDiscAsync(runtime, m.Name)
            : Task.CompletedTask;
        var running = new RunningMachine(m.Id, m.Name, pid, runtime.Directory, machineDirectory, DateTimeOffset.Now, ssh) { BootAssist = assist };
        await File.WriteAllTextAsync(runtime.Lock, JsonSerializer.Serialize(new JsonObject
        {
            ["id"] = m.Id,
            ["name"] = m.Name,
            ["pid"] = pid,
            ["machineDirectory"] = machineDirectory,
            ["started"] = running.Started.ToString("O"),
            ["sshPort"] = ssh,
        }, LockJson), ct).ConfigureAwait(false);
        Log.Info($"{m.Name}: started (process {pid}){(ssh is { } port ? $", SSH on host port {port}" : "")}");
        return running;
    }

    /// <summary>
    /// Microsoft's installation discs wait a few seconds for "Press any key to boot from CD or DVD" when they start on UEFI
    /// firmware; with nobody there the machine falls through to network boot. While a Windows machine installs, Enter is
    /// pressed every half second for the first twenty seconds: only the firmware and the boot manager see it.
    /// </summary>
    private static async Task PressKeyToBootTheDiscAsync(RuntimePaths runtime, string name)
    {
        try
        {
            await using var qmp = await QmpClient.ConnectAsync(runtime.Control, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var key = new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["type"] = "qcode", ["data"] = "ret" }) };
            for (var i = 0; i < 40; i++)
            {
                await qmp.ExecuteAsync("send-key", (JsonObject)key.DeepClone()).ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or QmpException or System.Net.Sockets.SocketException)
        {
            Log.Warn($"{name}: could not press a key to start the installer ({ex.Message}); press any key in the viewer if it waits.");
        }
    }

    /// <summary>The machine if it is running now; a runtime folder left by a machine that has stopped is cleared.</summary>
    public static RunningMachine? Find(MachineConfig m, QontrolPaths paths)
    {
        var runtime = new RuntimePaths(paths.RuntimeFor(m.Id));
        if (!File.Exists(runtime.Lock)) return null;
        try
        {
            var info = JsonNode.Parse(File.ReadAllText(runtime.Lock))!;
            var pid = info["pid"]!.GetValue<int>();
            if (IsOurQemu(pid, runtime.Directory))
            {
                return new RunningMachine(m.Id, m.Name, pid, runtime.Directory, info["machineDirectory"]!.GetValue<string>(),
                    DateTimeOffset.Parse(info["started"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), info["sshPort"]?.GetValue<int>());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or NullReferenceException or InvalidOperationException or FormatException)
        {
        }

        try
        {
            Directory.Delete(runtime.Directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return null;
    }

    public static string LogDirectory(QontrolPaths paths, MachineConfig m) => Path.Combine(paths.State, "machines", m.Id);

    /// <summary>A short-lived QMP connection on the control socket, for one command or a few.</summary>
    public static Task<QmpClient> ControlAsync(RunningMachine running, CancellationToken ct = default) =>
        QmpClient.ConnectAsync(running.Paths.Control, TimeSpan.FromSeconds(5), ct);

    /// <summary>"running", "paused", "shutdown", "guest-panicked"... as QEMU reports it.</summary>
    public static async Task<string> StatusAsync(RunningMachine running, CancellationToken ct = default)
    {
        await using var qmp = await ControlAsync(running, ct).ConfigureAwait(false);
        return (await qmp.ExecuteAsync("query-status", ct: ct).ConfigureAwait(false))?["status"]?.GetValue<string>() ?? "unknown";
    }

    /// <summary>Asks the guest to shut down, as pressing a computer's power button does.</summary>
    public static Task ShutDownAsync(RunningMachine running, CancellationToken ct = default) => SimpleAsync(running, "system_powerdown", ct);

    public static Task ResetAsync(RunningMachine running, CancellationToken ct = default) => SimpleAsync(running, "system_reset", ct);

    public static Task PauseAsync(RunningMachine running, CancellationToken ct = default) => SimpleAsync(running, "stop", ct);

    public static Task ResumeAsync(RunningMachine running, CancellationToken ct = default) => SimpleAsync(running, "cont", ct);

    /// <summary>Turns the machine off at once, as pulling the plug does: anything unsaved in the guest is lost.</summary>
    public static async Task PowerOffAsync(RunningMachine running, CancellationToken ct = default)
    {
        await using var qmp = await ControlAsync(running, ct).ConfigureAwait(false);
        await qmp.ExecuteExpectingExitAsync("quit", ct).ConfigureAwait(false);
    }

    /// <summary>Saves the machine's screen as a PNG.</summary>
    public static async Task ScreenshotAsync(RunningMachine running, string pngFile, CancellationToken ct = default)
    {
        await using var qmp = await ControlAsync(running, ct).ConfigureAwait(false);
        await qmp.ExecuteAsync("screendump", new JsonObject { ["filename"] = Path.GetFullPath(pngFile), ["format"] = "png" }, ct).ConfigureAwait(false);
    }

    /// <summary>Waits for QEMU's process to end; <c>false</c> when it is still running after <paramref name="timeout"/>.</summary>
    public static async Task<bool> WaitForExitAsync(RunningMachine running, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsOurQemu(running.Pid, running.RuntimeDirectory)) return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task SimpleAsync(RunningMachine running, string command, CancellationToken ct)
    {
        await using var qmp = await ControlAsync(running, ct).ConfigureAwait(false);
        await qmp.ExecuteAsync(command, ct: ct).ConfigureAwait(false);
    }

    /// <summary>The process is QEMU started for this runtime folder (a reused process id is not mistaken for it).</summary>
    private static bool IsOurQemu(int pid, string runtimeDirectory)
    {
        try
        {
            var cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
            return cmdline.Contains(runtimeDirectory, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> RunDaemonAsync(string program, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new StartRefusedException([$"{what} could not be started."]);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new StartRefusedException([$"{program} is not installed. The host check names the package for your distribution."]);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var text = ((await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false))).Trim();
            if (process.ExitCode != 0)
                throw new StartRefusedException([$"{what} refused to start: {(text.Length > 0 ? text : $"exit code {process.ExitCode}")}"]);
            return text;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new StartRefusedException([failure + "."]);
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private static int? ReadPid(string file)
    {
        try
        {
            return int.TryParse(File.ReadAllText(file).Trim(), out var pid) ? pid : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
