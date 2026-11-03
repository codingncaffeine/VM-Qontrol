using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// Import walked as a person does: the welcome page's "Import a machine…", the machine read (the file the system's
/// dialog would return), the review page with every guess in words, Import, and the copy selected in the library.
/// </summary>
public sealed class ImportWindowTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_VMware_machine_is_reviewed_then_imported_into_the_library()
    {
        using var profile = new TestProfile("import-window");
        var src = Path.Combine(profile.Root, "Dev Box");
        Directory.CreateDirectory(src);
        await QemuImg.RunAsync(TestContext.Current.CancellationToken, "create", "-q", "-f", "vmdk", Path.Combine(src, "Dev Box.vmdk"), "64M");
        File.WriteAllText(Path.Combine(src, "Dev Box.vmx"), """
            displayName = "Dev Box"
            guestOS = "ubuntu-64"
            memsize = "4096"
            numvcpus = "2"
            firmware = "efi"
            scsi0.present = "TRUE"
            scsi0.virtualDev = "pvscsi"
            scsi0:0.present = "TRUE"
            scsi0:0.fileName = "Dev Box.vmdk"
            ethernet0.present = "TRUE"
            ethernet0.virtualDev = "vmxnet3"
            ethernet0.connectionType = "nat"
            ethernet0.generatedAddress = "00:0c:29:11:22:33"
            vmci0.present = "TRUE"
            """);

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.Pump(0.5);
            var welcome = window.GetVisualDescendants().OfType<WelcomeView>().Single();
            Ui.Click(window, welcome.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "OpenImport"));
            Ui.PumpUntil(() => shell.ImportPage is not null && shell.ShowImport, "the import page");
            Ui.Pump(0.3);
            Ui.Capture(window, "s7", "import-choose-dark");
            var page = shell.ImportPage!;
            _ = page.ReadAsync(Path.Combine(src, "Dev Box.vmx"));
            Ui.PumpUntil(() => page.Reviewing, "the machine to be read");
            Ui.Pump(0.4);
            var view = window.GetVisualDescendants().OfType<ImportView>().Single();
            var rows = page.Rows.ToDictionary(r => r.Label, r => r.Value);
            var notes = page.Machine!.Notes.ToList();
            Ui.Capture(window, "s7", "import-review-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s7", "import-review-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            Ui.Click(window, view.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "DoImport"));
            Ui.PumpUntil(() => shell.ImportPage is null && shell.Selected is { Name: "Dev Box" }, "the imported machine, selected", 30);
            Ui.Pump(0.3);
            Ui.Capture(window, "s7", "import-done-dark");
            var selected = shell.Selected!;
            window.Close();
            return (rows, notes, Folder: selected.Folder);
        });

        Assert.Equal("4 GB", seen.rows["Memory"]);
        Assert.Contains("VMware paravirtual SCSI", seen.rows["Disk"], StringComparison.Ordinal);
        Assert.Contains("vmxnet3", seen.rows["Network"], StringComparison.Ordinal);
        Assert.Contains("never changed", seen.rows["Original"], StringComparison.Ordinal);
        Assert.Contains(seen.notes, n => n.Contains("VMware Tools", StringComparison.Ordinal));
        var saved = MachineStore.Load(Path.Combine(seen.Folder, MachineStore.FileName));
        Assert.Equal((DiskBus.Pvscsi, "vmxnet3", 4096L), (saved.Disks[0].Bus, saved.Nics[0].Model!, saved.MemoryMiB!.Value));
        Assert.True(File.Exists(Path.Combine(src, "Dev Box.vmdk")));
    }

    /// <summary>⋯ → Export → As one file, then Import a machine with that file in another library: the machine comes back.</summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task An_exported_file_comes_back_through_Import()
    {
        using var profile = new TestProfile("export-window");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Roamer");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(TestContext.Current.CancellationToken, "create", "-q", "-f", "qcow2", Path.Combine(dir, "disk0.qcow2"), "64M");
        MachineStore.Save(new MachineConfig { Name = "Roamer", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, MachineStore.FileName));
        var drive = Path.Combine(profile.Root, "drive");
        Directory.CreateDirectory(drive);

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, window.GetLogicalDescendants().OfType<ListBox>().First(l => l.Name == "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Pump(0.3);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            Ui.Click(window, page.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "More"));
            MenuItem? export = null;
            Ui.PumpUntil(() => (export = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault()?.GetRealizedContainers().OfType<MenuItem>().FirstOrDefault(m => m.Name == "ExportMenu")) is not null, "the Export item in the ⋯ menu");
            var drawn = export!.IsEffectivelyVisible && export.Bounds.Height > 10;
            window.Close();
            return (drawn, Machine: shell.Selected!);
        });
        Assert.True(seen.drawn);

        // What the item does once the folder dialog returns the drive.
        await seen.Machine.ExportAsync(drive, asTar: true);
        var tar = Path.Combine(drive, "Roamer.tar");
        Assert.True(File.Exists(tar));

        using var other = new TestProfile("export-window-other");
        var back = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(other.Paths, AppSettings.Load(other.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            window.Show();
            _ = shell.StartAsync();
            Ui.Pump(0.3);
            shell.OpenImport();
            _ = shell.ImportPage!.ReadAsync(tar);
            Ui.PumpUntil(() => shell.Selected is { Name: "Roamer" }, "the machine brought back", 30);
            var folder = shell.Selected!.Folder;
            window.Close();
            return folder;
        });
        Assert.Equal(Path.Combine(other.Paths.DefaultLibrary, "Roamer"), back);
    }
}
