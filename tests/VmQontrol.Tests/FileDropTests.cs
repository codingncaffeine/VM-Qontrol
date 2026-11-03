using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 19 item 5 (s11): files dropped on the window. An installer disc starts the wizard with it, a disk or a machine
/// from other software opens Import with it read, anything else is named and refused. Walked with the platform's own
/// drag-enter, drag-over and drop events.
/// </summary>
public sealed class FileDropTests
{
    private static string MakeDisc(string path)
    {
        IsoWriter.Write(path, "TESTDISC", new Dictionary<string, byte[]> { ["README.TXT"] = "x"u8.ToArray() });
        return path;
    }

    [Fact]
    public void A_dropped_file_is_sorted_by_what_it_is()
    {
        using var profile = new TestProfile("drop-kind");
        var iso = MakeDisc(Path.Combine(profile.Root, "installer.iso"));
        var hybrid = MakeDisc(Path.Combine(profile.Root, "hybrid.img"));
        var raw = Path.Combine(profile.Root, "disk.img");
        File.WriteAllBytes(raw, new byte[1 << 20]);
        var vmx = Path.Combine(profile.Root, "Win.vmx");
        File.WriteAllText(vmx, "config.version = \"8\"\n");
        var text = Path.Combine(profile.Root, "notes.txt");
        File.WriteAllText(text, "hello");

        Assert.Equal(DropKind.Installer, DroppedFile.Classify(iso));
        Assert.Equal(DropKind.Installer, DroppedFile.Classify(hybrid)); // a .img holding a disc volume is an installer
        Assert.Equal(DropKind.Import, DroppedFile.Classify(raw));       // control: any other .img is a disk
        Assert.Equal(DropKind.Import, DroppedFile.Classify(vmx));
        Assert.Equal(DropKind.None, DroppedFile.Classify(text));
        Assert.Equal(DropKind.None, DroppedFile.Classify(Path.Combine(profile.Root, "gone.iso")));
    }

    private static (string? hint, bool shown) Drag(Window window, string file, bool drop)
    {
        var at = new Point(window.Bounds.Width * 0.6, window.Bounds.Height * 0.5);
        // The file as the desktop hands it over: a storage item of this computer, from the platform's own provider.
        var item = window.StorageProvider.TryGetFileFromPathAsync(file);
        Ui.PumpUntil(() => item.IsCompleted, "the file's storage item");
        Assert.NotNull(item.Result);
        using var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile(item.Result));
        window.DragDrop(at, RawDragEventType.DragEnter, transfer, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.DragOver, transfer, DragDropEffects.Copy, RawInputModifiers.None);
        Ui.Pump(0.2);
        var shell = (ShellViewModel)window.DataContext!;
        var seen = (shell.DropHint, window.FindControl<Panel>("DropLayer")!.IsEffectivelyVisible);
        if (drop) window.DragDrop(at, RawDragEventType.Drop, transfer, DragDropEffects.Copy, RawInputModifiers.None);
        Ui.Pump(0.3);
        return seen;
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_disc_dropped_on_the_window_starts_the_wizard_and_a_disk_opens_import()
    {
        using var profile = new TestProfile("drop-window");
        var iso = MakeDisc(Path.Combine(profile.Root, "ubuntu-24.04.iso"));
        var disk = Path.Combine(profile.Root, "server.qcow2");
        await VmQontrol.Core.Runtime.QemuImg.CreateAsync(disk, "qcow2", 1, ct: TestContext.Current.CancellationToken);
        var text = Path.Combine(profile.Root, "notes.txt");
        File.WriteAllText(text, "hello");

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 860 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.Pump(0.4);

            // Something VM Qontrol does not open: no hint, no page, a note saying what it takes.
            var refused = Drag(window, text, drop: true);
            Ui.PumpUntil(() => shell.Status.Contains("not something VM Qontrol opens", StringComparison.Ordinal), "the refusal note");
            var nothingOpened = shell.Wizard is null && shell.ImportPage is null;

            // A disc: the hint while dragging, then the wizard with it chosen.
            var window1 = Drag(window, iso, drop: false);
            Ui.Capture(window, "s11", "drop-disc-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.2);
            Ui.Capture(window, "s11", "drop-disc-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            Drag(window, iso, drop: true);
            Ui.PumpUntil(() => shell.Wizard is { IsoPath: not null, Detecting: false }, "the wizard with the disc");
            var wizardIso = shell.Wizard!.IsoPath;
            var hintGone = shell.DropHint is null;
            shell.Wizard.CancelCommand.Execute(null);
            Ui.PumpUntil(() => shell.Wizard is null, "the wizard closed");

            // A disk: Import, with it read.
            var diskHint = Drag(window, disk, drop: true);
            Ui.PumpUntil(() => shell.ImportPage is { Reviewing: true }, "the import review");
            var bare = shell.ImportPage!.IsBareDisk;
            window.Close();
            return (refused, nothingOpened, window1, wizardIso, hintGone, diskHint, bare);
        });

        Assert.Null(seen.refused.hint);
        Assert.False(seen.refused.shown);
        Assert.True(seen.nothingOpened);
        Assert.Equal("Drop to make a new machine from ubuntu-24.04.iso", seen.window1.hint);
        Assert.True(seen.window1.shown);
        Assert.Equal(iso, seen.wizardIso);
        Assert.True(seen.hintGone);
        Assert.Equal("Drop to import server.qcow2", seen.diskHint.hint);
        Assert.True(seen.bare);
    }
}
