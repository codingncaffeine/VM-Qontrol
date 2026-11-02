using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

public sealed class MainWindowTests
{
    private static (MainWindow Window, ShellViewModel Shell) Open(TestProfile profile)
    {
        var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
        var window = new MainWindow { DataContext = shell, Width = 1320, Height = 860 };
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        window.Show();
        _ = shell.StartAsync();
        return (window, shell);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task A_machine_that_cannot_start_says_why_from_its_page_and_from_power_on()
    {
        using var profile = new TestProfile("window");
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(Path.Combine(lib, "windows-11"));
        foreach (var n in new[] { "windows-11", "linux" })
            File.Copy(Path.Combine(RecordedHost.FixtureDirectory, n + ".conf"), Path.Combine(lib, n + ".conf"));

        var seen = await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Machines.Count == 2 && shell.Welcome.Checked, "the library and the host check");
            Ui.Capture(window, "phase3", "welcome");

            // The user clicks the Windows machine in the library.
            var list = Named<ListBox>(window, "Library");
            var item = list.GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as MachineViewModel)?.Name == "windows-11");
            Ui.Click(window, item);
            Ui.PumpUntil(() => shell.Selected?.Name == "windows-11", "the machine page");
            Ui.Pump(0.3);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            var problems = Named<Border>(page, "Problems");
            var problemText = string.Join("\n", problems.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Ui.Capture(window, "phase3", "machine-with-problems");

            // Power on: refused before QEMU runs, with the reason in a notice.
            Ui.Click(window, Named<Button>(page, "PowerOn"));
            Ui.PumpUntil(() => shell.Notice is not null, "the refusal notice");
            Ui.Pump(0.2);
            var title = Named<TextBlock>(window, "NoticeTitle").Text;
            var lines = string.Join("\n", shell.Notice!.Lines);
            Ui.Capture(window, "phase3", "refusal-notice");
            Ui.Click(window, Named<Button>(window, "NoticeDismiss"));
            Ui.PumpUntil(() => shell.Notice is null, "the notice to close");
            var stillOff = shell.Selected!.IsOff;

            // Ctrl+K, type, Enter: the palette runs the command it highlights.
            window.KeyPress(Avalonia.Input.Key.K, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.K, "k");
            Ui.PumpUntil(() => shell.Palette.IsOpen, "the command palette");
            window.KeyTextInput("perf");
            Ui.Pump(0.2);
            var highlighted = shell.Palette.Highlighted?.Title;
            Ui.Capture(window, "phase3", "palette");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Ui.PumpUntil(() => !shell.Palette.IsOpen, "the palette to close");
            var tab = shell.Selected!.Tab;
            window.Close();
            return (problems.IsVisible, problemText, title, lines, stillOff, highlighted, tab);
        });

        Assert.True(seen.IsVisible);
        Assert.Contains("installer disc", seen.problemText, StringComparison.Ordinal);
        Assert.Equal("windows-11 could not start", seen.title);
        Assert.Contains("windows-11.iso", seen.lines, StringComparison.Ordinal);
        Assert.True(seen.stillOff);
        Assert.Equal("Show performance", seen.highlighted);
        Assert.Equal(2, seen.tab);
    }

    /// <summary>Right-click a machine, Move to folder, type a name: the sidebar files it under that heading, and it is remembered.</summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_machine_moved_to_a_folder_is_filed_under_it_and_remembered()
    {
        using var profile = new TestProfile("folders");
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(lib);
        foreach (var n in new[] { "linux", "freebsd", "haiku" })
            File.Copy(Path.Combine(RecordedHost.FixtureDirectory, n + ".conf"), Path.Combine(lib, n + ".conf"));

        var seen = await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Machines.Count == 3, "the library");
            var item = Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as MachineViewModel)?.Name == "haiku");
            Ui.Click(window, item, Avalonia.Input.MouseButton.Right);
            Ui.Pump(0.3);
            var menu = item.GetVisualDescendants().OfType<Grid>().Select(g => g.ContextMenu).First(c => c is not null)!
                .Items.OfType<MenuItem>().First(m => (m.Header as string) == "Move to folder…");
            Assert.True(menu.IsEffectivelyVisible, "the context menu did not open");
            Ui.Click(window, menu);
            Ui.PumpUntil(() => shell.FolderPrompt is not null, "the folder prompt");
            Ui.Pump(0.2);
            var box = Named<TextBox>(window, "FolderName");
            box.Focus();
            window.KeyTextInput("Retro");
            Ui.Click(window, Named<Button>(window, "FolderMove"));
            Ui.PumpUntil(() => shell.FolderPrompt is null, "the prompt to close");
            Ui.Pump(0.3);
            Ui.Capture(window, "phase3", "folders");
            var order = shell.Machines.Select(m => $"{(m.ShowFolderHeader ? "[" + m.FolderHeader + "] " : "")}{m.Name}").ToList();
            window.Close();
            return order;
        });

        Assert.Equal(["freebsd", "linux", "[RETRO] haiku"], seen);
        Assert.Equal("Retro", VmQontrol.Core.AppSettings.Load(profile.Paths).Folders.Single().Value);
    }

    /// <summary>
    /// More → Clone…, choose Linked clone: the new machine is in the library at once, layered on a shared base. Deleting it
    /// keeps the base (the original still uses it); deleting the original then names the base and removes it too.
    /// </summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_linked_clone_made_from_the_menu_shares_a_base_that_goes_with_the_last_machine()
    {
        Assert.SkipWhen(!File.Exists("/usr/bin/qemu-io"), "qemu-io is not installed");
        using var profile = new TestProfile("linked-window");
        var lib = profile.Paths.DefaultLibrary;
        var dir = Path.Combine(lib, "Lab");
        Directory.CreateDirectory(dir);
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: TestContext.Current.CancellationToken);
        MachineStore.Save(new MachineConfig { Name = "Lab", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var bases = Path.Combine(lib, MachineLifecycle.BasesFolder);

        var seen = await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            void Pick(string name)
            {
                var item = Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as MachineViewModel)?.Name == name);
                Ui.Click(window, item);
                Ui.PumpUntil(() => shell.Selected?.Name == name, $"{name}'s page");
                Ui.Pump(0.3);
            }

            void Menu(string header)
            {
                var page = window.GetVisualDescendants().OfType<MachineView>().Single();
                var more = Named<Button>(page, "More");
                Ui.Click(window, more);
                Ui.Pump(0.3);
                var entry = ((MenuFlyout)more.Flyout!).Items.OfType<MenuItem>().First(m => (m.Header as string) == header);
                Assert.True(entry.IsEffectivelyVisible, $"{header} is not in the open menu");
                Ui.Click(window, entry);
                Ui.PumpUntil(() => shell.Prompt is not null, $"the {header} prompt");
                Ui.Pump(0.3);
            }

            void Confirm(string? typed)
            {
                if (typed is not null)
                {
                    Named<TextBox>(window, "PromptText").Focus();
                    window.KeyTextInput(typed);
                }

                Ui.Click(window, Named<Button>(window, "PromptConfirm"));
                Ui.PumpUntil(() => shell.Prompt is null, "the prompt to close", 30);
                Ui.Pump(0.5);
            }

            Pick("Lab");
            Menu("Clone…");
            var choices = Named<ItemsControl>(window, "PromptOptions").GetVisualDescendants().OfType<RadioButton>().ToList();
            Ui.Click(window, choices[1]);
            Ui.Pump(0.2);
            Ui.Capture(window, "phase6", "linked-clone-prompt");
            Confirm(null);
            Ui.PumpUntil(() => shell.Machines.Count == 2, "the clone in the library");
            Ui.Capture(window, "phase6", "linked-clone-made");
            var afterClone = (shell.Machines.Select(m => m.Name).Order().ToList(), Directory.GetFiles(bases).Length);

            Pick("Lab (clone)");
            Menu("Delete…");
            var cloneDeleteLines = string.Join("\n", shell.Prompt!.Lines);
            Confirm("Lab (clone)");
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the clone gone");
            var basesAfterCloneDeleted = Directory.GetFiles(bases).Length;

            Pick("Lab");
            Menu("Delete…");
            var originalDeleteLines = string.Join("\n", shell.Prompt!.Lines);
            Ui.Capture(window, "phase6", "delete-names-shared-disk");
            Confirm("Lab");
            Ui.PumpUntil(() => shell.Machines.Count == 0, "the original gone");
            window.Close();
            return (afterClone, cloneDeleteLines, basesAfterCloneDeleted, originalDeleteLines, Directory.GetFiles(bases).Length);
        });

        Assert.Equal(["Lab", "Lab (clone)"], seen.afterClone.Item1);
        Assert.Equal(1, seen.afterClone.Item2);
        Assert.DoesNotContain(".bases", seen.cloneDeleteLines, StringComparison.Ordinal);
        Assert.Equal(1, seen.basesAfterCloneDeleted);
        Assert.Contains("shared disks no other machine uses", seen.originalDeleteLines, StringComparison.Ordinal);
        Assert.Contains(".bases", seen.originalDeleteLines, StringComparison.Ordinal);
        Assert.Equal(0, seen.Item5);
    }

    /// <summary>A real machine, started headless and silent before the window opens: its page finds it running and shows its screen.</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_running_machine_shows_its_state_and_live_screen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("live");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Live");
        Directory.CreateDirectory(dir);
        var machine = new VmQontrol.Core.Machines.MachineConfig
        {
            Name = "Live",
            MemoryMiB = 256,
            Cpu = new VmQontrol.Core.Machines.CpuConfig { Cores = 1 },
            Firmware = new VmQontrol.Core.Machines.FirmwareConfig { Kind = VmQontrol.Core.Machines.FirmwareKind.Bios },
            Disks = [new VmQontrol.Core.Machines.DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
        };
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        VmQontrol.Core.Machines.MachineStore.Save(machine, Path.Combine(dir, "vm.json"));
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(machine, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, ct);
        try
        {
            var seen = await Ui.Run(() =>
            {
                var (window, shell) = Open(profile);
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                var item = Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First();
                Ui.Click(window, item);
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true, HasScreen: true }, "the running state and the first screen", 20);
                Ui.Pump(0.3);
                var page = window.GetVisualDescendants().OfType<MachineView>().Single();
                var state = Named<TextBlock>(page, "StateText").Text;
                var powerOnShown = Named<Button>(page, "PowerOn").IsVisible;
                var powerOffShown = Named<Button>(page, "PowerOff").IsVisible;
                Ui.Capture(window, "phase3", "machine-running");
                // NAT with the SSH forward (the default): the summary offers the terminal, never only a command to copy.
                Assert.True(Named<Button>(page, "OpenSsh").IsEffectivelyVisible, "Open in terminal is shown for a NAT machine");

                // The Performance tab: real samples from the running process within a few seconds.
                Ui.Click(window, Named<TabItem>(page, "PerformanceTab"));
                Ui.PumpUntil(() => shell.Selected!.Performance.CpuSeries.Count >= 3, "three performance samples", 15);
                Ui.Capture(window, "phase3", "performance");
                var memory = shell.Selected!.Performance.MemorySeries[^1];
                window.Close();
                return (state, powerOnShown, powerOffShown, memory);
            });
            Assert.Equal("Running", seen.state);
            Assert.True(seen.memory > 10, $"the machine's resident memory read {seen.memory} MB");
            Assert.False(seen.powerOnShown);
            Assert.True(seen.powerOffShown);
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    /// <summary>
    /// More → Make into a template: the page says Template, offers no Power on, and "New machine from this template"
    /// makes a linked machine (linked is chosen for a template) that is an ordinary machine.
    /// </summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_template_made_from_the_menu_makes_linked_machines()
    {
        Assert.SkipWhen(!File.Exists("/usr/bin/qemu-io"), "qemu-io is not installed");
        using var profile = new TestProfile("template-window");
        var lib = profile.Paths.DefaultLibrary;
        var dir = Path.Combine(lib, "Golden");
        Directory.CreateDirectory(dir);
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: TestContext.Current.CancellationToken);
        MachineStore.Save(new MachineConfig { Name = "Golden", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));

        var seen = await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected?.Name == "Golden", "the machine page");
            Ui.Pump(0.3);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            var more = Named<Button>(page, "More");
            Ui.Click(window, more);
            Ui.Pump(0.3);
            Ui.Click(window, ((MenuFlyout)more.Flyout!).Items.OfType<MenuItem>().First(m => (m.Header as string) == "Make into a template…"));
            Ui.PumpUntil(() => shell.Notice is not null, "the template question");
            Ui.Pump(0.2);
            Ui.Click(window, Named<Button>(window, "NoticeConfirm"));
            Ui.PumpUntil(() => shell.Selected is { IsTemplate: true }, "the template");
            Ui.Pump(0.3);
            var state = Named<TextBlock>(page, "StateText").Text;
            var powerOn = Named<Button>(page, "PowerOn").IsVisible;
            Ui.Capture(window, "phase6", "template");

            Ui.Click(window, Named<Button>(page, "NewFromTemplate"));
            Ui.PumpUntil(() => shell.Prompt is not null, "the new machine prompt");
            Ui.Pump(0.3);
            var linkedChosen = shell.Prompt!.Choice == 1;
            Ui.Capture(window, "phase6", "new-from-template");
            Ui.Click(window, Named<Button>(window, "PromptConfirm"));
            Ui.PumpUntil(() => shell.Machines.Count == 2, "the new machine", 30);
            Ui.Pump(0.3);
            window.Close();
            return (state, powerOn, linkedChosen);
        });

        Assert.Equal("Template", seen.state);
        Assert.False(seen.powerOn);
        Assert.True(seen.linkedChosen);
        var made = Path.Combine(lib, "Golden 1");
        Assert.False(MachineStore.Load(Path.Combine(made, "vm.json")).IsTemplate);
        Assert.NotNull(DiskState.BackingFile(Path.Combine(made, "disk0.qcow2")));
    }

    /// <summary>
    /// A USB device plugged in while a machine runs (the watcher's event, fed by hand): the machine in view is offered it
    /// with "Always connect it"; ticked and confirmed, the rule is saved and is in the running QEMU at once. A device of a
    /// kind that already has a rule is only reported (no question).
    /// </summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_device_plugged_in_while_running_is_offered_and_Always_makes_a_live_rule()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("usb-plugged");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Desk");
        Directory.CreateDirectory(dir);
        var machine = new MachineConfig
        {
            Name = "Desk", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }], Display = new DisplayConfig { Backend = DisplayBackend.None },
        };
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        MachineStore.Save(machine, Path.Combine(dir, "vm.json"));
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(machine, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, ct);
        // Devices no computer has, so QEMU's rule waits and nothing real is taken.
        var stick = new VmQontrol.Core.Devices.HostUsbDevice("ffff", "fffc", "Test Stick", "001", "099", "9-9", 480, true, false);
        var locked = stick with { ProductId = "fffb", Name = "Locked Pad", Writable = false };
        try
        {
            var seen = await Ui.Run(() =>
            {
                var (window, shell) = Open(profile);
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true, Watcher: not null }, "the running machine", 20);
                Ui.Pump(0.3);

                shell.UsbPlugged(locked);
                Ui.PumpUntil(() => shell.Notice is not null, "the plugged-in question", 10);
                var lockedTitle = shell.Notice?.ItemTitle;
                Ui.Capture(window, "phase7", "usb-plugged-locked");
                var lockedConfirm = shell.Notice?.ConfirmText;
                Ui.Click(window, Named<Button>(window, "NoticeDismiss"));
                Ui.PumpUntil(() => shell.Notice is null, "the notice to close");

                shell.UsbPlugged(stick);
                Ui.PumpUntil(() => shell.Notice is not null, "the plugged-in question", 10);
                var title = shell.Notice?.ItemTitle;
                var check = Named<CheckBox>(window, "NoticeCheck");
                var checkText = check.Content as string;
                Ui.Click(window, check);
                Ui.Pump(0.1);
                Ui.Capture(window, "phase7", "usb-plugged");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.2);
                Ui.Capture(window, "phase7", "usb-plugged-light");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                Ui.Pump(0.1);
                Ui.Click(window, Named<Button>(window, "NoticeConfirm"));
                Ui.PumpUntil(() => shell.Selected!.Machine.Usb.Passthrough.Count == 1, "the rule", 20);
                // Through the page's own QMP connection: QEMU's monitor serves one client at a time.
                var desk = shell.Selected!.Watcher!;
                var attached = Task.Run(() => VmQontrol.Core.Devices.UsbHost.AttachedAsync(desk, ct)).GetAwaiter().GetResult();

                // Plugged in again: a rule exists, so no question, only a line in the status bar.
                shell.UsbPlugged(stick);
                Ui.Pump(1);
                var askedAgain = shell.Notice is not null;
                var status = shell.Status;
                // The panel, for a look at its rows (this computer's real devices) with the new label.
                var page = window.GetVisualDescendants().OfType<MachineView>().Single();
                // The toolbar's USB menu: this computer's real devices, only looked at (never clicked), then Manage….
                var usbButton = Named<Button>(page, "OpenUsb");
                var usbMenu = (MenuFlyout)usbButton.Flyout!;
                Ui.Click(window, usbButton);
                Ui.PumpUntil(() => usbMenu.Items.OfType<MenuItem>().Any(m => m.Name == "ManageUsb"), "the USB menu", 10);
                Ui.Pump(0.3);
                usbMenu.Items.OfType<MenuItem>().First(m => m.Name == "ManageUsb").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                usbMenu.Hide();
                Ui.PumpUntil(() => shell.UsbPanel is { } p && (p.Devices.Count > 0 || p.Note.Length > 0), "the USB panel", 10);
                Ui.Pump(0.3);
                Ui.Capture(window, "phase7", "usb-panel");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.2);
                Ui.Capture(window, "phase7", "usb-panel-light");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                if (shell.UsbPanel!.Devices.FirstOrDefault(r => r.ShowAllow) is { } lockedRow)
                {
                    lockedRow.AllowCommand.Execute(null);
                    Ui.Pump(0.3);
                    Ui.Capture(window, "phase7", "usb-allow");
                    Ui.Click(window, Named<Button>(window, "NoticeDismiss"));
                }
                window.Close();
                return (lockedTitle, lockedConfirm, title, checkText, attached, askedAgain, status);
            });

            Assert.Equal("Locked Pad", seen.lockedTitle);
            // With spice-gtk's helper the device is offered like any other (Connect opens it to this account); without
            // it, the one notice names the package to install.
            Assert.Equal(VmQontrol.Core.Devices.UsbAccess.Available ? "Connect to Desk" : "Copy the install command", seen.lockedConfirm);
            Assert.Equal("Test Stick", seen.title);
            Assert.Equal("Always connect it to Desk", seen.checkText);
            Assert.Contains("usbauto-ffff-fffc", seen.attached);
            Assert.False(seen.askedAgain);
            Assert.Contains("always connects there", seen.status, StringComparison.Ordinal);
            var saved = MachineStore.Load(Path.Combine(dir, "vm.json"));
            Assert.Equal(new UsbDevice("ffff", "fffc", "Test Stick"), Assert.Single(saved.Usb.Passthrough));
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    /// <summary>
    /// More → Power on in the background: the machine runs with its screen ready but no viewer opens. The viewer is a
    /// stand-in script that leaves a mark when started; the control is Open screen afterwards, which does start it.
    /// </summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task Power_on_in_the_background_opens_no_viewer_until_Open_screen()
    {
        using var profile = new TestProfile("background-start");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Quiet");
        Directory.CreateDirectory(dir);
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: TestContext.Current.CancellationToken);
        MachineStore.Save(new MachineConfig
        {
            Name = "Quiet", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Display = new DisplayConfig { Backend = DisplayBackend.Spice, Gl = false },
        }, Path.Combine(dir, "vm.json"));
        var marks = Path.Combine(profile.Root, "viewer-starts");
        var viewer = Path.Combine(profile.Root, "stand-in-viewer");
        File.WriteAllText(viewer, $"#!/bin/sh\necho \"$@\" >> '{marks}'\n");
        File.SetUnixFileMode(viewer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths) with { Viewer = viewer });
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected?.Name == "Quiet", "the machine page");
            Ui.Pump(0.3);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            var more = Named<Button>(page, "More");
            Ui.Click(window, more);
            Ui.Pump(0.3);
            Ui.Click(window, ((MenuFlyout)more.Flyout!).Items.OfType<MenuItem>().First(m => m.Name == "PowerOnBackground"));
            Ui.PumpUntil(() => shell.Selected is { IsRunning: true } || shell.Notice is not null, "the machine running", 30);
            var notice = shell.Notice is { } n ? n.Title + ": " + string.Join(" ", n.Lines) : null;
            Ui.Pump(2);
            var openedInBackground = File.Exists(marks);
            var status = shell.Status;
            Ui.Click(window, Named<Button>(page, "OpenScreen"));
            Ui.PumpUntil(() => File.Exists(marks), "the viewer after Open screen", 10);
            window.Close();
            return (notice, openedInBackground, status);
        });

        try
        {
            Assert.Null(seen.notice);
            Assert.False(seen.openedInBackground);
            Assert.Contains("in the background", seen.status, StringComparison.Ordinal);
            Assert.Contains("spice+unix://", File.ReadAllText(marks), StringComparison.Ordinal);
        }
        finally
        {
            var entry = VmQontrol.Core.Library.LibraryScanner.Scan(profile.Paths.DefaultLibrary, profile.Paths.State).Single();
            if (VmQontrol.Core.Runtime.MachineRunner.Find(entry.Machine!, profile.Paths) is { } running)
            {
                await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
                await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// The toolbar's USB menu as it draws: sample devices (never this computer's), one in the machine (ticked), in both
    /// themes, in a real menu presenter so the app's menu style applies. Checks the ticks and the Manage item.
    /// </summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_USB_menu_ticks_the_devices_in_the_machine()
    {
        var seen = await Ui.Run(() =>
        {
            VmQontrol.Core.Devices.HostUsbDevice D(string v, string p, string name, int speed) => new(v, p, name, "3", "4", "3-1", speed, true, false);
            var devices = new List<(VmQontrol.Core.Devices.HostUsbDevice, bool)>
            {
                (D("1b1c", "1bb5", "CORSAIR M65 RGB ULTRA WIRELESS Gaming Mouse", 12), true),
                (D("0781", "5583", "SanDisk Ultra Fit", 5000), false),
                (D("046d", "085e", "Logitech BRIO", 5000), false),
            };
            var ticked = new List<string>();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = theme;
                var presenter = new MenuFlyoutPresenter { ItemsSource = VmQontrol.App.Views.MachineView.UsbMenuItems(devices, true, (_, _) => { }, () => { }) };
                var window = new Window { Content = new Border { Padding = new Avalonia.Thickness(24), Child = presenter }, Width = 520, Height = 300, SizeToContent = SizeToContent.Height };
                window.Show();
                Ui.Pump(0.3);
                Ui.Capture(window, "phase7", $"usb-menu-{(theme == ThemeVariant.Dark ? "dark" : "light")}");
                ticked = presenter.Items.OfType<MenuItem>().Where(m => m.IsChecked).Select(m => (string)m.Tag!).ToList();
                window.Close();
            }

            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            return ticked;
        });

        Assert.Equal(["1b1c:1bb5"], seen);
    }

    /// <summary>
    /// Suspend on a running machine's page: it turns off as "Suspended", Power on reads "Resume", and Resume brings it back
    /// running with the saved state used up.
    /// </summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task Suspend_turns_the_machine_off_as_suspended_and_Resume_brings_it_back()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("suspend-window");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Nap");
        Directory.CreateDirectory(dir);
        var machine = new MachineConfig
        {
            Name = "Nap",
            MemoryMiB = 256,
            Cpu = new CpuConfig { Cores = 1 },
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Display = new DisplayConfig { Backend = DisplayBackend.None },
        };
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        MachineStore.Save(machine, Path.Combine(dir, "vm.json"));
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(machine, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, ct);
        try
        {
            var seen = await Ui.Run(() =>
            {
                var (window, shell) = Open(profile);
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true }, "the running state", 20);
                Ui.Pump(0.3);
                var page = window.GetVisualDescendants().OfType<MachineView>().Single();
                Ui.Click(window, Named<Button>(page, "Suspend"));
                Ui.PumpUntil(() => shell.Selected is { IsOff: true, IsSuspended: true }, "the machine suspended", 30);
                Ui.Pump(0.3);
                var suspendedState = Named<TextBlock>(page, "StateText").Text;
                var powerOnText = string.Join("", Named<Button>(page, "PowerOn").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                var file = File.Exists(VmQontrol.Core.Runtime.MachineSuspend.StatePath(dir));
                Ui.Capture(window, "phase6", "suspended");

                Ui.Click(window, Named<Button>(page, "PowerOn"));
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true, IsSuspended: false } || shell.Notice is not null, "the machine resumed", 30);
                Assert.True(shell.Notice is null, shell.Notice is { } n ? n.Title + ": " + string.Join(" ", n.Lines) : "");
                Ui.Pump(0.3);
                var resumedState = Named<TextBlock>(page, "StateText").Text;
                window.Close();
                return (suspendedState, powerOnText, file, resumedState);
            });
            Assert.Equal("Suspended", seen.suspendedState);
            Assert.Equal("Resume", seen.powerOnText);
            Assert.True(seen.file);
            Assert.Equal("Running", seen.resumedState);
            Assert.False(File.Exists(VmQontrol.Core.Runtime.MachineSuspend.StatePath(dir)));
        }
        finally
        {
            if (VmQontrol.Core.Runtime.MachineRunner.Find(machine, profile.Paths) is { } still)
            {
                await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(still, CancellationToken.None);
                await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(still, TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }
    }

    /// <summary>Alpine's cloud image logs to its serial port: the Console tab shows its boot and login prompt.</summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task The_console_tab_shows_what_the_guest_prints_on_its_serial_port()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", "nocloud_alpine-3.22.4-x86_64-bios-cloudinit-r0.qcow2");
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("console");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Alpine");
        Directory.CreateDirectory(dir);
        await VmQontrol.Core.Runtime.QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        // A minimal seed, so cloud-init finds its data source at once instead of searching for minutes.
        VmQontrol.Core.Media.IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-console-test\n"u8.ToArray(),
            ["user-data"] = "#cloud-config\n"u8.ToArray(),
        });
        var machine = new VmQontrol.Core.Machines.MachineConfig
        {
            Media = [new VmQontrol.Core.Machines.MediaConfig { Role = VmQontrol.Core.Machines.MediaRole.Drivers, File = "seed.iso" }],
            Name = "Alpine",
            MemoryMiB = 512,
            Cpu = new VmQontrol.Core.Machines.CpuConfig { Cores = 1 },
            Firmware = new VmQontrol.Core.Machines.FirmwareConfig { Kind = VmQontrol.Core.Machines.FirmwareKind.Bios },
            Disks = [new VmQontrol.Core.Machines.DiskConfig { File = "disk0.qcow2" }],
        };
        VmQontrol.Core.Machines.MachineStore.Save(machine, Path.Combine(dir, "vm.json"));
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(machine, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, ct);
        try
        {
            var text = await Ui.Run(() =>
            {
                var (window, shell) = Open(profile);
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true }, "the running state");
                Ui.Pump(0.3);
                var page = window.GetVisualDescendants().OfType<MachineView>().Single();
                Ui.Click(window, Named<TabItem>(page, "ConsoleTab"));
                var connectedAt = DateTime.UtcNow;
                try
                {
                    Ui.PumpUntil(() => shell.Selected!.Console.Text.Contains("login:", StringComparison.Ordinal), "the guest's login prompt on the console", 90);
                }
                catch (Xunit.Sdk.XunitException)
                {
                    var tail = shell.Selected!.Console.Text;
                    throw new Xunit.Sdk.XunitException($"No login prompt after 90 s. Connected: {shell.Selected.Console.Connected}; note: {shell.Selected.Console.Note}; last output:\n{tail[Math.Max(0, tail.Length - 1500)..]}");
                }

                TestContext.Current.SendDiagnosticMessage($"login prompt {(DateTime.UtcNow - connectedAt).TotalSeconds:0.0} s after the console tab opened");
                Ui.Capture(window, "phase3", "console");
                var result = shell.Selected!.Console.Text;
                window.Close();
                return result;
            });
            Assert.Contains("login:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\x1B[", text, StringComparison.Ordinal);
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    /// <summary>A running machine: Take snapshot from its Snapshots tab, see it in the tree with its picture, go back to it.</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_snapshot_taken_from_the_tab_shows_in_the_tree_and_can_be_returned_to()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("snaptab");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Snap");
        Directory.CreateDirectory(dir);
        var machine = new VmQontrol.Core.Machines.MachineConfig
        {
            Name = "Snap",
            MemoryMiB = 256,
            Cpu = new VmQontrol.Core.Machines.CpuConfig { Cores = 1 },
            Firmware = new VmQontrol.Core.Machines.FirmwareConfig { Kind = VmQontrol.Core.Machines.FirmwareKind.Bios },
            Disks = [new VmQontrol.Core.Machines.DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
        };
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        VmQontrol.Core.Machines.MachineStore.Save(machine, Path.Combine(dir, "vm.json"));
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(machine, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, ct);
        try
        {
            var seen = await Ui.Run(() =>
            {
                var (window, shell) = Open(profile);
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true }, "the running state", 20);
                Ui.Pump(0.3);
                var page = window.GetVisualDescendants().OfType<MachineView>().Single();
                Ui.Click(window, Named<TabItem>(page, "SnapshotsTab"));
                Ui.PumpUntil(() => shell.Selected!.Snapshots.NewName.Length > 0, "the snapshot list");
                shell.Selected!.Snapshots.NewName = "Clean firmware";
                Ui.Click(window, Named<Button>(page, "TakeSnapshot"));
                Ui.PumpUntil(() => shell.Selected.Snapshots.Rows.Count == 1, "the snapshot in the tree", 30);
                Ui.Pump(0.3);
                Ui.Capture(window, "phase6", "snapshots-tab");
                var row = shell.Selected.Snapshots.Rows[0];
                shell.Selected.Snapshots.RestoreCommand.Execute(row);
                Ui.PumpUntil(() => shell.Notice is not null, "the confirmation");
                Ui.Pump(0.2); // laid out before it is clicked
                Ui.Click(window, Named<Button>(window, "NoticeConfirm"));
                try
                {
                    Ui.PumpUntil(() => shell.Selected.Snapshots.Note.StartsWith("Back at", StringComparison.Ordinal), "the restore", 30);
                }
                catch (Xunit.Sdk.XunitException)
                {
                    throw new Xunit.Sdk.XunitException($"No restore. Note: {shell.Selected.Snapshots.Note}; notice: {shell.Notice?.Title} {string.Join(" ", shell.Notice?.Lines ?? [])}");
                }
                var result = (row.Name, row.IsCurrent, row.Picture is not null, row.Snapshot.WithMemory);
                window.Close();
                return result;
            });
            Assert.Equal("Clean firmware", seen.Name);
            Assert.True(seen.IsCurrent);
            Assert.True(seen.Item3, "the snapshot has no picture");
            Assert.True(seen.WithMemory);
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await VmQontrol.Core.Runtime.MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
