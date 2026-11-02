using Avalonia.Data.Converters;

namespace VmQontrol.App.ViewModels;

/// <summary>Small text converters for the views.</summary>
public static class Text
{
    public static readonly IValueConverter FavouriteToggle =
        new FuncValueConverter<bool, string>(f => f ? "Remove from favourites" : "Add to favourites");
}
