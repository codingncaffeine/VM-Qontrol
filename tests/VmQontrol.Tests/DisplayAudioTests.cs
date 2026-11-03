using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Tests;

public sealed class DisplayAudioTests
{
    private static IReadOnlyList<string> Args(MachineConfig m, out IReadOnlyList<string> warnings)
    {
        var launch = QemuCommandBuilder.Build(m, new LaunchContext { Host = RecordedHost.Load(), MachineDirectory = "/m", Runtime = new RuntimePaths("/r") });
        warnings = launch.Warnings;
        return launch.Args;
    }

    [Theory]
    [InlineData("linux", "max_outputs=2")]
    [InlineData("windows", "max_outputs=2")]
    public void Two_monitors_give_the_graphics_two_outputs(string os, string expected)
    {
        var m = new MachineConfig { Name = "m", Guest = new GuestConfig { Os = os }, Disks = [new DiskConfig { File = "d.qcow2" }], Display = new DisplayConfig { Monitors = 2 } };
        Assert.Contains(Args(m, out _), a => a.Contains(expected, StringComparison.Ordinal));
        m.Display.Monitors = null;
        Assert.DoesNotContain(Args(m, out _), a => a.Contains("max_outputs", StringComparison.Ordinal));
    }

    [Fact]
    public void Graphics_with_one_output_say_so_instead()
    {
        var m = new MachineConfig { Name = "m", Disks = [new DiskConfig { File = "d.qcow2" }], Display = new DisplayConfig { Monitors = 3, Device = "VGA" } };
        Assert.DoesNotContain(Args(m, out var warnings), a => a.Contains("max_outputs", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("more than one monitor", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_the_microphone_the_sound_card_has_no_input()
    {
        var m = new MachineConfig { Name = "m", Disks = [new DiskConfig { File = "d.qcow2" }] };
        Assert.Contains(Args(m, out _), a => a.StartsWith("hda-micro,", StringComparison.Ordinal));
        m.Audio.Microphone = false;
        var args = Args(m, out _);
        Assert.Contains(args, a => a.StartsWith("hda-output,", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("hda-micro", StringComparison.Ordinal));
    }
}

public sealed class DisplayAudioIntegrationTests
{
    /// <summary>QEMU starts with two monitors and the output-only sound card, and reports both devices.</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task QEMU_starts_with_two_monitors_and_no_microphone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("heads");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "heads");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig
        {
            Name = "heads", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Display = new DisplayConfig { Monitors = 2 }, Audio = new AudioConfig { Microphone = false },
        };
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(m, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, ct);
        try
        {
            await using var qmp = await VmQontrol.Core.Runtime.MachineRunner.ControlAsync(running, ct);
            var tree = (await qmp.ExecuteAsync("qom-list-types", new System.Text.Json.Nodes.JsonObject { ["implements"] = "device" }, ct))!.ToJsonString();
            Assert.Contains("hda-output", tree, StringComparison.Ordinal);
            var cmdline = await File.ReadAllTextAsync($"/proc/{running.Pid}/cmdline", ct);
            Assert.Contains("max_outputs=2", cmdline, StringComparison.Ordinal);
            Assert.Contains("hda-output", cmdline, StringComparison.Ordinal);
            Assert.Equal("running", (await qmp.ExecuteAsync("query-status", ct: ct))?["status"]?.GetValue<string>());
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
