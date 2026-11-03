using Avalonia.Controls;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Parity audit (s9): a machine moves to another folder or drive, and a linked disk keeps its base.</summary>
public sealed class MachineMoveTests
{
    private static async Task<(MachineConfig Machine, string Directory, string Lib)> Linked(TestProfile profile, CancellationToken ct)
    {
        var lib = profile.Paths.DefaultLibrary;
        var dir = Path.Combine(lib, "Original");
        Directory.CreateDirectory(dir);
        var raw = Path.Combine(dir, "seed.raw");
        var data = new byte[1024 * 1024];
        new Random(3).NextBytes(data);
        await using (var f = File.Create(raw)) { f.Write(data); f.SetLength(8L * 1024 * 1024); }
        await QemuImg.RunAsync(ct, "convert", "-q", "-f", "raw", "-O", "qcow2", raw, Path.Combine(dir, "disk0.qcow2"));
        File.Delete(raw);
        var m = new MachineConfig { Name = "Original", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        MachineStore.Save(m, Path.Combine(dir, "vm.json"));
        var (clone, cloneDir, _) = await MachineLifecycle.LinkedCloneAsync(m, dir, lib, "Linked", ct: ct);
        return (clone, cloneDir, lib);
    }

    private static async Task Moves(string targetParent, string what)
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("move-" + what);
        var (clone, dir, lib) = await Linked(profile, ct);
        var disk = Path.Combine(dir, "disk0.qcow2");
        var baseFile = DiskState.BackingFile(disk)!;
        var flat = Path.Combine(profile.Root, "flat.qcow2");
        await QemuImg.RunAsync(ct, "convert", "-q", "-O", "qcow2", disk, flat);
        Directory.CreateDirectory(targetParent);
        try
        {
            Assert.Null(MachineMove.Refusal(dir, targetParent, running: false, quickemu: false));
            var moved = await MachineMove.MoveAsync(clone, dir, targetParent, ct);
            Assert.Equal(Path.Combine(targetParent, "Linked"), moved);
            Assert.False(Directory.Exists(dir));
            var movedDisk = Path.Combine(moved, "disk0.qcow2");
            Assert.Equal(baseFile, DiskState.BackingFile(movedDisk));
            Assert.False(Path.IsPathRooted((await QemuImg.InfoAsync(movedDisk, ct))["backing-filename"]!.GetValue<string>()), "the base is named relative, as D21 keeps it");
            await QemuImg.RunAsync(ct, "compare", "-q", flat, movedDisk);
            Assert.Equal("Linked", MachineStore.Load(Path.Combine(moved, "vm.json")).Name);
            // The base is still in use: deleting nothing from the library's bases folder.
            Assert.Empty(MachineLifecycle.PruneBases([lib, targetParent]));
        }
        finally
        {
            if (Directory.Exists(targetParent)) Directory.Delete(targetParent, recursive: true);
        }
    }

    [Trait("Tier", "Integration")]
    [Fact]
    public Task A_linked_machine_moves_to_another_folder_and_keeps_its_base() =>
        Moves(Path.Combine(TestProfile.ScratchRoot, "test-profiles", "move-target-" + Guid.NewGuid().ToString("N")[..8]), "folder");

    [Trait("Tier", "Integration")]
    [Fact]
    public Task A_linked_machine_moves_to_another_drive_by_copying() =>
        Moves(Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", "vmq-move-test-" + Guid.NewGuid().ToString("N")[..8]), "drive");

    [Fact]
    public void Refusals_say_why()
    {
        using var profile = new TestProfile("move-refuse");
        var dir = Path.Combine(profile.Root, "lib", "M");
        Directory.CreateDirectory(dir);
        var elsewhere = Path.Combine(profile.Root, "other");
        Assert.StartsWith("Shut the machine down", MachineMove.Refusal(dir, elsewhere, running: true, quickemu: false), StringComparison.Ordinal);
        Assert.StartsWith("A quickemu machine", MachineMove.Refusal(dir, elsewhere, running: false, quickemu: true), StringComparison.Ordinal);
        Assert.Equal("The machine is already in that folder.", MachineMove.Refusal(dir, Path.Combine(profile.Root, "lib"), false, false));
        Assert.Equal("A machine cannot move into its own folder.", MachineMove.Refusal(dir, Path.Combine(dir, "sub"), false, false));
        Directory.CreateDirectory(Path.Combine(elsewhere, "M"));
        Assert.EndsWith("already exists.", MachineMove.Refusal(dir, elsewhere, false, false), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, MachineSuspend.StateFile), "x");
        Assert.StartsWith("The machine is suspended", MachineMove.Refusal(dir, Path.Combine(profile.Root, "third"), false, false), StringComparison.Ordinal);
    }

    /// <summary>The ⋯ menu's Move to… (the folder dialog's answer given directly): the library follows the machine.</summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task Moving_from_the_menu_keeps_the_machine_in_the_library()
    {
        using var profile = new TestProfile("move-window");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Lab");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Lab", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var elsewhere = Path.Combine(profile.Root, "Elsewhere");
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            var menu = window.GetVisualDescendants().OfType<MachineView>().Single().FindControl<MenuItem>("MoveTo");
            _ = shell.Selected!.MoveAsync(elsewhere);
            Ui.PumpUntil(() => shell.Selected?.Folder == Path.Combine(elsewhere, "Lab"), "the moved machine to be selected", 15);
            var result = (menu is not null, shell.Machines.Count, shell.LibraryRoots.Contains(elsewhere));
            window.Close();
            return result;
        });

        Assert.True(seen.Item1, "no Move to… item in the machine's menu");
        Assert.Equal(1, seen.Count);
        Assert.True(seen.Item3, "the folder it moved to did not join the library");
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(elsewhere, "Lab", "vm.json")));
    }
}
