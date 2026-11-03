using Avalonia.Controls;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;

namespace VmQontrol.Tests;

/// <summary>The main window opens at the size it was closed at, maximized again if it was (the person's path: resize, maximize, close, start again).</summary>
public sealed class WindowPlacementTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_main_window_comes_back_at_its_size_and_maximized()
    {
        using var profile = new TestProfile("placement");
        var first = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell };
            window.RestorePlacement(shell.Settings.MainWindow); // control: nothing saved yet, the XAML size stands
            window.Show();
            Ui.Pump(0.2);
            var opened = window.ClientSize;
            window.Width = 1100;
            window.Height = 700;
            Ui.Pump(0.2);
            window.WindowState = WindowState.Maximized;
            Ui.Pump(0.2);
            window.Close();
            return opened;
        });

        Assert.Equal(new Avalonia.Size(1320, 860), first);
        Assert.True(AppSettings.WaitForSaves(TimeSpan.FromSeconds(5)));
        var saved = AppSettings.Load(profile.Paths).MainWindow;
        Assert.NotNull(saved);
        Assert.Equal((1100d, 700d, true), (saved.Width, saved.Height, saved.Maximized)); // the size from before maximizing

        var again = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell };
            window.RestorePlacement(shell.Settings.MainWindow! with { Maximized = false });
            window.Show();
            Ui.Pump(0.2);
            var size = window.ClientSize;
            var restoredMaximized = new MainWindow { DataContext = shell };
            restoredMaximized.RestorePlacement(shell.Settings.MainWindow);
            var state = restoredMaximized.WindowState;
            window.Close();
            return (size, state);
        });

        Assert.Equal(new Avalonia.Size(1100, 700), again.size);
        Assert.Equal(WindowState.Maximized, again.state);
    }

    [Fact]
    public void A_damaged_saved_size_is_dropped()
    {
        using var profile = new TestProfile("placement-bad");
        Directory.CreateDirectory(profile.Paths.Config);
        File.WriteAllText(profile.Paths.SettingsFile, """{ "mainWindow": { "width": 5, "height": 1e300 } }""");
        Assert.Null(AppSettings.Load(profile.Paths).MainWindow);
        File.WriteAllText(profile.Paths.SettingsFile, """{ "mainWindow": { "width": 1000, "height": 640 } }""");
        Assert.Equal(1000, AppSettings.Load(profile.Paths).MainWindow!.Width);
    }
}
