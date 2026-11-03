using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 19 item 1 (s11): several machines selected in the library with Ctrl+click and Shift+click, shown together with
/// the actions for many: move to a folder, favorites, delete (one confirmation, the count typed). Walked with the mouse
/// and keys; deletion is checked on the disk.
/// </summary>
public sealed class MultiSelectTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name && c.IsEffectivelyVisible);

    private static ListBoxItem Row(Window window, string machine) =>
        Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as MachineViewModel)?.Name == machine);

    private static void ClickWith(Window window, Control target, RawInputModifiers mods)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at, mods);
        window.MouseDown(at, MouseButton.Left, mods);
        window.MouseUp(at, MouseButton.Left, mods);
        Ui.Pump(0.15);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task Several_machines_are_selected_moved_favorited_and_deleted_together()
    {
        using var profile = new TestProfile("multi");
        foreach (var name in (string[])["Alpha", "Bravo", "Charlie", "Delta"])
        {
            var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
            Directory.CreateDirectory(dir);
            MachineStore.Save(new MachineConfig { Name = name, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
            File.WriteAllBytes(Path.Combine(dir, "disk0.qcow2"), []);
        }

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 860 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 4, "the library");
            Ui.Pump(0.3);

            // One click: one machine's page. Ctrl+click another: both, on the page for several.
            ClickWith(window, Row(window, "Alpha"), RawInputModifiers.None);
            var single = shell.ShowMachine && !shell.ShowMany;
            ClickWith(window, Row(window, "Charlie"), RawInputModifiers.Control);
            Ui.PumpUntil(() => shell.ShowMany, "the page for several");
            var two = (Named<TextBlock>(window, "ManyTitle").Text, string.Join(",", shell.Chosen.Select(m => m.Name)));

            // Shift+click extends from the last clicked machine, as in a file manager: Alpha, then Shift+Delta = all four.
            ClickWith(window, Row(window, "Alpha"), RawInputModifiers.None);
            ClickWith(window, Row(window, "Delta"), RawInputModifiers.Shift);
            Ui.PumpUntil(() => shell.Chosen.Count == 4, "the range");
            Ui.Capture(window, "s11", "many-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.2);
            Ui.Capture(window, "s11", "many-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            // Esc: back to one machine.
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Ui.PumpUntil(() => !shell.ShowMany && shell.ShowMachine, "back to one");
            var backToOne = shell.Chosen.Count;

            // Bravo and Delta: into a new folder, then favorites, then deleted with the count typed.
            ClickWith(window, Row(window, "Bravo"), RawInputModifiers.None);
            ClickWith(window, Row(window, "Delta"), RawInputModifiers.Control);
            Ui.PumpUntil(() => shell.ShowMany, "two selected");
            Ui.Click(window, Named<Button>(window, "ManyMove"));
            Ui.PumpUntil(() => shell.GroupPrompt is not null, "the folder prompt");
            Ui.Pump(0.2);
            var box = Named<TextBox>(window, "GroupName");
            box.Focus();
            window.KeyTextInput("Work");
            Ui.Click(window, Named<Button>(window, "GroupConfirm"));
            Ui.PumpUntil(() => shell.GroupPrompt is null, "the prompt closed");
            var folders = string.Join(",", shell.Machines.OrderBy(m => m.Name).Select(m => m.GroupName));
            Ui.PumpUntil(() => shell.ShowMany, "still two selected"); // a folder move re-sorts the list; the selection stays
            Ui.Click(window, Named<Button>(window, "ManyFavorite"));
            Ui.Pump(0.2);
            var favorites = string.Join(",", shell.Machines.Where(m => m.IsFavorite).Select(m => m.Name).Order());

            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Ui.PumpUntil(() => shell.Prompt is not null, "the delete prompt");
            Ui.Pump(0.2);
            var promptTitle = shell.Prompt!.Title;
            var wrongCountRefused = !shell.Prompt.ConfirmCommand.CanExecute(null);
            Named<TextBox>(window, "PromptText").Focus();
            window.KeyTextInput("2");
            Ui.Pump(0.1);
            Ui.Click(window, Named<Button>(window, "PromptConfirm"));
            Ui.PumpUntil(() => shell.Machines.Count == 2, "two machines deleted", 20);
            var left = string.Join(",", shell.Machines.Select(m => m.Name).Order());
            window.Close();
            return (single, two, backToOne, folders, favorites, promptTitle, wrongCountRefused, left);
        });

        Assert.True(seen.single);
        Assert.Equal(("2 machines selected", "Alpha,Charlie"), seen.two);
        Assert.Equal(1, seen.backToOne);
        Assert.Equal(",Work,,Work", seen.folders);
        Assert.Equal("Bravo,Delta", seen.favorites);
        Assert.Equal("Delete 2 machines?", seen.promptTitle);
        Assert.True(seen.wrongCountRefused);
        Assert.Equal("Alpha,Charlie", seen.left);
        Assert.False(Directory.Exists(Path.Combine(profile.Paths.DefaultLibrary, "Bravo")));
        Assert.False(Directory.Exists(Path.Combine(profile.Paths.DefaultLibrary, "Delta")));
        Assert.True(Directory.Exists(Path.Combine(profile.Paths.DefaultLibrary, "Alpha"))); // control: the others stay
    }
}
