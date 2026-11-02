using Avalonia.Data.Converters;

namespace VmQontrol.App.ViewModels;

/// <summary>Small text converters for the views.</summary>
public static class Text
{
    /// <summary>True when an int equals the parameter (which page of the wizard is showing).</summary>
    public static readonly IValueConverter Is = new FuncValueConverter<int, object?, bool>((v, p) => p is not null && v.ToString(System.Globalization.CultureInfo.InvariantCulture) == p.ToString());

    /// <summary>The step rail's text sits beside the current step's accent bar, and indented the same when there is none.</summary>
    public static readonly IValueConverter RailIndent = new FuncValueConverter<bool, Avalonia.Thickness>(current => current ? new Avalonia.Thickness(0) : new Avalonia.Thickness(11, 0, 0, 0));

    public static readonly IValueConverter FavouriteToggle =
        new FuncValueConverter<bool, string>(f => f ? "Remove from favourites" : "Add to favourites");
}
