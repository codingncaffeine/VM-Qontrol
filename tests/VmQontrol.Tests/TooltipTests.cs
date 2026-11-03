using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

/// <summary>
/// The user's ask (2026-11-03): buttons that are only a picture say what they do. Every icon-only button in every view
/// carries a tooltip (read from the XAML itself), and the tooltip is drawn in the design language, dark and light.
/// </summary>
public sealed class TooltipTests
{
    private static readonly string[] ButtonTags = ["Button", "ToggleButton", "RepeatButton", "SplitButton", "DropDownButton"];

    private static IEnumerable<(string File, string Name, bool HasTip)> IconOnlyButtons()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "VmQontrol.slnx"))) dir = Path.GetDirectoryName(dir)!;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir, "src", "VmQontrol.App", "Views"), "*.axaml"))
        {
            foreach (var b in XDocument.Load(file).Descendants().Where(e => ButtonTags.Contains(e.Name.LocalName)))
            {
                // Words count as text; a symbol (⋯, ✕, +) is a picture like an icon.
                static bool Words(string? v) => v is not null && (v.StartsWith('{') || v.Any(char.IsLetter));
                var hasText = Words(b.Attribute("Content")?.Value)
                              || b.Descendants().Any(d => d.Name.LocalName == "TextBlock" && Words(d.Attribute("Text")?.Value))
                              || b.Nodes().OfType<XText>().Any(t => Words(t.Value.Trim()));
                if (hasText) continue;
                var tip = b.Attributes().Any(a => a.Name.LocalName == "ToolTip.Tip" && a.Value.Trim().Length > 0) || b.Elements().Any(e => e.Name.LocalName == "ToolTip.Tip");
                yield return (Path.GetFileName(file), b.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? b.Attribute("Command")?.Value ?? "?", tip);
            }
        }
    }

    [Fact]
    public void Every_icon_only_button_says_what_it_does()
    {
        var buttons = IconOnlyButtons().ToList();
        Assert.True(buttons.Count >= 8, $"the scan found only {buttons.Count} icon-only buttons (it cannot see them)");
        Assert.Empty(buttons.Where(b => !b.HasTip).Select(b => $"{b.File}: {b.Name}"));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_tooltip_is_drawn_in_the_design_language()
    {
        using var profile = new TestProfile("tooltips");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Tip");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Tip", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 860 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            var results = new List<(string Theme, double Width, string Background)>();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = theme;
                Ui.Pump(0.3);
                var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "OpenPreferences");
                ToolTip.SetIsOpen(button, true);
                Ui.Pump(0.4);
                // Avalonia keeps the open tooltip in an internal attached property of its control.
                var property = (Avalonia.AvaloniaProperty?)typeof(ToolTip).GetField("ToolTipProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null)
                               ?? throw new InvalidOperationException("Avalonia no longer has ToolTip.ToolTipProperty");
                var tip = button.GetValue(property) as ToolTip ?? throw new InvalidOperationException("no tooltip opened");
                var root = TopLevel.GetTopLevel(tip) ?? throw new InvalidOperationException("the tooltip has no popup");
                Ui.CaptureTopLevel(root, "phase17", $"tooltip-{theme}");
                results.Add((theme.ToString(), tip.Bounds.Width, (tip.Background as Avalonia.Media.ISolidColorBrush)?.Color.ToString() ?? "?"));
                ToolTip.SetIsOpen(button, false);
                Ui.Pump(0.2);
            }

            window.Close();
            return results;
        });

        Assert.Equal(2, seen.Count);
        Assert.All(seen, r => Assert.InRange(r.Width, 40, 340));
        Assert.NotEqual(seen[0].Background, seen[1].Background); // the surface follows the theme
    }
}
