using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class NewMachineView : UserControl
{
    private static readonly FilePickerFileType DiscImages = new("Disc images") { Patterns = ["*.iso", "*.ISO"] };

    public NewMachineView()
    {
        InitializeComponent();
        this.FindControl<Button>("ChooseIso")!.Click += async (_, _) =>
        {
            if (await PickFileAsync("Choose an installer disc image") is { } path && DataContext is NewMachineViewModel vm) await vm.UseIsoAsync(path);
        };
        this.FindControl<Button>("ChooseDrivers")!.Click += async (_, _) =>
        {
            if (await PickFileAsync("Choose the VirtIO driver disc (virtio-win.iso)") is { } path && DataContext is NewMachineViewModel vm) vm.DriversIso = path;
        };
        this.FindControl<Button>("ChooseLibrary")!.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || DataContext is not NewMachineViewModel vm) return;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where should this machine's files be kept?" });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.LibraryRoot = path;
        };
        var drop = this.FindControl<Border>("DropZone")!;
        drop.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None);
        drop.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path && DataContext is NewMachineViewModel vm) await vm.UseIsoAsync(path);
        });
    }

    private async Task<string?> PickFileAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [DiscImages, FilePickerFileTypes.All] });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
