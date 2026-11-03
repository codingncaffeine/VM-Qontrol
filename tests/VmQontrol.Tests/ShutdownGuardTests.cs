using System.Collections.Concurrent;
using System.Diagnostics;
using Tmds.DBus.Protocol;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// The shutdown guard against a stand-in logind on a private bus (packaging/isolated-bus.conf), never this computer's:
/// the stand-in hands out real locks (pipes: a lock is held while the guard keeps its end open) and sends the signals
/// logind sends. With a real Alpine guest: the warning names the machine, a shutdown saves it inside logind's 5 s, and
/// the delay lock is let go. The control: with saving off, no delay lock is taken and the machine runs on.
/// </summary>
[Trait("Tier", "Guest")]
public sealed class ShutdownGuardTests
{
    [Fact]
    public void The_warning_names_the_machines_in_order()
    {
        RunningMachine M(string name) => new(name, name, 1, "/r", "/m", DateTimeOffset.Now, null);
        Assert.Equal("The machine Web is running. Shutting down now saves as much as there is time for; suspend or shut them down in VM Qontrol first to be sure.",
            ShutdownGuard.WarningText([M("Web")], saving: true));
        Assert.Equal("2 machines are running (alpha, Web). Shutting down now turns them off as a power cut would.",
            ShutdownGuard.WarningText([M("Web"), M("alpha")], saving: false));
    }

    [Fact]
    public async Task A_shutdown_saves_the_running_machine_in_loginds_time_and_lets_go()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("guard");
        var (m, dir) = await AlpineAsync(profile, ct);
        await using var bus = await StandInLogind.StartAsync(profile, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ShutdownGuard.Poll = TimeSpan.FromMilliseconds(300);
        Task? guard = null;
        try
        {
            var running = await MachineRunner.StartAsync(m, dir, profile.Paths, Core.Hosts.HostProbe.Probe(), new StartOptions { Headless = true }, ct);
            await Task.Delay(TimeSpan.FromSeconds(8), ct); // into the guest's boot: memory in use, not a blank machine
            guard = ShutdownGuard.RunAsync(profile.Paths, new Logind(bus.ClientConnection()), stop.Token);

            var warning = await bus.WaitForLockAsync(l => l.Mode is "block-weak", ct);
            Assert.Equal("shutdown", warning.What);
            Assert.Equal("VM Qontrol", warning.Who);
            Assert.StartsWith("The machine guard-alpine is running.", warning.Why, StringComparison.Ordinal);
            var delay = await bus.WaitForLockAsync(l => l.Mode is "delay", ct);
            Assert.False(delay.Released.IsCompleted);

            var clock = Stopwatch.StartNew();
            bus.Signal("PrepareForShutdown", true);
            await delay.Released.WaitAsync(TimeSpan.FromSeconds(15), ct);
            TestContext.Current.SendDiagnosticMessage($"saved and let go in {clock.Elapsed.TotalSeconds:0.00} s");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the delay lock was held {clock.Elapsed.TotalSeconds:0.0} s, past logind's 5 s");
            Assert.True(MachineSuspend.IsSuspended(dir), "the machine was not saved");
            Assert.False(MachineRunner.IsRunning(running), "QEMU still runs after the save");
            Assert.False(warning.Released.IsCompleted, "the warning went before the shutdown did");
            Assert.Contains("the machine was saved in", File.ReadAllText(ShutdownGuard.NotesFile(profile.Paths, m.Id)), StringComparison.Ordinal);

            // The saved machine carries on: it resumes from that state.
            var resumed = await MachineRunner.StartAsync(m, dir, profile.Paths, Core.Hosts.HostProbe.Probe(), new StartOptions { Headless = true }, ct);
            Assert.False(MachineSuspend.IsSuspended(dir));
            Assert.Equal("running", await MachineRunner.StatusAsync(resumed, ct));
        }
        finally
        {
            await stop.CancelAsync();
            if (guard is not null) await guard.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }

        Assert.All(bus.Locks, l => Assert.True(l.Released.IsCompleted, $"a {l.Mode} lock outlived the guard"));
    }

    /// <summary>The control for the save: a logind that allows too little time. The save is cut short, nothing is left
    /// behind, the machine carries on (it turns off with the computer), and the lock still goes inside the time.</summary>
    [Fact]
    public async Task A_save_cut_short_by_loginds_time_leaves_nothing_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("guard-short");
        var (m, dir) = await AlpineAsync(profile, ct);
        await using var bus = await StandInLogind.StartAsync(profile, ct, budgetUsec: 900_000); // 0.2 s after the margin
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ShutdownGuard.Poll = TimeSpan.FromMilliseconds(300);
        Task? guard = null;
        try
        {
            var running = await MachineRunner.StartAsync(m, dir, profile.Paths, Core.Hosts.HostProbe.Probe(), new StartOptions { Headless = true }, ct);
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            guard = ShutdownGuard.RunAsync(profile.Paths, new Logind(bus.ClientConnection()), stop.Token);
            var delay = await bus.WaitForLockAsync(l => l.Mode is "delay", ct);

            var clock = Stopwatch.StartNew();
            bus.Signal("PrepareForShutdown", true);
            await delay.Released.WaitAsync(TimeSpan.FromSeconds(15), ct);
            TestContext.Current.SendDiagnosticMessage($"let go in {clock.Elapsed.TotalSeconds:0.00} s");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the delay lock was held {clock.Elapsed.TotalSeconds:0.0} s, past the 0.9 s allowed");
            Assert.False(MachineSuspend.IsSuspended(dir), "a state was kept from a save that did not finish");
            Assert.False(File.Exists(MachineSuspend.StatePath(dir) + ".partial"), "the cut-short save left its partial file");
            Assert.True(MachineRunner.IsRunning(running), "the machine stopped although it was not saved");
            Assert.Contains("there was not time to save the machine", File.ReadAllText(ShutdownGuard.NotesFile(profile.Paths, m.Id)), StringComparison.Ordinal);
            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct));
        }
        finally
        {
            await stop.CancelAsync();
            if (guard is not null) await guard.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }
    }

    [Fact]
    public async Task With_saving_off_no_delay_is_taken_and_the_machine_runs_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("guard-off");
        new AppSettings { SaveAtHostShutdown = false }.Save(profile.Paths);
        var (m, dir) = await AlpineAsync(profile, ct);
        await using var bus = await StandInLogind.StartAsync(profile, ct, refuseBlockWeak: true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ShutdownGuard.Poll = TimeSpan.FromMilliseconds(300);
        Task? guard = null;
        try
        {
            var running = await MachineRunner.StartAsync(m, dir, profile.Paths, Core.Hosts.HostProbe.Probe(), new StartOptions { Headless = true }, ct);
            guard = ShutdownGuard.RunAsync(profile.Paths, new Logind(bus.ClientConnection()), stop.Token);

            // An older logind without "block-weak": the guard falls back to "block", which there means the same.
            var warning = await bus.WaitForLockAsync(l => l.Mode is "block", ct);
            Assert.EndsWith("turns them off as a power cut would.", warning.Why, StringComparison.Ordinal);
            bus.Signal("PrepareForShutdown", true);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            Assert.DoesNotContain(bus.Locks, l => l.Mode is "delay");
            Assert.True(MachineRunner.IsRunning(running));
            Assert.False(MachineSuspend.IsSuspended(dir));
        }
        finally
        {
            await stop.CancelAsync();
            if (guard is not null) await guard.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }
    }

    private static async Task<(MachineConfig, string)> AlpineAsync(TestProfile profile, CancellationToken ct)
    {
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        var m = new MachineConfig
        {
            Name = "guard-alpine",
            MemoryMiB = 512,
            Cpu = new CpuConfig { Cores = 1 },
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
        };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        return (m, dir);
    }

    internal sealed record HeldLock(string What, string Who, string Why, string Mode, Task Released);

    /// <summary>A private bus with a stand-in for logind's manager: Inhibit, InhibitDelayMaxUSec (5 s) and its signals.</summary>
    private sealed class StandInLogind : IPathMethodHandler, IAsyncDisposable
    {
        private readonly Process _daemon;
        private readonly string _address;
        private readonly bool _refuseBlockWeak;
        private readonly ulong _budgetUsec;
        private DBusConnection? _connection;

        private StandInLogind(Process daemon, string address, bool refuseBlockWeak, ulong budgetUsec)
        {
            _budgetUsec = budgetUsec;
            _daemon = daemon;
            _address = address;
            _refuseBlockWeak = refuseBlockWeak;
        }

        public ConcurrentQueue<HeldLock> Locks { get; } = new();

        public string Path => "/org/freedesktop/login1";

        public bool HandlesChildPaths => false;

        public static async Task<StandInLogind> StartAsync(TestProfile profile, CancellationToken ct, bool refuseBlockWeak = false, ulong budgetUsec = 5_000_000)
        {
            Directory.CreateDirectory(profile.Paths.Runtime);
            var socket = System.IO.Path.Combine(profile.Paths.Runtime, "logind-bus");
            var config = System.IO.Path.Combine(TestProfile.ScratchRoot, "..", "packaging", "isolated-bus.conf");
            var start = new ProcessStartInfo("dbus-daemon") { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var a in new[] { $"--config-file={config}", "--nofork", "--print-address=1", $"--address=unix:path={socket}" }) start.ArgumentList.Add(a);
            var daemon = Process.Start(start)!;
            var address = (await daemon.StandardOutput.ReadLineAsync(ct))!.Trim();
            var stand = new StandInLogind(daemon, address, refuseBlockWeak, budgetUsec);
            stand._connection = new DBusConnection(address);
            await stand._connection.ConnectAsync();
            stand._connection.AddMethodHandler(stand);
            await stand._connection.RequestNameAsync("org.freedesktop.login1", RequestNameOptions.None);
            return stand;
        }

        public DBusConnection ClientConnection() => new(_address);

        public async Task<HeldLock> WaitForLockAsync(Func<HeldLock, bool> which, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                if (Locks.FirstOrDefault(which) is { } l) return l;
                Assert.True(DateTime.UtcNow < deadline, $"no such lock; taken: {string.Join(", ", Locks.Select(x => x.Mode))}");
                await Task.Delay(100, ct);
            }
        }

        public void Signal(string member, bool value)
        {
            using var writer = _connection!.GetMessageWriter();
            writer.WriteSignalHeader(null, Path, "org.freedesktop.login1.Manager", member, "b");
            writer.WriteBool(value);
            Assert.True(_connection.TrySendMessage(writer.CreateMessage()));
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (request.InterfaceAsString == "org.freedesktop.login1.Manager" && request.MemberAsString == "Inhibit")
            {
                var reader = request.GetBodyReader();
                var (what, who, why, mode) = (reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString());
                if (mode is "block-weak" && _refuseBlockWeak)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Invalid mode specification block-weak");
                    return ValueTask.CompletedTask;
                }

                var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
                // Released once every copy of the write end is closed: the read sees the end of the pipe.
                var released = Task.Run(() =>
                {
                    using (pipe) _ = pipe.Read(new byte[1]);
                });
                Locks.Enqueue(new HeldLock(what, who, why, mode, released));
                using var writer = context.CreateReplyWriter("h");
                writer.WriteHandle(pipe.ClientSafePipeHandle);
                context.Reply(writer.CreateMessage());
                pipe.DisposeLocalCopyOfClientHandle(); // the guard's copy is the only one left
                return ValueTask.CompletedTask;
            }

            if (request.InterfaceAsString == "org.freedesktop.DBus.Properties" && request.MemberAsString == "Get")
            {
                using var writer = context.CreateReplyWriter("v");
                writer.WriteVariantUInt64(_budgetUsec);
                context.Reply(writer.CreateMessage());
                return ValueTask.CompletedTask;
            }

            context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            _connection?.Dispose();
            if (!_daemon.HasExited) _daemon.Kill();
            await _daemon.WaitForExitAsync(CancellationToken.None);
            _daemon.Dispose();
        }
    }
}
