using System.Text;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 5: processors added while the machine runs (CPU hot-plug), opt-in so quickemu's command stays exact.</summary>
public sealed class LiveProcessorsTests
{
    private static string Smp(MachineConfig m, HostCapabilities host)
    {
        var args = QemuCommandBuilder.Build(m, new LaunchContext
        {
            Host = host,
            MachineDirectory = "/vms/m",
            Runtime = new RuntimePaths("/run/vmq/m"),
            FileExists = _ => false,
            FirmwareFormat = _ => "raw",
        }).Args.ToList();
        return args[args.IndexOf("-smp") + 1];
    }

    private static MachineConfig Linux(int cores, int? most) => new() { Name = "T", Cpu = new CpuConfig { Cores = cores, MaxCores = most }, Disks = [new DiskConfig { File = "disk0.qcow2" }] };

    [Fact]
    public void Off_keeps_quickemus_line_and_on_leaves_room_in_whole_cores()
    {
        var smt = RecordedHost.Load() with { SmtOn = true, Sockets = 1, LogicalCpus = 32 };
        Assert.Equal("cores=2,threads=2,sockets=1", Smp(Linux(4, null), smt));
        Assert.Equal("cpus=4,maxcpus=8,cores=4,threads=2,sockets=1", Smp(Linux(4, 8), smt));
        // Seven rounds up to a whole core (two threads).
        Assert.Equal("cpus=4,maxcpus=8,cores=4,threads=2,sockets=1", Smp(Linux(4, 7), smt));
        // No more than it has: nothing to add, so quickemu's line.
        Assert.Equal("cores=2,threads=2,sockets=1", Smp(Linux(4, 4), smt));
        // One processor has one thread: slots of one.
        Assert.Equal("cpus=1,maxcpus=3,cores=3,threads=1,sockets=1", Smp(Linux(1, 3), smt));
        var plain = smt with { SmtOn = false };
        Assert.Equal("cpus=2,maxcpus=6,cores=6,threads=1,sockets=1", Smp(Linux(2, 6), plain));
    }

    [Fact]
    public void The_change_is_in_the_history()
    {
        var before = Linux(2, null);
        var after = before with { Cpu = before.Cpu with { MaxCores = 4 } };
        Assert.Contains("Processors added while running, up to: off → 4", SettingsHistory.Describe(before, after), StringComparer.Ordinal);
    }

    [Trait("Tier", "Guest")]
    [Fact]
    public async Task A_running_guest_gets_processors_it_did_not_start_with()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("live-cpus");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-cpus-test\nlocal-hostname: vmq-cpus\n"u8.ToArray(),
            ["user-data"] = Encoding.UTF8.GetBytes("""
                #cloud-config
                packages: [qemu-guest-agent]
                runcmd:
                  - rc-update add qemu-guest-agent default
                  - rc-service qemu-guest-agent start
                """),
        });
        var m = new MachineConfig
        {
            Name = "alpine",
            MemoryMiB = 512,
            Cpu = new CpuConfig { Cores = 1, MaxCores = 3 },
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
        };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            Assert.Equal(new ProcessorSlots(1, 3, 1), await LiveProcessors.SlotsAsync(watcher, ct));
            var agent = new GuestAgentClient(running.Paths.Agent);
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (!await agent.PingAsync(TimeSpan.FromSeconds(2), ct))
            {
                Assert.True(DateTime.UtcNow < deadline, "the guest agent never answered (cloud-init installs it from the network at first boot)");
                await Task.Delay(2000, ct);
            }

            async Task<string> Read(string path) => Encoding.ASCII.GetString(await agent.ReadFileAsync(path, TimeSpan.FromSeconds(3), ct) ?? []).Trim();
            // The control: before, the guest knows of one processor.
            Assert.Equal("0", await Read("/sys/devices/system/cpu/present"));

            Assert.Equal(3, await LiveProcessors.AddAsync(watcher, 3, ct));
            Assert.Equal(new ProcessorSlots(3, 3, 1), await LiveProcessors.SlotsAsync(watcher, ct));
            var present = "";
            for (var i = 0; i < 20 && (present = await Read("/sys/devices/system/cpu/present")) != "0-2"; i++) await Task.Delay(500, ct);
            Assert.Equal("0-2", present);
            // Alpine has no rule that switches them on: the control shows them offline, then the agent switches them on.
            Assert.Equal("0", await Read("/sys/devices/system/cpu/online"));
            Assert.Equal(3, await agent.OnlineAllProcessorsAsync(TimeSpan.FromSeconds(5), ct));
            Assert.Equal("0-2", await Read("/sys/devices/system/cpu/online"));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }
    }
}
