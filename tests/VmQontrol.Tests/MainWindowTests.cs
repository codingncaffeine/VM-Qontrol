using Avalonia.Controls;
using Avalonia.Headless;
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

            // Ctrl+K, type, Enter: the palette runs the command it highlights.
            window.KeyPress(Avalonia.Input.Key.K, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.K, "k");
            Ui.PumpUntil(() => shell.Palette.IsOpen, "the command palette");
            window.KeyTextInput("perf");
            Ui.Pump(0.2);
            var highlighted = shell.Palette.Highlighted?.Title;
            Ui.Capture(window, "phase3", "palette");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Ui.PumpUntil(() => !shell.Palette.IsOpen, "the palette to close");
            var tab = shell.Selected!.Tab;
            window.Close();
            return (problems.IsVisible, problemText, title, lines, stillOff, highlighted, tab);
        });

        Assert.True(seen.IsVisible);
        Assert.Contains("installer disc", seen.problemText, StringComparison.Ordinal);
        Assert.Equal("windows-11 could not start", seen.title);
        Assert.Contains("windows-11.iso", seen.lines, StringComparison.Ordinal);
        Assert.True(seen.stillOff);
        Assert.Equal("Show performance", seen.highlighted);
        Assert.Equal(1, seen.tab);
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

                // The Performance tab: real samples from the running process within a few seconds.
                Ui.Click(window, Named<TabItem>(page, "PerformanceTab"));
                Ui.PumpUntil(() => shell.Selected!.Performance.CpuSeries.Count >= 3, "three performance samples", 15);
                Ui.Capture(window, "phase3", "performance");
                var memory = shell.Selected!.Performance.MemorySeries[^1];
                window.Close();
                return (state, powerOnShown, powerOffShown, memory);
            });
            Assert.Equal("Running", seen.state);
            Assert.True(seen.memory > 10, $"the machine's resident memory read {seen.memory} MB");
            Assert.False(seen.powerOnShown);
            Assert.True(seen.powerOffShown);
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    /// <summary>Alpine's cloud image logs to its serial port: the Console tab shows its boot and login prompt.</summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task The_console_tab_shows_what_the_guest_prints_on_its_serial_port()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", "nocloud_alpine-3.22.4-x86_64-bios-cloudinit-r0.qcow2");
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("console");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Alpine");
        Directory.CreateDirectory(dir);
        await VmQontrol.Core.Runtime.QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        // A minimal seed, so cloud-init finds its data source at once instead of searching for minutes.
        VmQontrol.Core.Media.IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-console-test\n"u8.ToArray(),
            ["user-data"] = "#cloud-config\n"u8.ToArray(),
        });
        var machine = new VmQontrol.Core.Machines.MachineConfig
        {
            Media = [new VmQontrol.Core.Machines.MediaConfig { Role = VmQontrol.Core.Machines.MediaRole.Drivers, File = "seed.iso" }],
            Name = "Alpine",
            MemoryMiB = 512,
            Cpu = new VmQontrol.Core.Machines.CpuConfig { Cores = 1 },
            Firmware = new VmQontrol.Core.Machines.FirmwareConfig { Kind = VmQontrol.Core.Machines.FirmwareKind.Bios },
            Disks = [new VmQontrol.Core.Machines.DiskConfig { File = "disk0.qcow2" }],
        };
        VmQontrol.Core.Machines.MachineStore.Save(machine, Path.Combine(dir, "vm.json"));
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(machine, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, ct);
        try
        {
            var text = await Ui.Run(() =>
            {
                var (window, shell) = Open(profile);
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true }, "the running state");
                Ui.Pump(0.3);
                var page = window.GetVisualDescendants().OfType<MachineView>().Single();
                Ui.Click(window, Named<TabItem>(page, "ConsoleTab"));
                var connectedAt = DateTime.UtcNow;
                try
                {
                    Ui.PumpUntil(() => shell.Selected!.Console.Text.Contains("login:", StringComparison.Ordinal), "the guest's login prompt on the console", 90);
                }
                catch (Xunit.Sdk.XunitException)
                {
                    var tail = shell.Selected!.Console.Text;
                    throw new Xunit.Sdk.XunitException($"No login prompt after 90 s. Connected: {shell.Selected.Console.Connected}; note: {shell.Selected.Console.Note}; last output:\n{tail[Math.Max(0, tail.Length - 1500)..]}");
                }

                TestContext.Current.SendDiagnosticMessage($"login prompt {(DateTime.UtcNow - connectedAt).TotalSeconds:0.0} s after the console tab opened");
                Ui.Capture(window, "phase3", "console");
                var result = shell.Selected!.Console.Text;
                window.Close();
                return result;
            });
            Assert.Contains("login:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\x1B[", text, StringComparison.Ordinal);
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
