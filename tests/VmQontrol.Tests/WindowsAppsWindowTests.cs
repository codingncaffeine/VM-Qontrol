using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.WindowsApps;

namespace VmQontrol.Tests;

/// <summary>The machine page's Windows apps tab (D43), walked as the person walks it, on a list Windows gave earlier.</summary>
public sealed class WindowsAppsWindowTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    /// <summary>A 256 px icon drawn here (a rounded square in one color), saved as Windows' list keeps them.</summary>
    private static void DrawIcon(string file, Color color)
    {
        var panel = new Border { Width = 256, Height = 256, CornerRadius = new CornerRadius(48), Background = new SolidColorBrush(color) };
        panel.Measure(new Size(256, 256));
        panel.Arrange(new Rect(0, 0, 256, 256));
        using var bitmap = new RenderTargetBitmap(new PixelSize(256, 256));
        bitmap.Render(panel);
        bitmap.Save(file, new PngBitmapEncoderOptions());
    }

    private static string WindowsMachine(TestProfile profile, bool withList)
    {
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Windows 11");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "disk0.qcow2"), []);
        MachineStore.Save(new MachineConfig { Name = "Windows 11", Guest = new GuestConfig { Os = "windows", Release = "11" }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 64 }] },
            Path.Combine(dir, "vm.json"));
        if (!withList) return dir;
        var apps = Path.Combine(dir, "windows-apps");
        Directory.CreateDirectory(apps);
        var list = new (string Id, string Name, string? Program, Color Color)[]
        {
            ("Microsoft.Paint_8wekyb3d8bbwe!App", "Paint", null, Color.Parse("#3D8BFD")),
            ("MSEdge", "Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", Color.Parse("#1BA37A")),
            ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\charmap.exe", "Character Map", @"C:\WINDOWS\system32\charmap.exe", Color.Parse("#8C6CF2")),
            ("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal", null, Color.Parse("#30343C")),
            ("{6D809377-6AF0-444B-8957-A3773F02200E}\\Notepad++\\notepad++.exe", "Notepad++ with a name long enough to be cut short", @"C:\Program Files\Notepad++\notepad++.exe", Color.Parse("#E5A50A")),
        };
        var json = new System.Text.Json.Nodes.JsonArray();
        foreach (var (id, name, program, color) in list)
        {
            var icon = WindowsAppsCatalog.Slug(id) + ".png";
            DrawIcon(Path.Combine(apps, icon), color);
            json.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["name"] = name, ["program"] = program, ["arguments"] = null, ["icon"] = icon });
        }

        File.WriteAllText(Path.Combine(apps, "apps.json"), "\uFEFF" + json.ToJsonString());
        return dir;
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_windows_apps_tab_lists_the_programs_finds_one_and_puts_it_in_this_computers_menu()
    {
        using var profile = new TestProfile("winapps-window");
        var seen = await Ui.Run(() =>
        {
            // The icons are drawn on the window's thread, before the library is read.
            var dir = WindowsMachine(profile, withList: true);
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 860 };
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");

            var item = Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First();
            Ui.Click(window, item);
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Pump(0.3);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            var tab = Named<TabItem>(page, "WindowsAppsTab");
            var tabShown = tab.IsVisible;
            Ui.Click(window, tab);
            Ui.PumpUntil(() => shell.Selected!.WindowsApps.Apps.Count == 5, "the list Windows gave earlier");
            Ui.Pump(0.5);
            var status = Named<TextBlock>(page, "WindowsAppsStatus").Text;
            Ui.Capture(window, "s14-winapps", "tab-dark");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s14-winapps", "tab-light");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            // The person types in the search box.
            var search = Named<TextBox>(page, "WindowsAppsSearch");
            search.Text = "pai";
            Ui.Pump(0.3);
            var found = shell.Selected!.WindowsApps.Apps.Select(a => a.Name).ToList();
            search.Text = "";
            Ui.Pump(0.3);

            // Ticks "In app menu" on Character Map's tile.
            var tile = Named<ItemsControl>(page, "WindowsAppsList").GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("appTile") && (b.DataContext as WindowsAppItem)?.Name == "Character Map");
            Ui.Click(window, tile.GetVisualDescendants().OfType<CheckBox>().Single());
            var entry = Path.Combine(profile.Paths.DesktopData, "applications");
            Ui.PumpUntil(() => Directory.Exists(entry) && Directory.EnumerateFiles(entry, "*.desktop").Any(), "the menu entry");
            Ui.Pump(0.3);
            Ui.Capture(window, "s14-winapps", "in-menu");
            var files = Directory.EnumerateFiles(entry, "*.desktop").ToList();
            var text = File.ReadAllText(files.Single());
            var icon = Directory.EnumerateFiles(Path.Combine(profile.Paths.DesktopData, "icons", "hicolor", "256x256", "apps")).ToList();

            // Unticks it: the entry and its icon go.
            Ui.Click(window, tile.GetVisualDescendants().OfType<CheckBox>().Single());
            Ui.PumpUntil(() => !Directory.EnumerateFiles(entry, "*.desktop").Any(), "the entry to go");
            var iconsAfter = Directory.EnumerateFiles(Path.Combine(profile.Paths.DesktopData, "icons", "hicolor", "256x256", "apps")).Count();

            // In the menu again, then Turn off (the machine is off: nothing to do inside Windows).
            Ui.Click(window, tile.GetVisualDescendants().OfType<CheckBox>().Single());
            Ui.PumpUntil(() => Directory.EnumerateFiles(entry, "*.desktop").Any(), "the entry again");
            Ui.Click(window, Named<Button>(page, "WindowsAppsTurnOff"));
            Ui.PumpUntil(() => !shell.Selected!.WindowsApps.HasList, "the list to clear");
            Ui.Pump(0.3);
            var entriesAfterOff = Directory.EnumerateFiles(entry, "*.desktop").Count();
            var listKept = Directory.Exists(WindowsAppsCatalog.HostFolder(dir));
            var introBack = Named<Button>(page, "WindowsAppsTurnOn").IsEffectivelyVisible;
            Ui.Capture(window, "s14-winapps", "turned-off");
            window.Close();
            return (tabShown, status, found, files, text, icon, iconsAfter, dir, entriesAfterOff, listKept, introBack);
        });

        Assert.True(seen.tabShown);
        Assert.Equal("5 Windows programs can open as windows here", seen.status);
        Assert.Equal(["Paint"], seen.found);
        // An isolated profile keeps its entries inside itself, never in the desktop's own folder.
        Assert.StartsWith(profile.Root, seen.files.Single(), StringComparison.Ordinal);
        Assert.Contains("Name=Character Map\n", seen.text, StringComparison.Ordinal);
        Assert.Contains($" {WindowsAppsMenu.Argument} ", seen.text, StringComparison.Ordinal);
        Assert.Contains($"X-VMQontrol-Machine={MachineStore.Load(Path.Combine(seen.dir, "vm.json")).Id}\n", seen.text, StringComparison.Ordinal);
        Assert.Contains("StartupWMClass=vm-qontrol-", seen.text, StringComparison.Ordinal);
        Assert.Single(seen.icon);
        Assert.Equal(0, seen.iconsAfter);
        Assert.Equal(0, seen.entriesAfterOff);
        Assert.False(seen.listKept);
        Assert.True(seen.introBack);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_windows_machine_that_never_listed_its_programs_offers_to_turn_them_on_and_a_linux_one_has_no_tab()
    {
        using var profile = new TestProfile("winapps-intro");
        WindowsMachine(profile, withList: false);
        var linux = Path.Combine(profile.Paths.DefaultLibrary, "Fedora");
        Directory.CreateDirectory(linux);
        File.WriteAllBytes(Path.Combine(linux, "disk0.qcow2"), []);
        MachineStore.Save(new MachineConfig { Name = "Fedora", Guest = new GuestConfig { Os = "linux" }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 20 }] }, Path.Combine(linux, "vm.json"));
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 860 };
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 2, "the library");
            var items = Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Ui.Click(window, items.First(i => (i.DataContext as MachineViewModel)?.Name == "Windows 11"));
            Ui.PumpUntil(() => shell.Selected?.Name == "Windows 11", "the Windows page");
            Ui.Pump(0.3);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            Ui.Click(window, Named<TabItem>(page, "WindowsAppsTab"));
            Ui.Pump(0.6);
            var turnOn = Named<Button>(page, "WindowsAppsTurnOn");
            var offered = turnOn.IsEffectivelyVisible && turnOn.IsEffectivelyEnabled;
            // With no FreeRDP here and polkit able to install it, a button does it: no command to type.
            var rdpCard = Named<Border>(page, "FreeRdpCard").IsEffectivelyVisible;
            var installButton = Named<Button>(page, "InstallFreeRdp").IsEffectivelyVisible;
            Ui.Capture(window, "s14-winapps", "intro-dark");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s14-winapps", "intro-light");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as MachineViewModel)?.Name == "Fedora"));
            Ui.PumpUntil(() => shell.Selected?.Name == "Fedora", "the Linux page");
            Ui.Pump(0.3);
            var linuxPage = window.GetVisualDescendants().OfType<MachineView>().Single();
            var linuxTab = Named<TabItem>(linuxPage, "WindowsAppsTab").IsVisible;
            window.Close();
            return (offered, linuxTab, rdpCard, installButton);
        });

        Assert.True(seen.offered);
        Assert.Equal(WindowsAppsClient.FindFreeRdp() is null, seen.rdpCard);
        Assert.Equal(seen.rdpCard && VmQontrol.Core.Hosts.PackageInstall.CanInstall("freerdp"), seen.installButton);
        Assert.False(seen.linuxTab);
    }
}

/// <summary>The small window an app menu entry opens (vmqontrol --windows-app MACHINE APP).</summary>
public sealed class WindowsAppLaunchWindowTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task An_entry_whose_machine_is_gone_says_so_and_the_password_step_reads_well()
    {
        using var profile = new TestProfile("winapps-launch");
        var seen = await Ui.Run(() =>
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            var vm = new WindowsAppLaunchViewModel(profile.Paths, "0123456789abcdef0123456789abcdef", "ef88144d7669");
            var window = new WindowsAppLaunchWindow { DataContext = vm };
            window.Show();
            _ = vm.RunAsync();
            Ui.PumpUntil(() => vm.Failed, "the refusal");
            Ui.Pump(0.2);
            var said = vm.Status;
            var close = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "LaunchClose").Content as string;
            Ui.Capture(window, "s14-winapps", "launch-gone");

            // The step where Windows wants the password, as the person sees it (dark and light).
            vm.Failed = false;
            vm.Title = "Opening Paint";
            vm.Account = "tester";
            vm.Status = "Type the Windows password of tester. It is kept in this desktop's keyring.";
            vm.Working = false;
            vm.Asking = true;
            Ui.Pump(0.3);
            Ui.Capture(window, "s14-winapps", "launch-password-dark");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s14-winapps", "launch-password-light");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            var signIn = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "LaunchSignIn");
            var enabledEmpty = signIn.IsEffectivelyEnabled;
            window.Close();
            return (said, close, enabledEmpty);
        });

        Assert.Contains("not in VM Qontrol's library any more", seen.said, StringComparison.Ordinal);
        Assert.Equal("Close", seen.close);
        Assert.False(seen.enabledEmpty);
    }
}
