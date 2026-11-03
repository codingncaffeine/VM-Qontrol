using System.Diagnostics;
using System.Text.Json;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>D35 item 3 (a): a machine's screen reopens at the size, and full screen, it was left at.</summary>
public sealed class ViewerMemoryTests
{
    private static MachineConfig Machine(int? width = null, int? height = null, bool full = false, int? monitors = null) => new()
    {
        Name = "Web",
        Disks = [new DiskConfig { File = "disk0.qcow2" }],
        Display = new DisplayConfig { Width = width, Height = height, FullScreen = full, Monitors = monitors },
    };

    private static string Remembered(TestProfile profile, int w, int h, bool full)
    {
        var dir = Path.Combine(profile.Root, "Web");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ViewerMemory.FileName), JsonSerializer.Serialize(new ViewerState(w, h, full)));
        return dir;
    }

    [Fact]
    public void The_machines_own_display_settings_win_then_the_remembered_screen_then_the_default()
    {
        using var profile = new TestProfile("viewer-memory");
        var dir = Remembered(profile, 1920, 1080, true);
        Assert.Equal((1920, 1080), ViewerMemory.StartResolution(Machine(), dir));
        Assert.True(ViewerMemory.OpensFullScreen(Machine(), dir));
        Assert.Equal((1024, 768), ViewerMemory.StartResolution(Machine(1024, 768), dir));
        // Several monitors: one remembered size would fit none of them.
        Assert.Equal((1280, 800), ViewerMemory.StartResolution(Machine(monitors: 2), dir));
        Assert.False(ViewerMemory.OpensFullScreen(Machine(monitors: 2), dir));
        Assert.Equal((1280, 800), ViewerMemory.StartResolution(Machine(), Path.Combine(profile.Root, "none")));
        Assert.True(ViewerMemory.OpensFullScreen(Machine(full: true), Path.Combine(profile.Root, "none")));
    }

    [Fact]
    public void QEMU_starts_the_guest_at_the_remembered_resolution()
    {
        using var profile = new TestProfile("viewer-memory-qemu");
        var dir = Remembered(profile, 1600, 900, false);
        var launch = QemuCommandBuilder.Build(Machine(), new LaunchContext
        {
            Host = RecordedHost.Load(), MachineDirectory = dir, Runtime = new RuntimePaths("/run/vmq/m"), FileExists = _ => false, FirmwareFormat = _ => "raw",
        });
        Assert.Contains(launch.Args, a => a.Contains("xres=1600,yres=900", StringComparison.Ordinal));
    }

    /// <summary>The real path with an Alpine guest and a stand-in viewer that closes at once: the guest's resolution (read
    /// independently) is kept, a size matching a monitor counts as full screen, and a viewer closed by the machine shutting down keeps nothing.</summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task Closing_the_viewer_keeps_the_guests_resolution_and_a_shut_machine_keeps_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("viewer-memory-guest");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "web");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        var m = new MachineConfig { Name = "web", MemoryMiB = 512, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Disks = [new DiskConfig { File = "disk0.qcow2" }] };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        var before = ViewerMemory.Monitors;
        try
        {
            var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
            await Task.Delay(TimeSpan.FromSeconds(4), ct); // the firmware has set the screen up
            // The guest's resolution, read independently: what the kept size must equal.
            var probe = Path.Combine(profile.Root, "probe.png");
            await MachineRunner.ScreenshotAsync(running, probe, ct);
            var (w, h) = ViewerMemory.Size(probe)!.Value;
            ViewerMemory.Monitors = [(w + 100, h + 100)];
            await ViewerMemory.RememberWhenClosedAsync(Process.Start("true")!, running, m);
            Assert.Equal(new ViewerState(w, h, false), ViewerMemory.Read(dir));
            ViewerMemory.Monitors = [(w, h)];
            await ViewerMemory.RememberWhenClosedAsync(Process.Start("true")!, running, m);
            Assert.Equal(new ViewerState(w, h, true), ViewerMemory.Read(dir));

            await MachineRunner.PowerOffAsync(running, ct);
            File.Delete(Path.Combine(dir, ViewerMemory.FileName));
            await ViewerMemory.RememberWhenClosedAsync(Process.Start("true")!, running, m);
            Assert.Null(ViewerMemory.Read(dir));
        }
        finally
        {
            ViewerMemory.Monitors = before;
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }
    }
}
