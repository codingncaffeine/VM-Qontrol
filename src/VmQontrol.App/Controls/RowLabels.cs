using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace VmQontrol.App.Controls;

/// <summary>
/// Screen-reader names by the layout's own convention: a control in a settings row (<c>Border.row</c>) with no name of
/// its own is named by the row's <c>TextBlock.label</c> and described by its <c>TextBlock.help</c>, which is what a
/// sighted person reads beside it. Avalonia names a control only from AutomationProperties, LabeledBy or plain-text
/// content, so without this a switch, list or number box in a row is silent.
/// </summary>
public static class RowLabels
{
    public static void Install() => Control.LoadedEvent.AddClassHandler<Control>(OnLoaded, RoutingStrategies.Direct);

    private static void OnLoaded(Control control, RoutedEventArgs e)
    {
        if (control is not (ToggleButton or ComboBox or NumericUpDown or TextBox or Slider or ListBox or Button)) return;
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))) return;
        if (control is ContentControl { Content: string { Length: > 0 } }) return;
        if (control.TemplatedParent is not null) return; // a part of another control's template (the number box's text)
        var row = control.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("row"));
        if (row is null) return;
        var texts = row.GetVisualDescendants().OfType<TextBlock>().ToList();
        var label = texts.FirstOrDefault(t => t.Classes.Contains("label"))?.Text;
        if (string.IsNullOrWhiteSpace(label)) return;
        // A button beside a labeled switch or list says what it does in its own content; name it only when it has none.
        AutomationProperties.SetName(control, control is Button ? $"{label}: {ButtonWords(control)}".TrimEnd(':', ' ') : label);
        if (texts.FirstOrDefault(t => t.Classes.Contains("help"))?.Text is { Length: > 0 } help && string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(control)))
            AutomationProperties.SetHelpText(control, help);
    }

    private static string ButtonWords(Control button) =>
        string.Join(" ", button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
}
