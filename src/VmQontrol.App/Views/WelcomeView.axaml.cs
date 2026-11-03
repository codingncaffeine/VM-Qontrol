using Avalonia.Controls;
using VmQontrol.App.ViewModels;
using VmQontrol.Core;

namespace VmQontrol.App.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
        this.FindControl<Button>("AddLibraryFolder")!.Click += async (_, _) =>
        {
            Log.Info("Add machines from a folder: pressed.");
            if (DataContext is not ShellViewModel shell)
            {
                Log.Warn($"Add machines from a folder: the page shows {DataContext?.GetType().Name ?? "nothing"}, not the library.");
                return;
            }

            try
            {
                if (await Pickers.FolderAsync(this, "Which folder holds your machines?") is { } path) await shell.AddLibraryFolderAsync(path);
            }
            catch (PickerFailedException ex)
            {
                shell.Note($"The folder dialog could not open: {ex.Message}");
            }
        };
    }
}
