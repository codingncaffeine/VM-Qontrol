using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// D37: a machine's disk opened on this computer. The libguestfs walk itself (read-only, writes kept, data disks) runs in
/// tools/guestfs-proof (a container with libguestfs); these hold the rules around it on any computer.
/// </summary>
public sealed class DiskOpenerTests
{
    /// <summary>An opened disk as the opener records it, at a folder that is mounted on every Linux computer.</summary>
    internal static void PretendOpen(QontrolPaths paths, MachineConfig m, string mountPoint)
    {
        Directory.CreateDirectory(DiskOpener.Root(paths));
        File.WriteAllText(Path.Combine(DiskOpener.Root(paths), $".{m.Id}-0.json"), JsonSerializer.Serialize(new OpenedDisk(m.Id, m.Name, 0, mountPoint, false)));
    }

    private static (MachineConfig, string) Machine(TestProfile profile, string name)
    {
        var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = name, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        return (m, dir);
    }

    [Fact]
    public async Task A_machine_whose_disk_is_open_will_not_start_and_says_why()
    {
        using var profile = new TestProfile("disk-open-start");
        var (m, dir) = Machine(profile, "Web");
        PretendOpen(profile.Paths, m, "/proc");
        var refused = await Assert.ThrowsAsync<StartRefusedException>(() =>
            MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, TestContext.Current.CancellationToken));
        Assert.Equal("Web's disk is open on this computer (/proc). Close it first: the machine and this computer must not use the disk at once.", refused.Message);
    }

    [Fact]
    public void A_record_whose_folder_is_no_longer_mounted_is_cleared()
    {
        using var profile = new TestProfile("disk-open-stale");
        var (m, _) = Machine(profile, "Web");
        var gone = Path.Combine(DiskOpener.Root(profile.Paths), "Web");
        Directory.CreateDirectory(gone);
        PretendOpen(profile.Paths, m, gone);
        Assert.Empty(DiskOpener.OpenFor(profile.Paths, m.Id));
        Assert.False(Directory.Exists(gone), "the empty folder of a closed disk was left");
        Assert.Empty(Directory.EnumerateFiles(DiskOpener.Root(profile.Paths), ".*.json"));

        // The control: the same record at a mounted folder is kept.
        PretendOpen(profile.Paths, m, "/proc");
        Assert.Single(DiskOpener.OpenFor(profile.Paths, m.Id));
    }

    [Fact]
    public async Task Without_libguestfs_it_names_the_package_and_a_running_or_suspended_disk_is_refused_first()
    {
        using var profile = new TestProfile("disk-open-rules");
        var (m, dir) = Machine(profile, "Web");
        if (!DiskOpener.Available)
        {
            var missing = await Assert.ThrowsAsync<DiskOpenException>(() => DiskOpener.OpenAsync(m, dir, profile.Paths, 0, false, TestContext.Current.CancellationToken));
            Assert.Contains("libguestfs", missing.Message, StringComparison.Ordinal);
            Assert.Contains(Doctor.InstallCommand(Doctor.DetectThisDistro(), "guestmount"), missing.Message, StringComparison.Ordinal);
            return;
        }

        File.WriteAllText(MachineSuspend.StatePath(dir), "state");
        var suspended = await Assert.ThrowsAsync<DiskOpenException>(() => DiskOpener.OpenAsync(m, dir, profile.Paths, 0, true, TestContext.Current.CancellationToken));
        Assert.StartsWith("Web is suspended: changing its disk would break its resume.", suspended.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_doctor_names_the_package_for_each_family_checked_against_its_index()
    {
        Assert.Contains("libguestfs", Doctor.InstallCommand(DistroFamily.Arch, "guestmount"), StringComparison.Ordinal);
        Assert.Contains("guestmount", Doctor.InstallCommand(DistroFamily.Debian, "guestmount"), StringComparison.Ordinal);
        Assert.Contains("libguestfs", Doctor.InstallCommand(DistroFamily.Fedora, "guestmount"), StringComparison.Ordinal);
        Assert.Contains("guestfs-tools", Doctor.InstallCommand(DistroFamily.Alpine, "guestmount"), StringComparison.Ordinal);
    }

    /// <summary>The machine page while its disk is open: the card says where and offers Close; the Open items are gone.</summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_machine_page_shows_the_open_disk_and_hides_opening_it_again()
    {
        using var profile = new TestProfile("disk-open-window");
        var (m, _) = Machine(profile, "Web");
        PretendOpen(profile.Paths, m, "/proc");
        var (visible, text, canOpen) = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            var item = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as MachineViewModel)?.Name == "Web");
            Ui.Click(window, item);
            Ui.PumpUntil(() => shell.Selected is { HasOpenDisk: true }, "the open disk on Web's page");
            Ui.Pump(0.4);
            var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "OpenDiskCard");
            Ui.Capture(window, "s12", "open-disk-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s12", "open-disk-light");
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            var result = (card.IsEffectivelyVisible, shell.Selected!.OpenDiskText, shell.Selected.CanOpenDisk);
            window.Close();
            return result;
        });

        Assert.True(visible);
        Assert.Equal("Open read-only at /proc. Web cannot start until it is closed.", text);
        Assert.False(canOpen);
    }
}
