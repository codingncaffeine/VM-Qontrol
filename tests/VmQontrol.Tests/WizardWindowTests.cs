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

    /// <summary>
    /// "From a template" on the first page: the system and hardware pages are skipped (the template decides them), the
    /// summary names the template and the kind of copy, and the machine made is an ordinary machine layered on the
    /// template's frozen disk. The control: before the template exists, the choice is disabled.
    /// </summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_template_is_a_source_on_the_first_page_and_makes_a_linked_machine()
    {
        using var profile = new TestProfile("wizard-template");
        var lib = profile.Paths.DefaultLibrary;
        var dir = Path.Combine(lib, "Golden");
        Directory.CreateDirectory(dir);
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: TestContext.Current.CancellationToken);
        var golden = new MachineConfig { Name = "Golden", MemoryMiB = 3072, Guest = new GuestConfig { Os = "linux" }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        MachineStore.Save(golden, Path.Combine(dir, "vm.json"));

        var seen = await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Welcome.Checked && shell.Machines.Count == 1, "the library");
            // The control: an ordinary machine is not offered.
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            Ui.PumpUntil(() => shell.Wizard is not null, "the wizard");
            Ui.Pump(0.2);
            var enabledWithout = Named<RadioButton>(window, "SourceTemplate").IsEnabled;
            shell.Wizard!.CancelCommand.Execute(null);
            Ui.PumpUntil(() => shell.Wizard is null, "the wizard to close");

            MachineLifecycle.SetTemplate(golden, dir, true);
            _ = shell.AfterLifecycleAsync(null);
            Ui.PumpUntil(() => shell.Templates.Count == 1, "the template");
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            Ui.PumpUntil(() => shell.Wizard is not null, "the wizard");
            var wizard = shell.Wizard!;
            Ui.Pump(0.2);
            var source = Named<RadioButton>(window, "SourceTemplate");
            var enabledWith = source.IsEnabled;
            Ui.Click(window, source);
            Ui.Pump(0.3);
            var chosen = wizard.Template?.Name;
            var linkedChosen = Named<RadioButton>(window, "LinkedCopy").IsChecked == true;
            Ui.Capture(window, "wizard-template", "0-installer");
            var steps = new List<int>();
            for (var guard = 0; wizard.Step < 5 && guard < 8; guard++)
            {
                Ui.Click(window, Named<Button>(window, "Next"));
                Ui.Pump(0.2);
                steps.Add(wizard.Step);
            }

            Ui.Capture(window, "wizard-template", "5-summary");
            var summary = wizard.Summary.ToDictionary(r => r.Label, r => r.Value);
            // Back from the summary returns to the name, past the hardware page the template decides.
            Ui.Click(window, Named<Button>(window, "Back"));
            Ui.Pump(0.2);
            var backTo = wizard.Step;
            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.Pump(0.2);
            wizard.PowerOnAfter = false;
            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.PumpUntil(() => shell.Wizard is null && shell.Selected is not null && shell.Selected.Name != "Golden", "the new machine", 30);
            var folder = shell.Selected!.Folder;
            window.Close();
            return (enabledWithout, enabledWith, chosen, linkedChosen, steps, summary, backTo, folder);
        });

        Assert.False(seen.enabledWithout);
        Assert.True(seen.enabledWith);
        Assert.Equal("Golden", seen.chosen);
        Assert.True(seen.linkedChosen);
        Assert.Equal([3, 5], seen.steps);
        Assert.Equal(3, seen.backTo);
        Assert.Equal("Golden", seen.summary["Template"]);
        Assert.StartsWith("Linked", seen.summary["Copy"], StringComparison.Ordinal);
        Assert.Contains("3 GB memory", seen.summary["Hardware"], StringComparison.Ordinal);
        Assert.Equal(Path.Combine(lib, "Golden 1"), seen.folder);
        var made = MachineStore.Load(Path.Combine(seen.folder, "vm.json"));
        Assert.False(made.IsTemplate);
        Assert.Equal(3072, made.MemoryMiB);
        Assert.NotNull(DiskState.BackingFile(Path.Combine(seen.folder, "disk0.qcow2")));
        // The template stays a template, now reading from its own layer on the same base.
        var template = MachineStore.Load(Path.Combine(dir, "vm.json"));
        Assert.True(template.IsTemplate);
        Assert.NotNull(DiskState.BackingFile(Path.Combine(dir, template.Disks[0].File)));
    }

    /// <summary>
    /// Download → Windows → 11: Microsoft's catalogue (a cached copy, no network) gives the editions on the card, Pro
    /// chosen, and Easy Install is offered for the download as for a disc. The download itself is proven by
    /// tools/verify-windows-download.sh (4.4 GB).
    /// </summary>
    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task Windows_from_microsoft_shows_its_editions_and_offers_easy_install()
    {
        using var profile = new TestProfile("wizard-mswin");
        Directory.CreateDirectory(profile.Paths.DefaultLibrary);
        Directory.CreateDirectory(profile.Paths.Cache);
        File.WriteAllText(Path.Combine(profile.Paths.Cache, "windows-11-products.xml"), WindowsDownloadTests.Catalogue());

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile);
            Ui.PumpUntil(() => shell.Welcome.Checked, "the host check");
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            Ui.PumpUntil(() => shell.Wizard is not null, "the wizard");
            var wizard = shell.Wizard!;
            wizard.Locale = "en-US";
            Ui.Click(window, Named<RadioButton>(window, "SourceDownload"));
            Ui.PumpUntil(() => wizard.Systems.Any(s => s.Os == "windows"), "the catalogue", 60);
            Assert.Equal("windows", wizard.Systems[0].Os); // the most asked-for download comes first
            var windows = wizard.Systems.Single(s => s.Os == "windows");
            Assert.Equal(["11", "10"], windows.Entries.Select(e => e.Release));
            Assert.All(windows.Entries, e => Assert.True(e.FromMicrosoft));
            Named<ListBox>(window, "CatalogueSystems").SelectedItem = windows;
            Ui.PumpUntil(() => wizard.Editions.Count > 0, "Microsoft's editions", 10);
            Ui.Pump(0.3);
            Assert.True(Named<StackPanel>(window, "MicrosoftWindows").IsEffectivelyVisible);
            Assert.Equal("Windows 11 Pro", wizard.Edition?.Name);
            Assert.Equal(["Windows 11 Pro", "Windows 11 Home", "Windows 11 Enterprise"], wizard.Editions.Select(e => e.Name));
            Assert.Contains("26100.4349", wizard.WindowsDownloadNote);
            Assert.Contains("English (United States)", wizard.WindowsDownloadNote);
            Assert.True(wizard.ConsumerDisc);
            Assert.True(wizard.EasyInstallPossible);
            Assert.Equal("Windows 11 Pro", wizard.Name);
            Ui.Capture(window, "wizard-mswin", "1-download-card");

            // Enterprise comes from the business file: volume-licence keys.
            wizard.Edition = wizard.Editions.Single(e => e.Name == "Windows 11 Enterprise");
            Assert.False(wizard.ConsumerDisc);
            wizard.Edition = wizard.Editions.Single(e => e.Name == "Windows 11 Pro");

            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.Pump(0.2);
            Ui.Click(window, Named<Button>(window, "Next"));
            Ui.Pump(0.3);
            Assert.Equal(2, wizard.Step); // Easy Install is asked, not skipped
            Assert.True(wizard.EasyInstallShown);
            Assert.False(wizard.NeedsOwnKey); // Pro has a published setup key
            Assert.Equal("Install Windows for you?", wizard.EasyInstallTitle);
            // The Windows answers are on the page: the system became Windows after the page was built.
            var page = window.GetLogicalDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.Contains(page, c => ReferenceEquals(c.ItemsSource, wizard.TimeZones));
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible && t.PlaceholderText?.StartsWith("Optional: XXXXX", StringComparison.Ordinal) == true);
            Ui.Capture(window, "wizard-mswin", "2-easy-install");

            // Back to a disc: the download's editions do not leak into the disc card.
            wizard.Step = 0;
            Ui.Click(window, Named<RadioButton>(window, "SourceDisc"));
            Ui.Pump(0.2);
            Assert.False(wizard.IsMicrosoftWindows);
            Ui.Capture(window, "wizard-mswin", "3-back-to-disc");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Click(window, Named<RadioButton>(window, "SourceDownload"));
            Ui.PumpUntil(() => wizard.Editions.Count > 0, "the editions again", 10);
            Ui.Pump(0.3);
            Ui.Capture(window, "wizard-mswin", "4-download-card-light");
            window.Close();
            return true;
        });
    }
}
