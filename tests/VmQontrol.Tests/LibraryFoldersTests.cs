using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;

namespace VmQontrol.Tests;

/// <summary>
/// The user's ask (2026-11-03): right-click the library → New folder, folders that fold up and open, machines dragged
/// into them. Walked through the window: the list's own menu, the name prompt, a drop on the heading (the platform's
/// drag-over and drop events at the heading's spot), a click on the heading, its menu's Rename and Delete.
/// </summary>
public sealed class LibraryFoldersTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static List<string> Sidebar(ShellViewModel shell) =>
        shell.SidebarItems.Select(i => i is GroupHeading h ? $"[{h.Name}{(h.IsCollapsed ? " +" : "")}]" : ((MachineViewModel)i).Name).ToList();

    private static void Type(Window window, string name, string text)
    {
        Ui.PumpUntil(() => window.DataContext is ShellViewModel { GroupPrompt: not null }, "the folder prompt");
        Ui.Pump(0.2);
        var box = Named<TextBox>(window, "GroupName");
        box.Focus();
        box.SelectAll();
        window.KeyTextInput(text);
        Ui.Click(window, Named<Button>(window, "GroupConfirm"));
        Ui.PumpUntil(() => window.DataContext is ShellViewModel { GroupPrompt: null }, "the prompt to close");
        Ui.Pump(0.2);
    }

    private static Button Heading(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("groupheading") && (b.DataContext as GroupHeading)?.Name == name);

    /// <summary>Right-clicks a folder's heading and clicks one of its menu's items.</summary>
    private static void MenuOf(Window window, string folder, string item)
    {
        var heading = Heading(window, folder);
        Ui.Click(window, heading, MouseButton.Right);
        Ui.Pump(0.3);
        var entry = heading.ContextMenu!.Items.OfType<MenuItem>().First(m => m.Name == item);
        Assert.True(entry.IsEffectivelyVisible, $"{folder}'s menu did not open");
        Ui.Click(window, entry);
        Ui.Pump(0.2);
    }

    private static void DropOn(Window window, Control target, string machineId)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        using var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(MainWindow.MachineDrag, System.Text.Encoding.UTF8.GetBytes(machineId)));
        window.DragDrop(at, RawDragEventType.DragEnter, transfer, DragDropEffects.Move, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.DragOver, transfer, DragDropEffects.Move, RawInputModifiers.None);
        Ui.Pump(0.1);
        window.DragDrop(at, RawDragEventType.Drop, transfer, DragDropEffects.Move, RawInputModifiers.None);
        Ui.Pump(0.3);
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task Folders_are_made_from_the_library_menu_take_dropped_machines_and_fold_up()
    {
        using var profile = new TestProfile("library-folders");
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(lib);
        foreach (var n in new[] { "linux", "freebsd", "haiku" })
            File.Copy(Path.Combine(RecordedHost.FixtureDirectory, n + ".conf"), Path.Combine(lib, n + ".conf"));

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1200, Height = 800 };
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 3, "the library");
            var library = Named<ListBox>(window, "Library");

            // Right-click the list below the machines: New folder…
            var below = library.TranslatePoint(new Point(library.Bounds.Width / 2, library.Bounds.Height - 20), window)!.Value;
            window.MouseMove(below);
            window.MouseDown(below, MouseButton.Right);
            window.MouseUp(below, MouseButton.Right);
            Ui.Pump(0.3);
            var newFolder = library.ContextMenu!.Items.OfType<MenuItem>().First(m => m.Name == "LibraryNewFolder");
            var menuOpen = newFolder.IsEffectivelyVisible;
            Ui.Click(window, newFolder);
            Type(window, "NewFolderPrompt", "Unix");
            var empty = Sidebar(shell);

            // The + beside LIBRARY makes a second one.
            Ui.Click(window, Named<Button>(window, "NewFolder"));
            Type(window, "", "Retro");

            // Drop two machines on Unix's heading, one on Retro's.
            var id = (string name) => shell.Machines.First(m => m.Name == name).Id;
            DropOn(window, Heading(window, "Unix"), id("linux"));
            DropOn(window, Heading(window, "Unix"), id("freebsd"));
            DropOn(window, Heading(window, "Retro"), id("haiku"));
            var filed = Sidebar(shell);
            Ui.Capture(window, "folders", "library-folders-dark");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "folders", "library-folders-light");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            // A click on the heading folds Unix away; again opens it.
            Ui.Click(window, Heading(window, "Unix"));
            var folded = Sidebar(shell);
            Ui.Capture(window, "folders", "library-folders-folded");
            Ui.Click(window, Heading(window, "Unix"));
            var opened = Sidebar(shell);

            // Dropped on LIBRARY: out of its folder.
            DropOn(window, Named<Grid>(window, "LibraryHeading"), id("haiku"));
            var outOfFolder = Sidebar(shell);

            // Rename Retro from its heading's menu; delete Unix (its machines stay).
            MenuOf(window, "Retro", "FolderRename");
            Type(window, "", "Old");
            MenuOf(window, "Unix", "FolderDelete");
            Ui.PumpUntil(() => shell.Notice is not null, "the delete question");
            Ui.Click(window, Named<Button>(window, "NoticeConfirm"));
            Ui.Pump(0.4);
            var deleted = Sidebar(shell);
            window.Close();
            return (menuOpen, empty, filed, folded, opened, outOfFolder, deleted);
        });

        Assert.True(seen.menuOpen, "the library's right-click menu did not open");
        Assert.Equal(["freebsd", "haiku", "linux", "[Unix]"], seen.empty);
        Assert.Equal(["[Unix]", "freebsd", "linux", "[Retro]", "haiku"], seen.filed);
        Assert.Equal(["[Unix +]", "[Retro]", "haiku"], seen.folded);
        Assert.Equal(seen.filed, seen.opened);
        Assert.Equal(["haiku", "[Unix]", "freebsd", "linux", "[Retro]"], seen.outOfFolder);
        Assert.Equal(["freebsd", "haiku", "linux", "[Old]"], seen.deleted);
        // Folder changes are saved on a worker (the app waits for them as it exits): so does the test, or a busy computer
        // reads the file before the last save lands.
        Assert.True(AppSettings.WaitForSaves(TimeSpan.FromSeconds(5)));
        var saved = AppSettings.Load(profile.Paths);
        Assert.Equal(["Old"], saved.Groups);
        Assert.Empty(saved.GroupOf);
    }

    [Fact]
    public void Settings_from_before_folders_were_groups_keep_their_folders()
    {
        using var profile = new TestProfile("folders-compat");
        Directory.CreateDirectory(profile.Paths.Config);
        File.WriteAllText(profile.Paths.SettingsFile, """{ "folders": { "id1": "Retro", "id2": "Work" } }""");
        var settings = AppSettings.Load(profile.Paths);
        Assert.Equal("Retro", settings.GroupOf["id1"]);
        Assert.Equal(["Retro", "Work"], settings.OrderedGroups());
        settings.Save(profile.Paths);
        Assert.DoesNotContain("\"folders\"", File.ReadAllText(profile.Paths.SettingsFile));
        Assert.Equal("Work", AppSettings.Load(profile.Paths).GroupOf["id2"]);
    }
}
