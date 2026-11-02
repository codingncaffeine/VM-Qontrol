using Avalonia.Controls;
using VmQontrol.App.Views;

namespace VmQontrol.Tests;

public sealed class MainWindowTests
{
    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task The_window_opens_with_the_library_and_the_welcome_page()
    {
        var seen = await Ui.Run(() =>
        {
            var window = new MainWindow();
            window.Show();
            Ui.Pump(0.2);
            var welcome = window.FindControl<StackPanel>("Welcome");
            var empty = window.FindControl<TextBlock>("LibraryEmpty");
            var result = (window.Title, WelcomeShown: welcome?.IsEffectivelyVisible ?? false, empty?.Text, window.Bounds.Width);
            window.Close();
            return result;
        });

        Assert.Equal("VM Qontrol", seen.Title);
        Assert.True(seen.WelcomeShown);
        Assert.Equal("No machines yet.", seen.Text);
        Assert.True(seen.Width > 0);
    }
}
