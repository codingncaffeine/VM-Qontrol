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
        this.FindControl<Button>("AddShare")!.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || DataContext is not SettingsViewModel vm) return;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to share with the machine" });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.AddShare(path);
        };
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
