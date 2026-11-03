using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 13 accessibility: every control a person can operate has a name a screen reader can say. Avalonia names a
/// control only from AutomationProperties.Name, LabeledBy, or plain-text content, so an icon button, or a switch whose
/// label is a TextBlock beside it, is silent. Walks every page and every settings section; the report lists each one.
/// </summary>
public sealed class AccessibleNamesTests
{
    private static string Describe(Control c)
    {
        var where = c.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Name is { Length: > 0 })?.Name ?? "";
        var label = c.GetVisualAncestors().OfType<Grid>().FirstOrDefault()?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("label"))?.Text;
        return $"{c.GetType().Name}{(c.Name is { Length: > 0 } n ? "#" + n : "")} near \"{label ?? where}\"";
    }

    private static IEnumerable<string> Unnamed(Window window, string page) =>
        window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c is Button or ToggleButton or ComboBox or NumericUpDown or TextBox or Slider or ListBox)
            .Where(c => c is not TextBox || c.GetVisualAncestors().OfType<NumericUpDown>().FirstOrDefault() is null)
            .Where(c => c.GetVisualAncestors().OfType<ComboBox>().FirstOrDefault() is null && c.GetVisualAncestors().OfType<ScrollBar>().FirstOrDefault() is null)
            .Where(c => c is not ButtonSpinner && c.TemplatedParent is not NumericUpDown)
            .Where(c => string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(c).GetName()))
            .Select(c => $"{page}: {Describe(c)}");

    [Trait("Tier", "Window")]
    [Fact]
    public async Task Every_control_on_every_page_has_a_name()
    {
        using var profile = new TestProfile("a11y");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Lab");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Lab", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var found = await Ui.Run(() =>
        {
            var list = new List<string>();
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Pump(0.5);
            list.AddRange(Unnamed(window, "welcome"));
            Ui.Click(window, window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Pump(0.5);
            list.AddRange(Unnamed(window, "machine"));
            Ui.Click(window, window.GetVisualDescendants().OfType<MachineView>().Single().GetVisualDescendants().OfType<Button>().First(b => b.Name == "OpenSettings"));
            Ui.PumpUntil(() => shell.SettingsPage is not null, "the settings");
            var settings = shell.SettingsPage!;
            foreach (var section in settings.Sections)
            {
                settings.Section = section;
                Ui.Pump(0.3);
                list.AddRange(Unnamed(window, "settings/" + section.Key));
            }

            settings.Section = settings.Sections.First(x => x.Key == "processors");
            Ui.Pump(0.3);
            var nested = ControlAutomationPeer.CreatePeerForElement(window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "Nested"));
            list.Add(nested.GetName() == "Nested virtualization" && nested.GetHelpText().StartsWith("Lets the guest run", StringComparison.Ordinal)
                ? "" : $"check: the Nested combo reads \"{nested.GetName()}\" / \"{nested.GetHelpText()}\"");
            list.RemoveAll(x => x.Length == 0);

            settings.CloseCommand.Execute(null);
            Ui.Pump(0.3);
            shell.OpenPreferences();
            Ui.Pump(0.5);
            list.AddRange(Unnamed(window, "preferences"));
            shell.PreferencesPage = null;
            shell.NewMachine();
            Ui.Pump(0.5);
            list.AddRange(Unnamed(window, "wizard"));
            window.Close();
            return list.Distinct().ToList();
        });

        var report = Path.Combine(TestProfile.ScratchRoot, "a11y-unnamed.txt");
        File.WriteAllLines(report, found);
        Assert.True(found.Count == 0, $"{found.Count} controls have no accessible name (list in {report}):\n" + string.Join("\n", found.Take(40)));
    }
}
