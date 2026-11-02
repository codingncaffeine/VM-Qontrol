using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// The virtual network editor, walked as a person walks it: Welcome page, Virtual networks, Open. It lists the segment
/// two machines share with the address each was given (named by machine), refuses a public subnet, and saves a chosen
/// one and a NAT subnet into the app's settings, which the next command line carries.
/// </summary>
public sealed class NetworksEditorTests
{
    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task The_network_editor_shows_segment_addresses_and_saves_subnets()
    {
        using var profile = new TestProfile("networks");
        var ids = new List<string>();
        foreach (var name in new[] { "Web", "Database" })
        {
            var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
            Directory.CreateDirectory(dir);
            var m = new MachineConfig { Name = name, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }], Nics = [new NicConfig { Mode = NetMode.Segment, Segment = "lab" }] };
            ids.Add(m.Id);
            MachineStore.Save(m, Path.Combine(dir, "vm.json"));
        }

        var subnet = SegmentSubnet.ForName("lab");
        var leases = SegmentServer.LeaseFile(profile.Paths, "lab");
        Directory.CreateDirectory(Path.GetDirectoryName(leases)!);
        File.WriteAllText(leases, JsonSerializer.Serialize(new[]
        {
            new { mac = QemuCommandBuilder.MacFor(ids[0]), address = subnet.Host(23).ToString(), hostName = "web", seen = DateTimeOffset.Now },
            new { mac = QemuCommandBuilder.MacFor(ids[1]), address = subnet.Host(11).ToString(), hostName = "db", seen = DateTimeOffset.Now },
        }));

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 2, "the library");
            Ui.Pump(0.3);
            Ui.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "OpenNetworks"));
            Ui.PumpUntil(() => shell.Networks is { Segments.Count: 1 }, "the network editor");
            Ui.Pump(0.4);
            var editor = shell.Networks!;
            var row = editor.Segments[0];
            var leaseLines = row.Leases.Select(l => $"{l.Address} {l.Who}").ToList();
            Ui.Capture(window, "phase8", "networks-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "phase8", "networks-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            row.Subnet = "8.8.8.0/24";
            Ui.Pump(0.2);
            var refusedShown = window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.StartsWith("A private range", StringComparison.Ordinal) == true);
            Ui.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "NetworksSave"));
            var refusedNote = editor.Note;
            row.Subnet = "192.168.77.0/24";
            editor.NatSubnet = "10.0.5.0/24";
            Ui.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "NetworksSave"));
            Ui.Pump(0.5);
            window.Close();
            return (leaseLines, refusedShown, refusedNote);
        });

        Assert.Equal([$"{subnet.Host(11)} Database", $"{subnet.Host(23)} Web"], seen.leaseLines); // in address order, by machine name
        Assert.True(seen.refusedShown, "a public subnet is marked");
        Assert.Equal("Fix the subnets marked in red first.", seen.refusedNote);
        for (var i = 0; i < 50 && !(File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains("192.168.77.0/24")); i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        var saved = AppSettings.Load(profile.Paths);
        Assert.Equal("192.168.77.0/24", Assert.Single(saved.Segments).Subnet);
        Assert.Equal("10.0.5.0/24", saved.NatSubnet);
    }

    [Fact]
    public void A_NAT_subnet_reaches_the_command_line_and_none_keeps_QEMUs_own()
    {
        var m = new MachineConfig { Name = "n", Disks = [new DiskConfig { File = "d.qcow2" }] };
        var ctx = new LaunchContext { Host = RecordedHost.Load(), MachineDirectory = "/m", Runtime = new RuntimePaths("/r"), Headless = true };
        Assert.DoesNotContain(QemuCommandBuilder.Build(m, ctx).Args, a => a.Contains("net=", StringComparison.Ordinal) && a.StartsWith("user,", StringComparison.Ordinal));
        Assert.Contains(QemuCommandBuilder.Build(m, ctx with { NatSubnet = "10.0.5.0/24" }).Args, a => a.StartsWith("user,", StringComparison.Ordinal) && a.Contains(",net=10.0.5.0/24", StringComparison.Ordinal));
    }
}

/// <summary>Which terminal runs the SSH command, and how each is told to run one (none is started here).</summary>
public sealed class TerminalLauncherTests
{
    private static readonly string[] Ssh = ["ssh", "-p", "22220", "me@localhost"];

    [Theory]
    [InlineData("xdg-terminal-exec,konsole", null, "/usr/bin/xdg-terminal-exec", "ssh -p 22220 me@localhost")]
    [InlineData("konsole,xterm", null, "/usr/bin/konsole", "-e ssh -p 22220 me@localhost")]
    [InlineData("gnome-terminal", null, "/usr/bin/gnome-terminal", "-- ssh -p 22220 me@localhost")]
    [InlineData("wezterm", null, "/usr/bin/wezterm", "start -- ssh -p 22220 me@localhost")]
    [InlineData("kitty,xterm", "xterm", "/usr/bin/xterm", "-e ssh -p 22220 me@localhost")]
    [InlineData("myterm,konsole", "myterm", "/usr/bin/myterm", "-e ssh -p 22220 me@localhost")]
    public void The_terminal_and_its_way_of_running_a_command(string installed, string? variable, string program, string args)
    {
        var have = installed.Split(',');
        var launch = VmQontrol.App.Platform.TerminalLauncher.For(Ssh, p => have.Contains(p) ? "/usr/bin/" + p : null, variable);
        Assert.NotNull(launch);
        Assert.Equal(program, launch.Value.Program);
        Assert.Equal(args, string.Join(' ', launch.Value.Args));
    }

    [Fact]
    public void No_terminal_is_said_plainly() =>
        Assert.Null(VmQontrol.App.Platform.TerminalLauncher.For(Ssh, _ => null, null));
}
