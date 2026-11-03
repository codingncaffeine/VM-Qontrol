using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

/// <summary>Phase 12: the Preferences page, from the sidebar's gear; each change applies and is kept at once.</summary>
public sealed class PreferencesTests
{
    [Fact]
    public void The_sign_in_entry_is_an_XDG_autostart_file_that_comes_and_goes()
    {
        using var profile = new TestProfile("autostart");
        Assert.False(Autostart.IsOn(profile.Root));
        Autostart.Set(true, "/opt/vmq/vmqontrol", profile.Root);
        var text = File.ReadAllText(Autostart.Path(profile.Root));
        Assert.Contains("Exec=\"/opt/vmq/vmqontrol\" --autostart", text, StringComparison.Ordinal);
        Assert.StartsWith("[Desktop Entry]", text, StringComparison.Ordinal);
        Assert.True(Autostart.IsOn(profile.Root));
        Autostart.Set(false, "/opt/vmq/vmqontrol", profile.Root);
        Assert.False(Autostart.IsOn(profile.Root));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task Preferences_open_from_the_gear_apply_at_once_and_are_kept()
    {
        using var profile = new TestProfile("preferences");
        var realEntry = Autostart.Path();
        var realBefore = File.Exists(realEntry);
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.Pump(0.4);
            Ui.Click(window, window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "OpenPreferences"));
            Ui.PumpUntil(() => shell.ShowPreferences, "the preferences page");
            Ui.Pump(0.4);
            var view = window.GetVisualDescendants().OfType<PreferencesView>().Single();
            Ui.Capture(window, "s7", "preferences-dark");
            var prefs = shell.PreferencesPage!;
            prefs.Theme = PreferencesViewModel.ThemeOptions.First(o => o.Value == "light");
            Ui.Pump(0.4);
            var light = Avalonia.Application.Current!.ActualThemeVariant == ThemeVariant.Light;
            Ui.Capture(window, "s7", "preferences-light");
            void Toggle(string name)
            {
                var toggle = view.GetLogicalDescendants().OfType<ToggleSwitch>().First(t => t.Name == name);
                toggle.BringIntoView();
                Ui.Pump(0.3);
                Ui.Click(window, toggle);
            }

            Toggle("ConfirmPowerOff");
            Ui.PumpUntil(() => !shell.Settings.ConfirmPowerOff, "Ask before Power off, off");
            Toggle("StartAtSignIn");
            Ui.PumpUntil(() => Autostart.IsOn(profile.Paths.Config), "the sign-in entry");
            Ui.Click(window, view.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "ClosePreferences"));
            Ui.PumpUntil(() => !shell.ShowPreferences && shell.ShowWelcome, "back on the welcome page");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
            return light;
        });

        Assert.True(seen);
        AppSettings? saved = null;
        for (var i = 0; i < 50 && (saved = AppSettings.Load(profile.Paths)).ConfirmPowerOff; i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(saved!.ConfirmPowerOff);
        Assert.Equal("light", saved.Theme);
        // The control: the person's own startup folder was never touched.
        Assert.Equal(realBefore, File.Exists(realEntry));
    }

    /// <summary>When this computer shuts down: both switches on by default, the time logind gives is read and shown, and
    /// switching them off is kept (the guard reads the settings on its next look).</summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_shutdown_section_shows_the_time_and_keeps_its_switches()
    {
        using var profile = new TestProfile("prefs-shutdown");
        var (before, text) = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.Pump(0.4);
            Ui.Click(window, window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "OpenPreferences"));
            Ui.PumpUntil(() => shell.ShowPreferences, "the preferences page");
            var view = window.GetVisualDescendants().OfType<PreferencesView>().Single();
            var prefs = shell.PreferencesPage!;
            Ui.PumpUntil(() => prefs.ShutdownTimeText != "Looking…", "the time logind gives");
            ToggleSwitch Switch(string name) => view.GetLogicalDescendants().OfType<ToggleSwitch>().First(t => t.Name == name);
            var before = (Switch("SaveAtHostShutdown").IsChecked, Switch("WarnAtHostShutdown").IsChecked);
            view.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "ShutdownSleepNote").BringIntoView();
            Ui.Pump(0.4);
            Ui.Capture(window, "s12", "preferences-shutdown-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.4);
            Ui.Capture(window, "s12", "preferences-shutdown-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            foreach (var name in new[] { "SaveAtHostShutdown", "WarnAtHostShutdown" })
            {
                Switch(name).BringIntoView();
                Ui.Pump(0.3);
                Ui.Click(window, Switch(name));
            }

            Ui.PumpUntil(() => !shell.Settings.SaveAtHostShutdown && !shell.Settings.WarnAtHostShutdown, "both switches off");
            var text = prefs.ShutdownTimeText;
            window.Close();
            return (before, text);
        });

        Assert.Equal((true, true), before);
        Assert.True(text.StartsWith("This computer gives programs", StringComparison.Ordinal) || text.StartsWith("This computer has no logind", StringComparison.Ordinal), text);
        AppSettings? saved = null;
        for (var i = 0; i < 50 && (saved = AppSettings.Load(profile.Paths)).SaveAtHostShutdown; i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(saved!.SaveAtHostShutdown);
        Assert.False(saved.WarnAtHostShutdown);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_host_check_runs_again_from_its_button()
    {
        using var profile = new TestProfile("recheck");
        var note = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Welcome.Checked, "the first host check", 30);
            var button = window.GetVisualDescendants().OfType<WelcomeView>().Single().GetLogicalDescendants().OfType<Button>().First(b => b.Name == "RecheckHost");
            button.BringIntoView();
            Ui.Pump(0.2);
            Ui.Click(window, button);
            Ui.PumpUntil(() => shell.Status == "This computer was checked again.", "the second host check", 30);
            var checks = shell.Welcome.Checks.Count;
            window.Close();
            return checks;
        });
        Assert.True(note > 3);
    }
}
