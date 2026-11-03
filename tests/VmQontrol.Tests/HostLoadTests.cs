using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

/// <summary>Parity audit (s9): this computer's processor and memory graphs (virt-manager's host graphs) on the welcome page.</summary>
public sealed class HostLoadTests
{
    [Fact]
    public void The_share_of_busy_time_comes_from_two_readings()
    {
        var root = Path.Combine(TestProfile.ScratchRoot, "host-load-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            // user nice system idle iowait irq softirq steal
            File.WriteAllText(Path.Combine(root, "stat"), "cpu  100 0 100 700 100 0 0 0 0 0\ncpu0 1 2 3 4\n");
            File.WriteAllText(Path.Combine(root, "meminfo"), "MemTotal:       16000000 kB\nMemFree:  1 kB\nMemAvailable:   12000000 kB\n");
            var load = new HostLoad(root);
            Assert.Null(load.Sample([]));
            // 400 more jiffies, 100 of them idle and 100 iowait: half busy.
            File.WriteAllText(Path.Combine(root, "stat"), "cpu  200 0 200 800 200 0 0 0 0 0\n");
            var s = load.Sample([]);
            Assert.NotNull(s);
            Assert.Equal(50.0, s.CpuPercent, 3);
            Assert.Equal(16000000L * 1024, s.MemoryTotalBytes);
            Assert.Equal(4000000L * 1024, s.MemoryUsedBytes);
            Assert.Equal(0, s.MachinesBytes);
            Assert.Equal((1400L - 800 - 200, 1400L), (HostLoad.ParseCpu("cpu  200 0 200 800 200 0 0 0\n")!.Value.Busy, HostLoad.ParseCpu("cpu  200 0 200 800 200 0 0 0\n")!.Value.Total));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_welcome_page_draws_this_computers_last_minute()
    {
        using var profile = new TestProfile("host-load");
        Directory.CreateDirectory(profile.Paths.DefaultLibrary);
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Welcome.HostCpuSeries.Count >= 3, "three seconds of this computer", 20);
            var cpu = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "HostCpu").Text;
            var memory = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "HostMemory").Text;
            var charts = window.GetVisualDescendants().OfType<VmQontrol.App.Controls.LiveChart>().Count(c => c.IsEffectivelyVisible && c.Bounds.Height > 50);
            Ui.Capture(window, "s9", "welcome-host-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s9", "welcome-host-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
            return (cpu, memory, charts);
        });

        Assert.EndsWith("%", seen.cpu, StringComparison.Ordinal);
        Assert.Contains(" of ", seen.memory, StringComparison.Ordinal);
        Assert.Equal(2, seen.charts);
    }
}
