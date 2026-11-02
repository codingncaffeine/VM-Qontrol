using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

/// <summary>The New Machine wizard, walked through the real window: every page, then the machine it makes.</summary>
public sealed class WizardWindowTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    private static (MainWindow Window, ShellViewModel Shell) Open(TestProfile profile)
    {
        var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
        var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        window.Show();
        _ = shell.StartAsync();
        return (window, shell);
    }

    /// <summary>Continue until the summary, saving a frame of each page.</summary>
    private static void WalkToSummary(MainWindow window, NewMachineViewModel wizard, string batch)
    {
        for (var guard = 0; wizard.Step < 5 && guard < 8; guard++)
        {
            Ui.Capture(window, batch, $"wizard-{wizard.Step}-{NewMachineViewModel.StepTitles[wizard.Step].Replace(' ', '-').ToLowerInvariant()}");
            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.Pump(0.2);
        }

        Ui.Capture(window, batch, "wizard-5-summary");
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task A_linux_disc_walks_through_the_wizard_to_a_new_selected_machine()
    {
        using var profile = new TestProfile("wizard");
        Directory.CreateDirectory(profile.Paths.DefaultLibrary);
        var iso = Path.Combine(profile.Root, "ubuntu-24.04.1-desktop-amd64.iso");
        IsoWriter.Write(iso, "Ubuntu 24.04.1 LTS amd64", new Dictionary<string, byte[]> { ["README"] = "x"u8.ToArray() }, keepLabelCase: true);

        var seen = await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Welcome.Checked, "the host check");
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            Ui.PumpUntil(() => shell.Wizard is not null, "the wizard");
            var wizard = shell.Wizard!;
            // The file the person picks in the system's file dialog.
            _ = wizard.UseIsoAsync(iso);
            Ui.PumpUntil(() => !wizard.Detecting && wizard.IsoSummary.Length > 0 && !wizard.IsoSummary.StartsWith("Reading", StringComparison.Ordinal), "the disc to be read");
            Ui.PumpUntil(() => wizard.MemoryReason.Length > 0, "the recommendations", 20);
            var summary = wizard.IsoSummary;
            WalkToSummary(window, wizard, "phase4");
            wizard.PowerOnAfter = false;
            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.PumpUntil(() => shell.Wizard is null && shell.Selected is not null, "the new machine to be selected", 20);
            Ui.Capture(window, "phase4", "wizard-done");
            var result = (summary, shell.Selected!.Name, shell.Selected.GuestLabel, shell.Selected.Folder);
            window.Close();
            return result;
        });

        Assert.Contains("Ubuntu", seen.summary, StringComparison.Ordinal);
        Assert.Equal("Linux", seen.GuestLabel.Split(' ')[0]);
        var machine = MachineStore.Load(Path.Combine(seen.Folder, "vm.json"));
        // What the wizard recommends depends on this computer; the rule does not: whole gigabytes, at most half of it.
        var host = VmQontrol.Core.Hosts.HostProbe.Probe();
        Assert.NotNull(machine.MemoryMiB);
        Assert.Equal(0, machine.MemoryMiB!.Value % 1024);
        Assert.InRange(machine.MemoryMiB.Value, 1024, Math.Max(1024, host.MemoryGB / 2 * 1024L));
        Assert.Contains(machine.Media, m => m.Role == MediaRole.Installer && m.File == iso);
        Assert.DoesNotContain(machine.Media, m => m.Role == MediaRole.Unattended);
    }

    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_Windows_disc_offers_its_editions_and_makes_an_easy_install_machine()
    {
        var iso = Path.Combine(Path.GetDirectoryName(TestProfile.ScratchRoot)!, "Win10_22H2_English_x64v1.iso");
        Assert.SkipWhen(!File.Exists(iso), "The Windows 10 disc is not in the dev folder.");
        using var profile = new TestProfile("wizard-win");
        Directory.CreateDirectory(profile.Paths.DefaultLibrary);

        var seen = await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Welcome.Checked, "the host check");
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            Ui.PumpUntil(() => shell.Wizard is not null, "the wizard");
            var wizard = shell.Wizard!;
            _ = wizard.UseIsoAsync(iso);
            Ui.PumpUntil(() => wizard.Editions.Count > 0, "the editions", 20);
            Ui.PumpUntil(() => wizard.MemoryReason.Length > 0, "the recommendations", 20);
            var edition = wizard.Edition?.DisplayName;
            Ui.Capture(window, "phase4", "windows-0-installer");
            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.Pump(0.2);
            Ui.Capture(window, "phase4", "windows-1-system");
            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.Pump(0.2);
            var onEasyInstall = wizard.Step == 2;
            Ui.Capture(window, "phase4", "windows-2-easy-install");
            WalkToSummary(window, wizard, "phase4-windows");
            wizard.PowerOnAfter = false;
            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.PumpUntil(() => shell.Wizard is null && shell.Selected is not null, "the new machine", 20);
            var folder = shell.Selected!.Folder;
            window.Close();
            return (edition, onEasyInstall, folder);
        });

        Assert.Equal("Windows 10 Pro", seen.edition);
        Assert.True(seen.onEasyInstall);
        Assert.True(File.Exists(Path.Combine(seen.folder, "unattended.iso")));
        var machine = MachineStore.Load(Path.Combine(seen.folder, "vm.json"));
        Assert.Equal("10", machine.Guest.Release);
        Assert.Equal(DiskBus.Sata, machine.Disks[0].Bus);
    }
}
