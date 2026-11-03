using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 5: memory changed while the machine runs, through the balloon, read back from the guest.</summary>
[Trait("Tier", "Guest")]
public sealed class LiveMemoryTests
{
    [Fact]
    public async Task A_running_guest_gives_memory_back_and_takes_it_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("live-memory");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        var m = new MachineConfig
        {
            Name = "alpine",
            MemoryMiB = 512,
            Cpu = new CpuConfig { Cores = 1 },
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
        };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            Assert.Equal(512, await LiveMemory.ActualMiBAsync(watcher, ct));
            // The guest's balloon driver answers once its kernel is up: ask until it has given memory back.
            var deadline = DateTime.UtcNow.AddMinutes(2);
            long? now;
            do
            {
                await LiveMemory.SetAsync(watcher, 256, ct);
                await Task.Delay(3000, ct);
                now = await LiveMemory.ActualMiBAsync(watcher, ct);
            }
            while (now > 264 && DateTime.UtcNow < deadline);
            Assert.InRange(now!.Value, 240, 264);

            await LiveMemory.SetAsync(watcher, 512, ct);
            for (var i = 0; i < 20 && await LiveMemory.ActualMiBAsync(watcher, ct) < 500; i++) await Task.Delay(1000, ct);
            Assert.Equal(512, await LiveMemory.ActualMiBAsync(watcher, ct));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }
    }
}
