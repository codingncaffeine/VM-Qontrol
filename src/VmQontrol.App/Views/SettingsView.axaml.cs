using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // "Change disc…" buttons inside the disc list: pick a file, then the view model puts it in the drive.
        AddHandler(Button.ClickEvent, async (_, e) =>
        {
            if (e.Source is not Button { Tag: "change", DataContext: MediaRow row } || DataContext is not SettingsViewModel vm) return;
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Choose a disc for: {row.Title}",
                FileTypeFilter = [new FilePickerFileType("Disc and floppy images") { Patterns = ["*.iso", "*.ISO", "*.img", "*.IMG"] }, FilePickerFileTypes.All],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.ChangeDiscAsync(row, path);
        }, RoutingStrategies.Bubble);
    }
}
