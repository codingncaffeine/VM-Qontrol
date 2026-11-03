using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;

namespace VmQontrol.Tests;

/// <summary>The sidebar's New machine button, pressed the way a person presses it: at once, and from every page.</summary>
public sealed class NewMachineButtonTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    /// <summary>A library like a real one: a quickemu Windows 11 machine (conf + folder, no disc).</summary>
    private static void QuickemuMachine(TestProfile profile)
    {
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(Path.Combine(lib, "windows-11"));
        File.WriteAllText(Path.Combine(lib, "windows-11.conf"),
            "#!/usr/bin/quickemu --vm\nguest_os=\"windows\"\ndisk_img=\"windows-11/disk.qcow2\"\niso=\"windows-11/windows-11.iso\"\nfixed_iso=\"windows-11/virtio-win.iso\"\ndisk_size=\"64G\"\ntpm=\"on\"\nsecureboot=\"off\"\n");
    }

    private static void AssertWizardShown(MainWindow window, ShellViewModel shell, string when)
    {
        Ui.PumpUntil(() => shell.Wizard is not null, $"the wizard ({when})", 5);
        Ui.Pump(0.3);
        var page = Named<ContentControl>(window, "WizardPage");
        Assert.True(page.IsEffectivelyVisible, $"{when}: the wizard page is not visible");
        Assert.True(page.Bounds.Width > 100 && page.Bounds.Height > 100, $"{when}: the wizard page has no size ({page.Bounds})");
        foreach (var other in new[] { "MachinePage", "SettingsPage", "Welcome" })
            Assert.False(Named<Control>(window, other).IsVisible, $"{when}: {other} is still drawn over or under the wizard");
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task New_machine_opens_the_wizard_at_once_from_the_selected_machine_and_from_settings()
    {
        using var profile = new TestProfile("newbutton");
        QuickemuMachine(profile);
        await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();

            // At once, before the host check or the library read have finished.
            Ui.Pump(0.05);
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            AssertWizardShown(window, shell, "pressed at start-up");
            Ui.Capture(window, "newbutton", "1-at-once");
            shell.Wizard!.CancelCommand.Execute(null);
            Ui.PumpUntil(() => shell.Wizard is null, "the wizard to close");

            // With the library's machine selected.
            Ui.PumpUntil(() => shell.Machines.Count > 0, "the library", 20);
            shell.Selected = shell.Machines[0];
            Ui.Pump(0.5);
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            AssertWizardShown(window, shell, "pressed with a machine selected");
            Ui.Capture(window, "newbutton", "2-from-machine");
            shell.Wizard!.CancelCommand.Execute(null);
            Ui.PumpUntil(() => shell.Wizard is null, "the wizard to close");

            // From the machine's Settings page.
            shell.Selected = shell.Machines[0];
            Ui.Pump(0.3);
            shell.Selected.OpenSettingsCommand.Execute(null);
            Ui.PumpUntil(() => shell.SettingsPage is not null, "the settings page", 10);
            Ui.Pump(0.3);
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            AssertWizardShown(window, shell, "pressed from Settings");
            Ui.Capture(window, "newbutton", "3-from-settings");
            window.Close();
            return true;
        });
    }
}
