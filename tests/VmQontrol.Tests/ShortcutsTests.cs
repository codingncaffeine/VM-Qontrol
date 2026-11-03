using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;

namespace VmQontrol.Tests;

/// <summary>
/// The user's ask (2026-11-03): every keyboard shortcut people expect. Pressed through the window's own input, as keys
/// are: each one does its job, a text box keeps its own keys, and an open dialog holds them back.
/// </summary>
public sealed class ShortcutsTests
{
    [Fact]
    public void No_two_shortcuts_share_keys_and_every_one_has_words_for_the_sheet()
    {
        var keys = Shortcuts.All.Select(s => s.Gesture.ToString()).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(Shortcuts.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        Assert.Contains(Shortcuts.Sheet.SelectMany(g => g.Rows), r => r.Keys == "Ctrl+? or Ctrl+/");
    }

    private static void Press(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None, PhysicalKey physical = PhysicalKey.None)
    {
        w.KeyPress(key, mods, physical, null);
        w.KeyRelease(key, mods, physical, null);
        Ui.Pump(0.25);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_shortcuts_do_what_the_sheet_says()
    {
        using var profile = new TestProfile("shortcuts");
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(lib);
        foreach (var n in new[] { "linux", "freebsd", "haiku" })
            File.Copy(Path.Combine(RecordedHost.FixtureDirectory, n + ".conf"), Path.Combine(lib, n + ".conf"));
        const RawInputModifiers Ctrl = RawInputModifiers.Control, Shift = RawInputModifiers.Shift, Alt = RawInputModifiers.Alt;

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1200, Height = 820 };
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 3, "the library");
            var r = new Dictionary<string, object?>();

            Press(window, Key.N, Ctrl, PhysicalKey.N);
            r["Ctrl+N wizard"] = shell.Wizard is not null;
            shell.Wizard?.CancelCommand.Execute(null);
            Ui.Pump(0.2);

            Press(window, Key.N, Ctrl | Shift, PhysicalKey.N);
            r["Ctrl+Shift+N folder prompt"] = shell.GroupPrompt is not null;
            Press(window, Key.Escape);
            r["Esc closes it"] = shell.GroupPrompt is null;

            Press(window, Key.OemComma, Ctrl, PhysicalKey.Comma);
            r["Ctrl+, preferences"] = shell.PreferencesPage is not null;
            Press(window, Key.Escape);
            r["Esc closes preferences"] = shell.PreferencesPage is null;

            Press(window, Key.Down, Alt, PhysicalKey.ArrowDown);
            var first = shell.Selected?.Name;
            Press(window, Key.Down, Alt, PhysicalKey.ArrowDown);
            r["Alt+Down walks the library"] = first is not null && shell.Selected?.Name != first ? "moved" : $"{first} -> {shell.Selected?.Name}";
            Press(window, Key.Up, Alt, PhysicalKey.ArrowUp);
            r["Alt+Up walks back"] = shell.Selected?.Name == first;

            Press(window, Key.D3, Ctrl, PhysicalKey.Digit3);
            r["Ctrl+3 performance"] = shell.Selected?.Tab;
            Press(window, Key.PageDown, Ctrl, PhysicalKey.PageDown);
            r["Ctrl+PgDn next tab"] = shell.Selected?.Tab;
            Press(window, Key.M, Ctrl, PhysicalKey.M);
            Ui.Pump(0.3);
            r["Ctrl+M snapshots, name box focused"] = shell.Selected?.Tab == 1 && (window.FocusManager?.GetFocusedElement() as TextBox)?.Name == "SnapshotName";

            // In a text box, Delete and Ctrl+Z are the text box's.
            Press(window, Key.Delete, RawInputModifiers.None, PhysicalKey.Delete);
            Press(window, Key.Z, Ctrl, PhysicalKey.Z);
            r["a text box keeps Delete and Ctrl+Z"] = shell.Notice is null;

            Press(window, Key.D, Ctrl, PhysicalKey.D);
            r["Ctrl+D settings"] = shell.SettingsPage is not null;
            Press(window, Key.Escape);
            r["Esc closes unchanged settings"] = shell.SettingsPage is null;

            Press(window, Key.OemQuestion, Ctrl | Shift, PhysicalKey.Slash);
            r["Ctrl+? sheet"] = shell.ShowShortcuts;
            Ui.Pump(0.3);
            Ui.Capture(window, "shortcuts", "sheet-dark");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "shortcuts", "sheet-light");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            // An open sheet holds the other shortcuts back.
            Press(window, Key.N, Ctrl, PhysicalKey.N);
            r["the sheet holds Ctrl+N"] = shell.Wizard is null;
            Press(window, Key.Escape);
            r["Esc closes the sheet"] = !shell.ShowShortcuts;

            Press(window, Key.K, Ctrl, PhysicalKey.K);
            r["Ctrl+K lists keys"] = shell.Palette.Items.FirstOrDefault(i => i.Title == "New machine")?.Keys;
            Ui.Capture(window, "shortcuts", "palette");
            Press(window, Key.Escape);

            // Delete from the library list asks before deleting an off machine.
            var item = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext == shell.Selected);
            item.Focus();
            Press(window, Key.Delete, RawInputModifiers.None, PhysicalKey.Delete);
            Ui.PumpUntil(() => shell.Prompt is not null, "the delete question", 5);
            r["Delete in the library asks"] = shell.Prompt?.Title;
            shell.Prompt?.CancelCommand.Execute(null);
            Ui.Pump(0.2);
            window.Close();
            return r;
        });

        Assert.Equal(true, seen["Ctrl+N wizard"]);
        Assert.Equal(true, seen["Ctrl+Shift+N folder prompt"]);
        Assert.Equal(true, seen["Esc closes it"]);
        Assert.Equal(true, seen["Ctrl+, preferences"]);
        Assert.Equal(true, seen["Esc closes preferences"]);
        Assert.Equal("moved", seen["Alt+Down walks the library"]);
        Assert.Equal(true, seen["Alt+Up walks back"]);
        Assert.Equal(2, seen["Ctrl+3 performance"]);
        Assert.Equal(3, seen["Ctrl+PgDn next tab"]);
        Assert.Equal(true, seen["Ctrl+M snapshots, name box focused"]);
        Assert.Equal(true, seen["a text box keeps Delete and Ctrl+Z"]);
        Assert.Equal(true, seen["Ctrl+D settings"]);
        Assert.Equal(true, seen["Esc closes unchanged settings"]);
        Assert.Equal(true, seen["Ctrl+? sheet"]);
        Assert.Equal(true, seen["the sheet holds Ctrl+N"]);
        Assert.Equal(true, seen["Esc closes the sheet"]);
        Assert.Equal("Ctrl+N", seen["Ctrl+K lists keys"]);
        Assert.NotNull(seen["Delete in the library asks"]);
    }
}
