using System.Text.RegularExpressions;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Every key combination the machine page's menu offers is one QEMU accepts, read from the menu itself.</summary>
public sealed partial class KeyCombinationTests
{
    [GeneratedRegex("""Command="\{Binding SendKeysCommand\}" CommandParameter="([^"]+)" """)]
    private static partial Regex Offered();

    private static List<string> MenuCombinations()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "VmQontrol.slnx"))) dir = Path.GetDirectoryName(dir)!;
        var xaml = File.ReadAllText(Path.Combine(dir, "src", "VmQontrol.App", "Views", "MachineView.axaml"));
        return Offered().Matches(xaml).Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public void The_menu_offers_the_console_keys_and_names_them_as_printed()
    {
        var keys = MenuCombinations();
        Assert.Contains("ctrl+alt+backspace", keys);
        Assert.Equal(12, keys.Count(k => k.StartsWith("ctrl+alt+f", StringComparison.Ordinal)));
        Assert.Equal("Ctrl+Alt+F2", VmQontrol.App.ViewModels.Text.KeyLabel("ctrl+alt+f2"));
        Assert.Equal("Print Screen", VmQontrol.App.ViewModels.Text.KeyLabel("print"));
    }

    [Trait("Tier", "Integration")]
    [Fact]
    public async Task QEMU_takes_every_combination_the_menu_offers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("keys");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "keys");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "keys", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            foreach (var keys in MenuCombinations())
                await MachineRunner.SendKeysAsync(running, keys.Split('+'), ct); // a key QEMU does not know is refused (QmpException)
            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
