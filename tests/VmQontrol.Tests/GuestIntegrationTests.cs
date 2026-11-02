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
}
