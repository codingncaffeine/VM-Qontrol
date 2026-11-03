using Avalonia.Data.Converters;

namespace VmQontrol.App.ViewModels;

/// <summary>Small text converters for the views.</summary>
public static class Text
{
    /// <summary>True when an int equals the parameter (which page of the wizard is showing).</summary>
    public static readonly IValueConverter Is = new FuncValueConverter<int, object?, bool>((v, p) => p is not null && v.ToString(System.Globalization.CultureInfo.InvariantCulture) == p.ToString());

    /// <summary>The step rail's text sits beside the current step's accent bar, and indented the same when there is none.</summary>
    public static readonly IValueConverter RailIndent = new FuncValueConverter<bool, Avalonia.Thickness>(current => current ? new Avalonia.Thickness(0) : new Avalonia.Thickness(11, 0, 0, 0));

    /// <summary>True when a string equals the parameter (which settings section is showing).</summary>
    public static readonly IValueConverter Equal = new FuncValueConverter<string, object?, bool>((v, p) => v is not null && v == p as string);

    public static readonly IReadOnlyList<string> Protocols = ["tcp", "udp"];

    public static readonly IValueConverter FavouriteToggle =
        new FuncValueConverter<bool, string>(f => f ? "Remove from favourites" : "Add to favourites");

    /// <summary>"ctrl+alt+f2" → "Ctrl+Alt+F2", the way the keys are printed on a keyboard.</summary>
    public static string KeyLabel(string qcodes) => string.Join('+', qcodes.Split('+').Select(k => k switch
    {
        "ctrl" => "Ctrl", "alt" => "Alt", "shift" => "Shift", "backspace" => "Backspace", "delete" => "Del", "print" => "Print Screen",
        "sysrq" => "SysRq", "esc" => "Esc", "tab" => "Tab", "meta_l" => "Super",
        _ when k.Length <= 3 && k.StartsWith('f') => k.ToUpperInvariant(),
        _ => k.Length > 0 ? char.ToUpperInvariant(k[0]) + k[1..] : k,
    }));
}
