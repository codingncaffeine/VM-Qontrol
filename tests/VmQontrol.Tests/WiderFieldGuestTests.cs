using System.Diagnostics;
using System.Text;
using VmQontrol.Cli;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 17a (PLAN 4d): what VirtualBox, vmrun, Workstation and Hyper-V have, proven on one real Alpine guest through the
/// vmq commands a person types — run a program in the guest, copy files both ways, pull and plug the network cable,
/// capture traffic to a .pcap, disk speed limits (against an unlimited disk on the same machine), free page reporting.
/// </summary>
public sealed class WiderFieldGuestTests
{
    private static QemuLaunch Build(MachineConfig m) => QemuCommandBuilder.Build(m, new LaunchContext
    {
        Host = RecordedHost.Load(),
        MachineDirectory = "/vms/m",
        Runtime = new RuntimePaths("/run/vmq/m"),
        FirmwareFormat = _ => "raw",
    });

    [Fact]
    public void Limits_and_memory_return_reach_the_command_line_and_quickemu_lines_stay_as_they_were()
    {
        var m = new MachineConfig
        {
            Name = "T",
            Disks = [new DiskConfig { File = "disk0.qcow2", LimitMBps = 50, LimitIops = 400 }, new DiskConfig { File = "disk1.qcow2" }],
        };
        var args = Build(m).Args;
        Assert.Contains(args, a => a.StartsWith("id=SystemDisk,", StringComparison.Ordinal) && a.EndsWith(",throttling.bps-total=50000000,throttling.iops-total=400", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith("id=Disk1,", StringComparison.Ordinal) && !a.Contains("throttling", StringComparison.Ordinal));
        Assert.Contains("virtio-balloon,free-page-reporting=on", args);
        Assert.Contains("virtio-balloon", Build(m with { ImportedFrom = new ImportSource("quickemu", "/x.conf") }).Args);
        Assert.Contains("virtio-balloon", Build(m with { Performance = new PerformanceConfig { PciDevices = ["0000:01:00.0"] } }).Args);
        var changes = SettingsHistory.Describe(m with { Disks = [new DiskConfig { File = "disk0.qcow2" }] }, m);
        Assert.Contains(changes, c => c.StartsWith("Disk speed limits:", StringComparison.Ordinal) && c.Contains("50 MB/s", StringComparison.Ordinal));
        var cable = SettingsHistory.Describe(m, m with { Nics = [new NicConfig { ConnectedAtPowerOn = false }] });
        Assert.Contains("Network cable at power on: connected → left out", cable, StringComparer.Ordinal);
    }

    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_machine_set_to_start_with_its_cable_out_runs_with_it_out()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("cable-start");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "c");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "c", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }], Nics = [new NicConfig { ConnectedAtPowerOn = false }] };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            Assert.True(LiveNetwork.IsUnplugged(running.Paths));
            Assert.Empty(running.Notices);
            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    [Trait("Tier", "Guest")]
    [Fact]
    public async Task Guest_control_cable_capture_and_disk_limits_work_on_a_real_guest()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("widerfield");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        // Two data disks filled on this side: the first limited to 4 MB/s, the second unlimited (the control).
        foreach (var name in (string[])["slow.qcow2", "fast.qcow2"])
            await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-o", "preallocation=full", Path.Combine(dir, name), "64M");
        IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-widerfield\nlocal-hostname: vmq-widerfield\n"u8.ToArray(),
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
            Disks = [new DiskConfig { File = "disk0.qcow2" }, new DiskConfig { File = "slow.qcow2", LimitMBps = 4 }, new DiskConfig { File = "fast.qcow2" }],
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

            (int Code, string Out, string Err) Vmq(params string[] args)
            {
                var o = new StringWriter();
                var e = new StringWriter();
                var code = Commands.Run(args, o, e, profile.Paths, () => HostProbe.Probe());
                return (code, o.ToString(), e.ToString());
            }

            // Run a program: its output and its exit code come back.
            var hello = Vmq("exec", "alpine", "/bin/sh", "-c", "echo hello-$((6*7)); exit 3");
            Assert.Equal(3, hello.Code);
            Assert.Equal("hello-42\n", hello.Out);

            // Copy 3 MB of random bytes in and back out: byte-identical, and the guest computes the same checksum.
            var original = Path.Combine(profile.Root, "random.bin");
            var bytes = new byte[3 * 1024 * 1024 + 17];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(original, bytes, ct);
            var put = Vmq("copy-to", "alpine", original, "/tmp/random.bin");
            Assert.True(put.Code == 0, put.Err);
            var sum = Vmq("exec", "alpine", "/usr/bin/sha256sum", "/tmp/random.bin");
            Assert.StartsWith(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), sum.Out, StringComparison.Ordinal);
            var back = Path.Combine(profile.Root, "back.bin");
            var get = Vmq("copy-from", "alpine", "/tmp/random.bin", back);
            Assert.True(get.Code == 0, get.Err);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(back, ct));
            var missing = Vmq("copy-from", "alpine", "/no/such/file", Path.Combine(profile.Root, "none.bin"));
            Assert.Equal(1, missing.Code);
            Assert.Contains("No such file", missing.Err, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(profile.Root, "none.bin.part")));

            // The cable: the guest's adapter loses its carrier and gets it back.
            string Carrier() => Vmq("exec", "alpine", "/bin/cat", "/sys/class/net/eth0/carrier").Out.Trim();
            Assert.Equal("1", Carrier());
            Assert.Equal(0, Vmq("cable", "alpine", "off").Code);
            Assert.True(LiveNetwork.IsUnplugged(running.Paths));
            Assert.Equal("0", Carrier());
            Assert.Equal(0, Vmq("cable", "alpine", "on").Code);
            Assert.Equal("1", Carrier());
            // The guest asks for its address again when the link comes back; until then it has no route.
            var routed = DateTime.UtcNow.AddSeconds(60);
            while (Vmq("exec", "alpine", "/bin/ping", "-c", "1", "-W", "1", "10.0.2.2").Code != 0)
            {
                Assert.True(DateTime.UtcNow < routed, "the guest never reached its gateway again after the cable was plugged back in");
                await Task.Delay(500, ct);
            }

            // Capture: a ping to the NAT gateway lands in a pcap with the libpcap magic.
            var pcap = Path.Combine(profile.Root, "capture", "alpine.pcap");
            Assert.Equal(0, Vmq("capture", "alpine", pcap).Code);
            var ping = Vmq("exec", "alpine", "/bin/ping", "-c", "3", "10.0.2.2");
            Assert.Equal(0, Vmq("capture", "alpine", "--stop").Code);
            var captured = await File.ReadAllBytesAsync(pcap, ct);
            Assert.True(captured.Length > 24 + 3 * 98, $"capture has only {captured.Length} bytes; ping said {ping.Code}: {ping.Out} {ping.Err}");
            Assert.Equal(0xa1b2c3d4u, BitConverter.ToUInt32(captured, 0));
            Assert.Null(LiveNetwork.CapturePath(running.Paths));

            // Disk limits: 16 MB read straight from the limited disk takes about 4 s; the same from the unlimited one does not.
            TimeSpan Read(string device)
            {
                var watch = Stopwatch.StartNew();
                var r = Vmq("exec", "alpine", "/bin/dd", $"if={device}", "of=/dev/null", "bs=1M", "count=16", "iflag=direct");
                Assert.True(r.Code == 0, r.Err);
                return watch.Elapsed;
            }

            var fast = Read("/dev/vdc");
            var slow = Read("/dev/vdb");
            Assert.True(fast < TimeSpan.FromSeconds(2), $"the unlimited disk took {fast.TotalSeconds:F1} s (the control)");
            Assert.True(slow > TimeSpan.FromSeconds(3), $"the 4 MB/s disk read 16 MB in {slow.TotalSeconds:F1} s");

            // Free page reporting is on in the running machine's balloon device.
            await using var qmp = await QmpClient.ConnectAsync(running.Paths.Control, TimeSpan.FromSeconds(5), ct);
            var devices = await qmp.ExecuteAsync("qom-list", new System.Text.Json.Nodes.JsonObject { ["path"] = "/machine/peripheral-anon" }, ct);
            var found = false;
            foreach (var d in devices!.AsArray())
            {
                if (d?["type"]?.GetValue<string>() is not { } type || !type.Contains("virtio-balloon", StringComparison.Ordinal)) continue;
                var path = "/machine/peripheral-anon/" + d["name"]!.GetValue<string>().Replace("child<", "", StringComparison.Ordinal);
                var on = await qmp.ExecuteAsync("qom-get", new System.Text.Json.Nodes.JsonObject { ["path"] = path, ["property"] = "free-page-reporting" }, ct);
                found |= on?.GetValue<bool>() == true;
            }

            Assert.True(found, "no balloon device with free-page-reporting on");
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }
    }
}
