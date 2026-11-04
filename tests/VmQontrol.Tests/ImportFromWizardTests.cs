using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;

namespace VmQontrol.Tests;

/// <summary>
/// A disk (or a machine file) put in the New machine wizard's installer disc box is handed to Import instead of being
/// refused, and lands on Import's review with the system and firmware the disk itself showed.
/// </summary>
public sealed class ImportFromWizardTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    private static string Sfdisk => File.Exists("/usr/bin/sfdisk") ? "/usr/bin/sfdisk" : "/sbin/sfdisk";

    /// <summary>A Windows-shaped GPT disk (EFI, reserved, data, recovery) as a .vmdk.</summary>
    private static string WindowsVmdk(TestProfile profile)
    {
        var raw = Path.Combine(profile.Root, "Office PC.raw");
        using (var f = File.Create(raw)) f.SetLength(64L << 20);
        foreach (var (program, input, args) in new[]
                 {
                     (Sfdisk, "label: gpt\nsize=8M, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B\nsize=8M, type=E3C9E316-0B5C-4DB8-817D-F92DF00215AE\nsize=30M, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7\ntype=DE94BBA4-06D1-4D40-A16A-BFD50179D6AC\n", new[] { "-q", raw }),
                     ("qemu-img", "", new[] { "convert", "-O", "vmdk", raw, Path.Combine(profile.Root, "Office PC.vmdk") }),
                 })
        {
            var start = new ProcessStartInfo(program) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            p.StandardInput.Write(input);
            p.StandardInput.Close();
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        return Path.Combine(profile.Root, "Office PC.vmdk");
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_disk_in_the_wizards_disc_box_goes_to_import_recognized()
    {
        Assert.SkipWhen(!File.Exists(Sfdisk) || !File.Exists("/usr/bin/qemu-io"), "sfdisk and qemu-io are needed");
        using var profile = new TestProfile("wizard-existing");
        Directory.CreateDirectory(profile.Paths.DefaultLibrary);
        var disk = WindowsVmdk(profile);
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Welcome.Checked, "the host check");
            Ui.Click(window, Named<Button>(window, "NewMachine"));
            Ui.PumpUntil(() => shell.Wizard is not null, "the wizard");

            // What "Choose disc image…" and a drop on the disc box call with the file.
            _ = shell.Wizard!.UseFileAsync(disk);
            Ui.PumpUntil(() => shell.Wizard is null && shell.ImportPage is { Reviewing: true }, "Import's review", 30);
            Ui.Pump(0.4);
            var import = shell.ImportPage!;
            var system = import.DetectedSystem;
            var notes = string.Join(" ", import.Machine!.Notes);
            var firmware = import.Rows.First(r => r.Label == "Firmware").Value;
            Ui.Capture(window, "s14-import", "review-recognized");
            window.Close();
            return (system, notes, firmware);
        });

        Assert.StartsWith("Windows 11", seen.system, StringComparison.Ordinal);
        Assert.Contains("Recognized from the disk", seen.notes, StringComparison.Ordinal);
        Assert.Equal("UEFI", seen.firmware);
    }
}
