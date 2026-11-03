using System.Globalization;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// The "Installing for you" card, walked the way a person meets it: pick the machine in the library and read what the
/// card says before power on, part way (off and running), and once the guest said it finished; "Got it" closes it for good.
/// </summary>
public sealed class InstallCardTests
{
    private static void Make(TestProfile profile, string name, string? signal)
    {
        var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig
        {
            Name = name,
            Guest = new GuestConfig { Os = "windows", Release = "11" },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            EasyInstall = new EasyInstallConfig { System = "Windows 11 Pro", UserName = "Ann", Lean = true },
        }, Path.Combine(dir, MachineStore.FileName));
        if (signal is not null) File.WriteAllText(Path.Combine(dir, InstallSignal.FileName), signal);
    }

    private static string Started(int minutesAgo) => "started " + DateTimeOffset.Now.AddMinutes(-minutesAgo).ToString("O", CultureInfo.InvariantCulture) + "\n";

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task The_card_says_what_Easy_Install_is_doing_at_every_stage()
    {
        using var profile = new TestProfile("install-card");
        Make(profile, "A fresh one", null);
        Make(profile, "B part way", Started(12));
        Make(profile, "C done", Started(24) + InstallSignal.Marker + "\r\n");
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 3, "the library");
            var said = new List<(string Title, string Body, string Time, bool Visible)>();
            var library = window.GetLogicalDescendants().OfType<ListBox>().First(l => l.Name == "Library");

            (string, string, string, bool) Open(string name, string capture, Action<MachineViewModel>? before = null)
            {
                var item = library.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is MachineViewModel m && m.Name == name);
                Ui.Click(window, item);
                Ui.PumpUntil(() => shell.Selected is MachineViewModel m && m.Name == name, name);
                var vm = (MachineViewModel)shell.Selected!;
                Ui.PumpUntil(() => vm.Install.Stage != InstallStage.None, $"{name}'s install stage");
                before?.Invoke(vm);
                Ui.Pump(0.4);
                var view = window.GetVisualDescendants().OfType<MachineView>().Single();
                var card = view.GetLogicalDescendants().OfType<Border>().First(b => b.Name == "InstallCard");
                string Text(string n) => view.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == n).Text ?? "";
                Ui.Capture(window, "s7", capture + "-dark");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.3);
                Ui.Capture(window, "s7", capture + "-light");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                return (Text("InstallTitle"), Text("InstallBody"), Text("InstallTime"), card.IsEffectivelyVisible);
            }

            said.Add(Open("A fresh one", "install-card-not-started"));
            said.Add(Open("B part way", "install-card-off-part-way"));
            // The same machine while it runs (the power state as the watcher would set it).
            said.Add(Open("B part way", "install-card-installing", vm => vm.State = PowerState.Running));
            var rowLabel = shell.Machines.First(m => m.Name == "B part way").LibraryLabel;
            said.Add(Open("C done", "install-card-finished"));

            var gotIt = window.GetVisualDescendants().OfType<MachineView>().Single().GetLogicalDescendants().OfType<Button>().First(b => b.Name == "InstallGotIt");
            Ui.Click(window, gotIt);
            var done = (MachineViewModel)shell.Selected!;
            Ui.PumpUntil(() => !done.ShowInstallCard, "Got it to close the card");
            ((MachineViewModel)shell.Machines.First(m => m.Name == "B part way")).State = PowerState.Off;
            window.Close();
            return (said, rowLabel);
        });

        var (notStarted, offPartWay, installing, finished) = (seen.said[0], seen.said[1], seen.said[2], seen.said[3]);
        Assert.All(seen.said, s => Assert.True(s.Visible));
        Assert.Equal("Windows 11 Pro installs itself", notStarted.Title);
        Assert.Contains("nothing to press", notStarted.Body, StringComparison.Ordinal);
        Assert.Equal("Installing Windows 11 Pro: waiting for power", offPartWay.Title);
        Assert.Equal("Installing Windows 11 Pro for you", installing.Title);
        Assert.Contains("signed in as Ann", installing.Body, StringComparison.Ordinal);
        Assert.StartsWith("Started 12 minutes ago", installing.Time, StringComparison.Ordinal);
        Assert.Equal("Windows 11 Pro is installed", finished.Title);
        Assert.Equal("Installing Windows 11 Pro…", seen.rowLabel);
        // Closed for good: the next look at the folder agrees.
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "C done");
        Assert.True(InstallSignal.Read(MachineStore.Load(Path.Combine(dir, MachineStore.FileName)), dir).Acknowledged);
    }
}
