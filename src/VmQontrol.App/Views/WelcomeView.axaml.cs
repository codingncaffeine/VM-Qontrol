using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
        this.FindControl<Button>("AddLibraryFolder")!.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || DataContext is not ShellViewModel shell) return;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Which folder holds your machines?" });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) await shell.AddLibraryFolderAsync(path);
        };
    }
}
