using System.Text;
using VmQontrol.Cli;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// A real guest: Alpine's cloud image (tools/fetch-test-image.sh) through a throwaway overlay, with a cloud-init seed
/// disc made by <see cref="IsoWriter"/> that installs and starts the QEMU guest agent. Headless and silent, 512 MB.
/// Proves the agent path end to end: its addresses through vmq status, and a clean shutdown asked of the guest.
/// </summary>
[Trait("Tier", "Guest")]
public sealed class GuestIntegrationTests
{
    private const string Image = "nocloud_alpine-3.22.4-x86_64-bios-cloudinit-r0.qcow2";

    [Fact]
    public async Task The_guest_agent_reports_the_guest_address_and_shuts_the_guest_down()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("guest");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-guest-test\nlocal-hostname: vmq-guest\n"u8.ToArray(),
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
            Cpu = new CpuConfig { Cores = 1 },
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
        };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));

        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            var agent = new GuestAgentClient(running.Paths.Agent);
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (!await agent.PingAsync(TimeSpan.FromSeconds(2), ct))
            {
                Assert.True(DateTime.UtcNow < deadline, "the guest agent never answered (cloud-init installs it from the network at first boot)");
                await Task.Delay(2000, ct);
            }

            var output = new StringWriter();
            Assert.Equal(0, Commands.Run(["status", "alpine"], output, new StringWriter(), profile.Paths, () => HostProbe.Probe()));
            Assert.Contains("10.0.2.15", output.ToString(), StringComparison.Ordinal);

            Assert.True(await agent.ShutdownAsync(TimeSpan.FromSeconds(2), ct));
            Assert.True(await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(60), ct), "the guest did not shut down when asked through its agent");
        }
        finally
        {
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }
    }

    /// <summary>A shared folder, both ways: the guest reads a file the host put there and writes one back, then powers off.</summary>
    [Fact]
    public async Task A_shared_folder_carries_files_both_ways()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        Assert.SkipWhen(!File.Exists("/usr/lib/virtiofsd") && !File.Exists("/usr/libexec/virtiofsd"), "virtiofsd is not installed");
        using var profile = new TestProfile("share");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        var shared = Path.Combine(profile.Root, "shared");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "hello.txt"), "hi from the host\n");
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-share-test\n"u8.ToArray(),
            ["user-data"] = Encoding.UTF8.GetBytes("""
                #cloud-config
                runcmd:
                  - mkdir -p /mnt/share
                  - mount -t virtiofs hostshare /mnt/share
                  - cp /mnt/share/hello.txt /mnt/share/back.txt
                  - echo "written by the guest" >> /mnt/share/back.txt
                  - sync
                  - poweroff
                """),
        });
        var m = new MachineConfig
        {
            Name = "alpine",
            MemoryMiB = 512,
            Cpu = new CpuConfig { Cores = 1 },
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
            Shares = [new ShareConfig { Path = shared, Tag = "hostshare" }],
        };
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            Assert.True(await MachineRunner.WaitForExitAsync(running, TimeSpan.FromMinutes(3), ct), "the guest did not finish and power off");
        }
        finally
        {
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }

        var back = Path.Combine(shared, "back.txt");
        Assert.True(File.Exists(back), "the guest wrote nothing into the shared folder");
        Assert.Equal("hi from the host\nwritten by the guest\n", File.ReadAllText(back));
    }

    /// <summary>Two machines on one private segment, nothing else: the second reaches the first by its fixed address.</summary>
    [Fact]
    public async Task Two_machines_on_a_private_segment_reach_each_other()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("segment");
        var shared = Path.Combine(profile.Root, "results");
        Directory.CreateDirectory(shared);
        var host = HostProbe.Probe();

        async Task<(MachineConfig, string)> MakeAsync(string name, string script, bool share)
        {
            var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
            Directory.CreateDirectory(dir);
            await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
            IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
            {
                ["meta-data"] = Encoding.UTF8.GetBytes($"instance-id: vmq-{name}\n"),
                ["user-data"] = Encoding.UTF8.GetBytes("#cloud-config\nruncmd:\n" + script),
                ["network-config"] = "version: 2\nethernets: {}\n"u8.ToArray(),
            });
            var m = new MachineConfig
            {
                Name = name,
                MemoryMiB = 512,
                Cpu = new CpuConfig { Cores = 1 },
                Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
                Disks = [new DiskConfig { File = "disk0.qcow2" }],
                Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
                Nics = [new NicConfig { Mode = NetMode.Segment, Segment = "lab" }],
                Shares = share ? [new ShareConfig { Path = shared, Tag = "results" }] : [],
            };
            return (m, dir);
        }

        var (a, aDir) = await MakeAsync("seg-a", "  - ip link set eth0 up\n  - ip addr add 10.10.0.1/24 dev eth0\n  - sleep 120\n  - poweroff\n", false);
        var (b, bDir) = await MakeAsync("seg-b", "  - ip link set eth0 up\n  - ip addr add 10.10.0.2/24 dev eth0\n  - mkdir -p /mnt/r\n  - mount -t virtiofs results /mnt/r\n  - ping -c 3 -w 90 10.10.0.1 > /mnt/r/ping.txt 2>&1; echo \"exit $?\" >> /mnt/r/ping.txt\n  - sync\n  - poweroff\n", true);
        var ra = await MachineRunner.StartAsync(a, aDir, profile.Paths, host, new StartOptions { Headless = true }, ct);
        var rb = await MachineRunner.StartAsync(b, bDir, profile.Paths, host, new StartOptions { Headless = true }, ct);
        try
        {
            Assert.True(await MachineRunner.WaitForExitAsync(rb, TimeSpan.FromMinutes(4), ct), "the second machine did not finish");
        }
        finally
        {
            foreach (var (m, r) in new[] { (a, ra), (b, rb) })
                if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }

        Assert.NotEqual(a.Id, b.Id);
        var result = File.ReadAllText(Path.Combine(shared, "ping.txt"));
        Assert.Contains("exit 0", result, StringComparison.Ordinal);
        Assert.Contains("3 packets received", result, StringComparison.Ordinal);
    }
}
