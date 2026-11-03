using System.Diagnostics;
using System.Text;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 5 audit: the VirtIO socket (vsock) virt-manager offers.</summary>
public sealed class VsockTests
{
    private static QemuLaunch Build(MachineConfig m, bool hostHasIt) => QemuCommandBuilder.Build(m, new LaunchContext
    {
        Host = RecordedHost.Load(),
        MachineDirectory = "/vms/m",
        Runtime = new RuntimePaths("/run/vmq/m"),
        FileExists = f => hostHasIt && f == "/dev/vhost-vsock",
        FirmwareFormat = _ => "raw",
    });

    private static MachineConfig Linux(bool vsock) => new() { Id = "0123456789abcdef", Name = "T", Devices = new DevicesConfig { Vsock = vsock }, Disks = [new DiskConfig { File = "disk0.qcow2" }] };

    [Fact]
    public void On_adds_the_device_at_the_machines_own_address_and_says_so_when_the_host_has_none()
    {
        var cid = QemuCommandBuilder.VsockCid("0123456789abcdef");
        Assert.InRange(cid, 3u, (uint)int.MaxValue);
        Assert.Equal(cid, QemuCommandBuilder.VsockCid("0123456789abcdef"));
        Assert.NotEqual(cid, QemuCommandBuilder.VsockCid("fedcba9876543210"));
        Assert.Contains($"vhost-vsock-pci,guest-cid={cid}", Build(Linux(true), true).Args);
        Assert.DoesNotContain(Build(Linux(false), true).Args, a => a.StartsWith("vhost-vsock", StringComparison.Ordinal));
        var missing = Build(Linux(true), false);
        Assert.DoesNotContain(missing.Args, a => a.StartsWith("vhost-vsock", StringComparison.Ordinal));
        Assert.Contains(missing.Warnings, w => w.Contains("/dev/vhost-vsock", StringComparison.Ordinal));
        Assert.Contains("VirtIO socket (vsock): off → on", SettingsHistory.Describe(Linux(false), Linux(true)), StringComparer.Ordinal);
        Assert.Equal($"ssh user@vsock/{cid}", new GuestConfig().SshVsockCommand(cid));
    }

    [Trait("Tier", "Guest")]
    [Fact]
    public async Task This_computer_reaches_the_guest_over_vsock()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        Assert.SkipWhen(!File.Exists("/dev/vhost-vsock"), "This computer has no /dev/vhost-vsock.");
        Assert.SkipWhen(!File.Exists("/usr/bin/socat"), "socat is not installed.");
        using var profile = new TestProfile("vsock");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        // The guest answers on vsock port 5000 once its package is in.
        IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-vsock\nlocal-hostname: vmq-vsock\n"u8.ToArray(),
            ["user-data"] = Encoding.UTF8.GetBytes("""
                #cloud-config
                packages: [socat]
                runcmd:
                  - modprobe vmw_vsock_virtio_transport || true
                  - setsid socat VSOCK-LISTEN:5000,fork SYSTEM:'echo vsock-hello-from-guest' >/dev/null 2>&1 < /dev/null &
                """),
        });
        var m = new MachineConfig
        {
            Name = "alpine",
            MemoryMiB = 512,
            Cpu = new CpuConfig { Cores = 1 },
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Devices = new DevicesConfig { Vsock = true },
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
        };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            var cid = QemuCommandBuilder.VsockCid(m.Id);
            var answer = "";
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (!answer.Contains("vsock-hello-from-guest", StringComparison.Ordinal))
            {
                Assert.True(DateTime.UtcNow < deadline, $"nothing answered on vsock {cid}:5000 (last: {answer})");
                await Task.Delay(2000, ct);
                var start = new ProcessStartInfo("/usr/bin/socat") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                foreach (var a in (string[])["-T", "3", "-", $"VSOCK-CONNECT:{cid}:5000"]) start.ArgumentList.Add(a);
                using var p = Process.Start(start)!;
                p.StandardInput.Close();
                answer = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
            }
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }
    }
}
