using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

/// <summary>
/// Easy Install for a machine that already exists (added from a folder, or made with it off): the machine page offers
/// it, the wizard's own disc and Easy Install pages ask only what they need, and the machine then installs itself.
/// </summary>
public sealed class EasyInstallLaterTests
{
    private static (MachineConfig Machine, string Dir) Make(TestProfile profile, string iso)
    {
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "From a folder");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig
        {
            Name = "From a folder",
            Guest = new GuestConfig { Os = "linux" },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 20 }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = iso, Connect = Connect.UntilInstalled }],
            MemoryMiB = 3072,
        };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        return (m, dir);
    }

    private static string FedoraDisc(TestProfile profile)
    {
        var iso = Path.Combine(profile.Root, "fedora.iso");
        IsoWriter.Write(iso, "Fedora-S-dvd-x86_64-41", new Dictionary<string, byte[]> { [".treeinfo"] = "[general]\n"u8.ToArray() }, keepLabelCase: true);
        return iso;
    }

    [Fact]
    public void Only_an_empty_machine_with_an_unanswered_installer_is_offered_it_and_adding_keeps_its_hardware()
    {
        using var profile = new TestProfile("easy-later");
        var (m, dir) = Make(profile, FedoraDisc(profile));
        Assert.True(MachineFactory.CanAddEasyInstall(m, dir));
        Assert.False(MachineFactory.CanAddEasyInstall(m with { Media = [] }, dir));
        Assert.False(MachineFactory.CanAddEasyInstall(m with { IsTemplate = true }, dir));
        Assert.False(MachineFactory.CanAddEasyInstall(m with { Guest = new GuestConfig { Os = "macos" } }, dir));

        var added = MachineFactory.AddEasyInstall(m, dir, new NewMachineRequest
        {
            Name = m.Name,
            LibraryRoot = profile.Paths.DefaultLibrary,
            Guest = new GuestConfig { Os = "linux", OsInfoId = "http://fedoraproject.org/fedora/41" },
            InstallerIso = Path.Combine(dir, m.Media[0].File),
            LinuxEasyInstall = new LinuxInstallChoices { UserName = "Ann", Password = "x", HostName = "box" },
        });
        Assert.Equal(new EasyInstallConfig { System = "Fedora", UserName = "Ann" }, added.EasyInstall);
        Assert.Equal("ann", added.Guest.Account);
        Assert.Contains(added.Media, x => x.Role == MediaRole.Unattended && x.File == "kickstart.iso");
        Assert.Equal(3072, added.MemoryMiB);
        Assert.Equal(20, added.Disks.Single().SizeGiB);
        var saved = MachineStore.Load(Path.Combine(dir, MachineStore.FileName));
        Assert.Equal(added.EasyInstall, saved.EasyInstall);
        Assert.Equal("http://fedoraproject.org/fedora/41", saved.Guest.OsInfoId);
        Assert.Equal(added.Media.Select(x => x.File), saved.Media.Select(x => x.File));
        // Now it answers itself: no second offer, and the card says it installs itself.
        Assert.False(MachineFactory.CanAddEasyInstall(added, dir));
        Assert.Equal(InstallStage.NotStarted, InstallSignal.Read(added, dir).Stage);
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task The_machine_page_offers_it_and_the_wizard_sets_it_up()
    {
        using var profile = new TestProfile("easy-later-ui");
        var (_, dir) = Make(profile, FedoraDisc(profile));
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, window.GetLogicalDescendants().OfType<ListBox>().First(l => l.Name == "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is { ShowEasyInstallOffer: true }, "the Easy Install offer");
            Ui.Pump(0.4);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            var offerTitle = page.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "InstallTitle").Text;
            Ui.Capture(window, "s7", "easy-offer-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s7", "easy-offer-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            Ui.Click(window, page.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "SetUpEasyInstall"));
            Ui.PumpUntil(() => shell.Wizard is { ForExisting: true }, "the wizard for this machine");
            var wizard = shell.Wizard!;
            Ui.PumpUntil(() => wizard.NextCommand.CanExecute(null), "the disc to be read");
            Ui.Pump(0.3);
            var view = window.GetVisualDescendants().OfType<NewMachineView>().Single();
            var title = view.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "WizardTitle").Text;
            // What the rail DRAWS, not what the view model would say now.
            var rail = view.GetVisualDescendants().OfType<ItemsControl>().First(i => i.ItemsSource == wizard.Steps || i.ItemsSource is IReadOnlyList<StepItem>)
                .Items.OfType<StepItem>().Select(s => s.Title).ToList();
            Ui.Capture(window, "s7", "easy-later-disc");
            Button Next() => view.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "Next");
            Ui.Click(window, Next());
            Ui.PumpUntil(() => wizard.Step == 2, "the Easy Install page");
            Ui.Pump(0.3);
            var password = view.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "Password");
            password.Focus();
            window.KeyTextInput("Qontrol-1");
            Ui.PumpUntil(() => wizard.NextCommand.CanExecute(null), "a password to be typed");
            Ui.Capture(window, "s7", "easy-later-answers");
            Ui.Click(window, Next());
            Ui.PumpUntil(() => wizard.Step == 5, "the summary");
            Ui.Pump(0.3);
            var nextText = Next().Content as string;
            // A test never powers a machine on: untick it as a person would.
            var powerOn = view.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "PowerOnAfter");
            if (powerOn.IsChecked == true) Ui.Click(window, powerOn);
            Ui.PumpUntil(() => !wizard.PowerOnAfter, "power on unticked");
            Ui.Capture(window, "s7", "easy-later-summary");
            Ui.Click(window, Next());
            Ui.PumpUntil(() => shell.Wizard is null && shell.Selected is { Install.Stage: InstallStage.NotStarted }, "the machine to install itself");
            Ui.Pump(0.4);
            var after = window.GetVisualDescendants().OfType<MachineView>().Single().GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "InstallTitle").Text;
            Ui.Capture(window, "s7", "easy-later-done");
            window.Close();
            return (offerTitle, title, rail, nextText, after);
        });

        Assert.Equal("Let VM Qontrol install Linux for you?", seen.offerTitle);
        Assert.Equal("Easy Install for From a folder", seen.title);
        Assert.Equal(["Installer", "Easy Install", "Summary"], seen.rail);
        Assert.Equal("Set up Easy Install", seen.nextText);
        Assert.EndsWith("installs itself", seen.after, StringComparison.Ordinal);
        var saved = MachineStore.Load(Path.Combine(dir, MachineStore.FileName));
        Assert.NotNull(saved.EasyInstall);
        Assert.Contains(saved.Media, x => x.Role == MediaRole.Unattended);
        Assert.Equal(3072, saved.MemoryMiB);
        // The control for "a test never powers a machine on": no process carries this profile's folder.
        var started = Directory.EnumerateDirectories("/proc").Where(d => int.TryParse(Path.GetFileName(d), out _))
            .Select(d => { try { return File.ReadAllText(Path.Combine(d, "cmdline")); } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; } })
            .Count(c => c.Contains(profile.Root, StringComparison.Ordinal) && c.StartsWith("qemu", StringComparison.Ordinal));
        Assert.Equal(0, started);
    }
}
