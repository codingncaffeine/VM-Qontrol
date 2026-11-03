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

    /// <summary>What did not come with the machine (a host drive that is unplugged or was refused); plain sentences.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];
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

    /// <summary>Start paused, before the firmware runs (tests).</summary>
    public bool Paused { get; init; }

    /// <summary>Open the firmware's setup screen (UEFI) or boot menu (BIOS) instead of starting the system.</summary>
    public bool FirmwareSetup { get; init; }
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
        if (m.IsTemplate)
            throw new StartRefusedException([$"{m.Name} is a template. A template is never started, so every machine made from it begins the same way: make a machine from it, or turn it back into a machine."]);
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

        // Discard changes at power off: QEMU writes to an overlay of each disk, never the disk itself (DiscardSession).
        // A machine carrying on from a suspended session keeps its overlays (its saved state names them).
        var systemDisk = m.Disks.Count > 0 ? Full(m.Disks[0].File) : null;
        var suspendedSession = MachineSuspend.IsSuspended(machineDirectory) && !options.FirmwareSetup && DiscardSession.Exists(machineDirectory);
        if (!suspendedSession && DiscardSession.Exists(machineDirectory) && m.OnPowerOff == DiscardMode.Keep)
            await DiscardSession.KeepAsync(m, machineDirectory, ct).ConfigureAwait(false); // switched to Keep with a session waiting: keep it
        if (m.OnPowerOff != DiscardMode.Keep && !suspendedSession) m = await DiscardSession.BeginAsync(m, machineDirectory, ct).ConfigureAwait(false);

        // Devices the machine always connects that this account may not open yet: spice-gtk's helper opens them to it
        // long enough for QEMU to take them (what virt-viewer does); a refusal is logged and the machine still starts.
        if (m.Usb.Passthrough.Count > 0)
        {
            var present = Devices.UsbHost.List().Where(d => m.Usb.Passthrough.Any(u => u.VendorId == d.VendorId && u.ProductId == d.ProductId));
            foreach (var refusal in await Devices.UsbAccess.GrantAllAsync(present, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
                Log.Warn($"{m.Name}: USB {refusal}");
        }

        // A host serial port that is unplugged or that this account may not open would stop QEMU from starting at all:
        // it is left out, and the notice says why.
        var notices = new List<string>();
        if (m.HostSerialPorts.Count > 0)
        {
            var ports = Devices.HostSerial.List();
            var usable = new List<HostSerialConfig>();
            foreach (var s in m.HostSerialPorts)
            {
                var label = s.Name.Length > 0 ? s.Name : s.Path;
                // A port the list does not know (a pseudo-terminal, an unusual driver) is used as named when it opens.
                if (Devices.HostSerial.Find(s.Path, ports) is not { } here)
                {
                    if (!s.Path.StartsWith("/dev/disk/", StringComparison.Ordinal) && Devices.HostSerial.CanOpen(s.Path)) usable.Add(s);
                    else notices.Add($"{label} is not connected to this computer.");
                }
                else if (!here.Accessible) notices.Add($"{label} was left out: this account may not open it yet. The machine's Settings, Serial port, give access.");
                else usable.Add(s with { Path = here.DevicePath });
            }

            m = m with { HostSerialPorts = usable };
        }

        // Machines on a private segment get their addresses from VM Qontrol's own server for it (D25), started with the first.
        var appSettings = AppSettings.Load(paths);
        foreach (var segment in m.Nics.Where(n => n.Mode == NetMode.Segment).Select(n => n.Segment ?? "default").Distinct(StringComparer.Ordinal))
            SegmentServer.EnsureRunning(paths, segment, appSettings.Segments.FirstOrDefault(s => s.Name == segment));

        var nic = m.Nics.FirstOrDefault();
        var ssh = nic is { SshForward: true, Mode: NetMode.User or NetMode.Restricted } ? nic.SshHostPort ?? PortAllocator.FirstFree(22220) : null;
        if (nic is { SshForward: true, Mode: NetMode.User or NetMode.Restricted } && ssh is null)
        {
            // Every port in the SSH range is taken: start without the forward rather than clash on one.
            Log.Warn($"{m.Name}: no free host port for SSH (22220-22229 are in use); starting without it.");
            m = m with { Nics = [nic with { SshForward = false }, .. m.Nics.Skip(1)] };
        }

        var context = new LaunchContext
        {
            Host = host,
            MachineDirectory = machineDirectory,
            Runtime = runtime,
            SshHostPort = ssh ?? 22220,
            SystemDiskUsed = systemDisk is not null && DiskState.IsUsed(systemDisk),
            Headless = options.Headless,
            NatSubnet = appSettings.NatSubnet is { } nat && Runtime.SegmentSubnet.Parse(nat) is { } natNet ? natNet.ToString() : null,
            FirmwareMenu = options.FirmwareSetup && m.Firmware.Kind == FirmwareKind.Bios && m.Guest.Os != "macos",
            InstallSignal = Install.InstallSignal.Prepare(m, machineDirectory),
        };
        var launch = QemuCommandBuilder.Build(m, context);
        if (launch.Errors.Count > 0) throw new StartRefusedException(launch.Errors);
        foreach (var w in launch.Warnings) Log.Warn($"{m.Name}: {w}");
        // Memory from huge pages: the pool must have room now, or QEMU fails with an allocation error.
        var memoryMiB = MemoryOf(launch.Args);
        if (m.Performance.HugePages is { } hugeSize && Hosts.HugePagePool.Sizes().FirstOrDefault(s => s.Label == hugeSize) is { } pool && pool.Free < pool.PagesFor(memoryMiB))
            throw new StartRefusedException([$"{m.Name} takes its memory from {hugeSize} huge pages: it needs {pool.PagesFor(memoryMiB)}, and {pool.Free} are free. Reserve them in Settings, Performance, or turn huge pages off."]);

        // PCI devices: what cannot be fixed refuses the start; the rest is done with one password prompt (D27).
        if (m.Performance.PciDevices.Count > 0)
        {
            var pci = Hosts.PciHost.List();
            if (Hosts.Vfio.Blocking(m.Performance.PciDevices, pci) is { Count: > 0 } blocking) throw new StartRefusedException(blocking);
            if (Hosts.Vfio.Missing(m.Performance.PciDevices, memoryMiB, pci).Count > 0)
            {
                if (await Hosts.Vfio.PrepareAsync(m.Performance.PciDevices, runtime.Pid, ct).ConfigureAwait(false) is { } why)
                    throw new StartRefusedException([$"The PCI devices were not handed over: {why}."]);
                if (Hosts.Vfio.Missing(m.Performance.PciDevices, memoryMiB, Hosts.PciHost.List()) is { Count: > 0 } still) throw new StartRefusedException(still);
            }
        }

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

        // Each shared folder's virtiofsd, in a session of its own so it outlives this process; it ends with the machine.
        var shareProcesses = new List<int>();
        foreach (var share in launch.Shares)
        {
            var start = new ProcessStartInfo("setsid") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in share.Args) start.ArgumentList.Add(a);
            var process = Process.Start(start) ?? throw new StartRefusedException(["virtiofsd could not be started."]);
            shareProcesses.Add(process.Id);
            await WaitForAsync(() => File.Exists(share.Socket) || process.HasExited, TimeSpan.FromSeconds(5), "virtiofsd did not open its socket", ct).ConfigureAwait(false);
            if (process.HasExited)
                throw new StartRefusedException([$"A shared folder could not be shared: {(await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim()}"]);
        }

        var hostDrives = m.HostDrives.Count > 0;
        // A cable left out at power on is pulled before the firmware runs, so the guest never sees a link.
        var cableOut = m.Nics.FirstOrDefault() is { Mode: not NetMode.None, ConnectedAtPowerOn: false };

        // A suspended machine carries on from its saved state (the firmware screen means starting afresh instead).
        var resuming = MachineSuspend.IsSuspended(machineDirectory) && !options.FirmwareSetup;
        if (options.FirmwareSetup) MachineSuspend.Discard(machineDirectory);
        List<string> Args(IReadOnlyList<string> built)
        {
            var list = new List<string>(built);
            // Host drives plug in before the firmware runs, so the machine waits stopped until they are in.
            if (options.Paused || ((hostDrives || cableOut) && !resuming)) list.Add("-S");
            if (resuming) list.AddRange(["-incoming", "defer"]);
            // QEMU detaches once it is ready; until then its errors come back on stderr and the exit code says whether it started.
            list.Add("-daemonize");
            return list;
        }

        // A resumed machine runs the command line it was suspended with (its state fits only that hardware); only the
        // SSH port, which belongs to this computer and not the machine, is chosen again.
        var saved = resuming ? MachineSuspend.SavedCommand(machineDirectory) : null;
        IReadOnlyList<string> Built(int? port) => saved is null ? QemuCommandBuilder.Build(m, context with { SshHostPort = port ?? 22220 }).Args : WithSshPort(saved, port);
        var args = Args(saved is null ? launch.Args : Built(ssh));
        try
        {
            string stderr;
            while (true)
            {
                try
                {
                    stderr = await RunDaemonAsync(QemuProgram, args, "QEMU", ct).ConfigureAwait(false);
                    break;
                }
                catch (StartRefusedException ex) when (ssh is { } taken && nic?.SshHostPort is null
                    && ex.Message.Contains("Could not set up host forwarding rule", StringComparison.Ordinal)
                    && PortAllocator.FirstFree(22220, after: taken) is { } next)
                {
                    // Another program took the SSH port between choosing it and QEMU binding it: take the next one.
                    Log.Warn($"{m.Name}: host port {taken} was taken as the machine started; using {next} for SSH.");
                    ssh = next;
                    args = Args(Built(next));
                }
            }

            if (stderr.Length > 0) await File.WriteAllTextAsync(Path.Combine(logs, "qemu.log"), stderr, ct).ConfigureAwait(false);
        }
        catch
        {
            if (ssh is { } unused) PortAllocator.Release(unused);
            if (swtpmPid is { } p) TryKill(p);
            foreach (var sharePid in shareProcesses) TryKill(sharePid);
            // QEMU opens its sockets before it can fail on a device; a refused start leaves nothing behind.
            TryDelete(runtime.Directory);
            throw;
        }

        if (ssh is { } bound) PortAllocator.Release(bound);
        var pid = ReadPid(runtime.Pid) ?? throw new StartRefusedException(["QEMU started but wrote no process id."]);
        // Pinning, as soon as QEMU's threads exist (before the guest runs, when it was started stopped).
        if (m.Performance.PinnedCpus.Count > 0 || m.Performance.EmulatorCpus.Count > 0)
        {
            try
            {
                await using var pinQmp = await QmpClient.ConnectAsync(runtime.Control, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                foreach (var problem in await CpuPinning.ApplyAsync(pinQmp, pid, m.Performance.PinnedCpus, m.Performance.EmulatorCpus, ct).ConfigureAwait(false))
                {
                    Log.Warn($"{m.Name}: {problem}");
                    notices.Add(problem);
                }
            }
            catch (Exception ex) when (ex is IOException or QmpException or System.Net.Sockets.SocketException)
            {
                notices.Add($"The machine's processors were not pinned: {ex.Message}");
            }
        }

        // Before the boot helpers below: they press keys the firmware must see running.
        if ((hostDrives || cableOut) && !resuming) notices.AddRange(await PrepareStoppedAsync(m, runtime, cableOut, run: !options.Paused, ct).ConfigureAwait(false));
        else if (hostDrives) notices.Add("The host drives were not connected: a machine carrying on from a saved state keeps the hardware it was saved with.");
        var installing = !resuming && !(systemDisk is not null && DiskState.IsUsed(systemDisk))
                         && m.Media.Any(x => x.Role == MediaRole.Installer && x.Connect != Connect.Never);
        var assist = options.FirmwareSetup && !options.Paused
            ? PressKeysAsync(runtime, m.Name, "esc", 24, "open the firmware screen")
            : installing && m.Guest.Os is "windows" or "windows-server" && m.Firmware.Kind == FirmwareKind.Uefi && !options.Paused
                ? PressKeysAsync(runtime, m.Name, "ret", 40, "start the installer")
                : Task.CompletedTask;
        var running = new RunningMachine(m.Id, m.Name, pid, runtime.Directory, machineDirectory, DateTimeOffset.Now, ssh) { BootAssist = assist, Notices = notices };
        await File.WriteAllTextAsync(runtime.Lock, JsonSerializer.Serialize(new JsonObject
        {
            ["id"] = m.Id,
            ["name"] = m.Name,
            ["pid"] = pid,
            ["machineDirectory"] = machineDirectory,
            ["started"] = running.Started.ToString("O"),
            ["sshPort"] = ssh,
        }, LockJson), ct).ConfigureAwait(false);
        Log.Info($"{m.Name}: started (process {pid}){(ssh is { } port ? $", SSH on host port {port}" : "")}{(resuming ? ", resuming" : "")}");
        if (resuming)
        {
            try
            {
                await MachineSuspend.FinishResumeAsync(running, run: !options.Paused, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is StartRefusedException or QmpException or IOException or OperationCanceledException)
            {
                // The saved state stays for the person to discard; nothing of this attempt does.
                try
                {
                    await PowerOffAsync(running, CancellationToken.None).ConfigureAwait(false);
                    await WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stop) when (stop is IOException or QmpException or System.Net.Sockets.SocketException)
                {
                }

                if (swtpmPid is { } p) TryKill(p);
                foreach (var sharePid in shareProcesses) TryKill(sharePid);
                TryDelete(runtime.Directory);
                throw ex as StartRefusedException ?? new StartRefusedException([$"The suspended state could not be loaded ({ex.Message}). Discard it to start the machine afresh."]);
            }
        }

        return running;
    }

    /// <summary>
    /// Pulls the network cable when it is left out at power on, and plugs the machine's host drives in, while it waits stopped (-S), then lets it run. A drive that is unplugged, busy or
    /// refused is left out and named; the machine still starts, as a computer does with an empty USB port.
    /// </summary>
    private static async Task<IReadOnlyList<string>> PrepareStoppedAsync(MachineConfig m, RuntimePaths runtime, bool cableOut, bool run, CancellationToken ct)
    {
        var notices = new List<string>();
        await using var qmp = await QmpClient.ConnectAsync(runtime.Control, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        try
        {
            if (cableOut)
            {
                try
                {
                    await LiveNetwork.SetCableAsync((c, a) => qmp.ExecuteAsync(c, a, ct), runtime, connected: false, ct).ConfigureAwait(false);
                }
                catch (QmpException ex)
                {
                    notices.Add($"The network cable could not be left out: {ex.Description}");
                }
            }

            foreach (var drive in m.HostDrives)
            {
                try
                {
                    await LiveHostDrives.AttachAsync(qmp, drive, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Devices.DriveAccessException or QmpException or IOException)
                {
                    var why = ex is QmpException q ? q.Description : ex.Message;
                    Log.Warn($"{m.Name}: host drive {drive.Path} not connected: {why}");
                    notices.Add(why);
                }
            }
        }
        finally
        {
            if (run) await qmp.ExecuteAsync("cont", ct: CancellationToken.None).ConfigureAwait(false);
        }

        return notices;
    }

    /// <summary>
    /// Presses a key every half second while the machine starts. Microsoft's installation discs wait a few seconds for
    /// "Press any key to boot from CD or DVD" on UEFI (with nobody there the machine falls through to network boot), so
    /// Enter is pressed while a Windows machine installs; Esc opens the firmware's setup screen or boot menu. Only the
    /// firmware and the boot manager see the keys.
    /// </summary>
    private static async Task PressKeysAsync(RuntimePaths runtime, string name, string key, int times, string purpose)
    {
        try
        {
            await using var qmp = await QmpClient.ConnectAsync(runtime.Control, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var press = new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["type"] = "qcode", ["data"] = key }) };
            for (var i = 0; i < times; i++)
            {
                await qmp.ExecuteAsync("send-key", (JsonObject)press.DeepClone()).ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or QmpException or System.Net.Sockets.SocketException)
        {
            Log.Warn($"{name}: could not press a key to {purpose} ({ex.Message}).");
        }
    }

    /// <summary>
    /// A saved command line with its SSH forward moved to <paramref name="port"/> (or dropped when none is free). The SSH
    /// forward is the first on the network option (the builder writes it before the person's own), so only that one moves.
    /// </summary>
    public static IReadOnlyList<string> WithSshPort(IReadOnlyList<string> args, int? port) =>
        args.Select(a => SshForward.Replace(a, port is { } p ? $",hostfwd=tcp::{p}-:22" : "", 1)).ToList();

    private static readonly System.Text.RegularExpressions.Regex SshForward = new(@",hostfwd=tcp::\d+-:22(?=,|$)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The memory a built command line gives the machine, in MiB (its -m argument: "16G" or "4096M").</summary>
    internal static long MemoryOf(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("-m");
        if (i < 0 || i + 1 >= args.Count) return 0;
        var v = args[i + 1];
        var n = long.TryParse(v.TrimEnd('G', 'M'), System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0;
        return v.EndsWith('G') ? n * 1024 : n;
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

    /// <summary>Presses keys together in the machine, e.g. ["ctrl", "alt", "delete"] (QEMU key codes).</summary>
    public static async Task SendKeysAsync(RunningMachine running, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        await using var qmp = await ControlAsync(running, ct).ConfigureAwait(false);
        var list = new JsonArray(keys.Select(k => (JsonNode)new JsonObject { ["type"] = "qcode", ["data"] = k }).ToArray());
        await qmp.ExecuteAsync("send-key", new JsonObject { ["keys"] = list }, ct).ConfigureAwait(false);
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
            {
                // A disk another process holds: say who, before QEMU's own words.
                List<string> reasons = FileHolders.Explain(text) is { } who ? [who] : [];
                reasons.Add($"{what} refused to start: {(text.Length > 0 ? text : $"exit code {process.ExitCode}")}");
                throw new StartRefusedException(reasons);
            }
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
