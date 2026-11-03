using System.Text;
using VmQontrol.Cli;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Runtime;
using VmQontrol.Core.Snapshots;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 17b: changes discarded at power off (Parallels' rollback mode), proven on a real Alpine guest. A file written
/// in a Discard session is gone at the next power on; one written in a session kept with vmq keep-changes is there;
/// the real disk is not written while a session runs. Control: in Keep mode the guest's first-boot install stays.
/// </summary>
public sealed class DiscardSessionTests
{
    [Fact]
    public async Task Snapshots_are_refused_while_changes_are_not_kept()
    {
        using var profile = new TestProfile("discard-snap");
        var m = new MachineConfig { Name = "d", OnPowerOff = DiscardMode.Discard, Disks = [new DiskConfig { File = "disk0.qcow2" }] };
        var store = new SnapshotStore(m, profile.Root, profile.Root);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.TakeAsync("x", "", null, TestContext.Current.CancellationToken));
        Assert.Contains("Changes at power off", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Changes at power off: kept → discarded", SettingsHistory.Describe(m with { OnPowerOff = DiscardMode.Keep }, m), StringComparer.Ordinal);
    }

    [Trait("Tier", "Guest")]
    [Fact]
    public async Task A_discarded_session_leaves_no_trace_and_a_kept_one_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("discard");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", disk);
        IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-discard\nlocal-hostname: vmq-discard\n"u8.ToArray(),
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

        (int Code, string Out, string Err) Vmq(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var code = Commands.Run(args, o, e, profile.Paths, () => HostProbe.Probe());
            return (code, o.ToString(), e.ToString());
        }

        async Task<RunningMachine> BootAsync(DiscardMode mode)
        {
            m = m with { OnPowerOff = mode };
            MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
            var r = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
            var agent = new GuestAgentClient(r.Paths.Agent);
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (!await agent.PingAsync(TimeSpan.FromSeconds(2), ct))
            {
                Assert.True(DateTime.UtcNow < deadline, "the guest agent never answered");
                await Task.Delay(2000, ct);
            }

            return r;
        }

        int Exists(string file) => Vmq("exec", "alpine", "/bin/sh", "-c", $"test -e {file}").Code;

        RunningMachine? running = null;
        try
        {
            // Keep (the control): the guest's first boot installs its agent into the disk.
            running = await BootAsync(DiscardMode.Keep);
            Assert.False(DiscardSession.Exists(dir));
            Assert.True((Vmq("exec", "alpine", "/bin/sh", "-c", "echo kept > /root/control && sync").Code) == (0), "step 1: Vmq('exec', 'alpine', '/bin/sh', '-c', 'echo kept > /root/control && sync').Code should be 0");
            Assert.True((Vmq("poweroff", "alpine").Code) == (0), "step 2: Vmq('poweroff', 'alpine').Code should be 0");
            running = null;

            // Discard: the session writes to its overlay; the real disk does not change while it runs.
            running = await BootAsync(DiscardMode.Discard);
            Assert.True(DiscardSession.Exists(dir));
            Assert.True((Exists("/root/control")) == (0), "step 3: Exists('/root/control') should be 0");
            var before = await File.ReadAllBytesAsync(disk, ct);
            Assert.True((Vmq("exec", "alpine", "/bin/sh", "-c", "dd if=/dev/urandom of=/root/gone bs=1M count=8 && sync").Code) == (0), "step 4: Vmq('exec', 'alpine', '/bin/sh', '-c', 'dd if=/dev/urandom of=/root/gone bs=1M count=8 && sync').Code should be 0");
            Assert.True((await File.ReadAllBytesAsync(disk, ct)).AsSpan().SequenceEqual(before), "step 5: the real disk changed while the session ran");
            var off = Vmq("poweroff", "alpine");
            Assert.Contains("changes were discarded", off.Out, StringComparison.Ordinal);
            running = null;
            Assert.False(DiscardSession.Exists(dir));

            // The next session does not see it; this one is kept by hand.
            running = await BootAsync(DiscardMode.Ask);
            Assert.True((Exists("/root/gone")) == (1), "step 6: Exists('/root/gone') should be 1");
            Assert.True((Exists("/root/control")) == (0), "step 7: Exists('/root/control') should be 0");
            Assert.True((Vmq("exec", "alpine", "/bin/sh", "-c", "echo yes > /root/saved && sync").Code) == (0), "step 8: Vmq('exec', 'alpine', '/bin/sh', '-c', 'echo yes > /root/saved && sync').Code should be 0");
            Assert.Contains("keep-changes", Vmq("poweroff", "alpine").Out, StringComparison.Ordinal);
            running = null;
            Assert.True(DiscardSession.Exists(dir));
            Assert.True((Vmq("keep-changes", "alpine").Code) == (0), "step 9: Vmq('keep-changes', 'alpine').Code should be 0");
            Assert.False(DiscardSession.Exists(dir));

            running = await BootAsync(DiscardMode.Keep);
            Assert.True((Exists("/root/saved")) == (0), "step 10: Exists('/root/saved') should be 0");
            Assert.True((Exists("/root/gone")) == (1), "step 11: Exists('/root/gone') should be 1");
        }
        finally
        {
            if (running is not null) await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }
    }
}
