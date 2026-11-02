using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;

namespace VmQontrol.Tests;

public sealed class MainWindowTests
{
    private static (MainWindow Window, ShellViewModel Shell) Open(TestProfile profile)
    {
        var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
        var window = new MainWindow { DataContext = shell, Width = 1320, Height = 860 };
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        window.Show();
        _ = shell.StartAsync();
        return (window, shell);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task A_machine_that_cannot_start_says_why_from_its_page_and_from_power_on()
    {
        using var profile = new TestProfile("window");
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(Path.Combine(lib, "windows-11"));
        foreach (var n in new[] { "windows-11", "linux" })
            File.Copy(Path.Combine(RecordedHost.FixtureDirectory, n + ".conf"), Path.Combine(lib, n + ".conf"));

        var seen = await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Machines.Count == 2 && shell.Welcome.Checked, "the library and the host check");
            Ui.Capture(window, "phase3", "welcome");

            // The user clicks the Windows machine in the library.
            var list = Named<ListBox>(window, "Library");
            var item = list.GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as MachineViewModel)?.Name == "windows-11");
            Ui.Click(window, item);
            Ui.PumpUntil(() => shell.Selected?.Name == "windows-11", "the machine page");
            Ui.Pump(0.3);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            var problems = Named<Border>(page, "Problems");
            var problemText = string.Join("\n", problems.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Ui.Capture(window, "phase3", "machine-with-problems");

            // Power on: refused before QEMU runs, with the reason in a notice.
            Ui.Click(window, Named<Button>(page, "PowerOn"));
            Ui.PumpUntil(() => shell.Notice is not null, "the refusal notice");
            Ui.Pump(0.2);
            var title = Named<TextBlock>(window, "NoticeTitle").Text;
            var lines = string.Join("\n", shell.Notice!.Lines);
            Ui.Capture(window, "phase3", "refusal-notice");
            Ui.Click(window, Named<Button>(window, "NoticeDismiss"));
            Ui.PumpUntil(() => shell.Notice is null, "the notice to close");
            var stillOff = shell.Selected!.IsOff;
            window.Close();
            return (problems.IsVisible, problemText, title, lines, stillOff);
        });

        Assert.True(seen.IsVisible);
        Assert.Contains("installer disc", seen.problemText, StringComparison.Ordinal);
        Assert.Equal("windows-11 could not start", seen.title);
        Assert.Contains("windows-11.iso", seen.lines, StringComparison.Ordinal);
        Assert.True(seen.stillOff);
    }

    /// <summary>A real machine, started headless and silent before the window opens: its page finds it running and shows its screen.</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_running_machine_shows_its_state_and_live_screen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("live");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Live");
        Directory.CreateDirectory(dir);
        var machine = new VmQontrol.Core.Machines.MachineConfig
        {
            Name = "Live",
            MemoryMiB = 256,
            Cpu = new VmQontrol.Core.Machines.CpuConfig { Cores = 1 },
            Firmware = new VmQontrol.Core.Machines.FirmwareConfig { Kind = VmQontrol.Core.Machines.FirmwareKind.Bios },
            Disks = [new VmQontrol.Core.Machines.DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
        };
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        VmQontrol.Core.Machines.MachineStore.Save(machine, Path.Combine(dir, "vm.json"));
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(machine, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, ct);
        try
        {
            var seen = await Ui.Run(() =>
            {
                var (window, shell) = Open(profile);
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                var item = Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First();
                Ui.Click(window, item);
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true, HasScreen: true }, "the running state and the first screen", 20);
                Ui.Pump(0.3);
                var page = window.GetVisualDescendants().OfType<MachineView>().Single();
                var state = Named<TextBlock>(page, "StateText").Text;
                var powerOnShown = Named<Button>(page, "PowerOn").IsVisible;
                var powerOffShown = Named<Button>(page, "PowerOff").IsVisible;
                Ui.Capture(window, "phase3", "machine-running");
                window.Close();
                return (state, powerOnShown, powerOffShown);
            });
            Assert.Equal("Running", seen.state);
            Assert.False(seen.powerOnShown);
            Assert.True(seen.powerOffShown);
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
