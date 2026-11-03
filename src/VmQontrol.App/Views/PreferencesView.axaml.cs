using Avalonia.Controls;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class PreferencesView : UserControl
{
    public PreferencesView()
    {
        InitializeComponent();
        this.FindControl<Button>("AddRoot")!.Click += async (_, _) =>
        {
            if (DataContext is not PreferencesViewModel vm) return;
            try
            {
                if (await Pickers.FolderAsync(this, "Which folder holds your machines?") is { } path) await vm.AddRootAsync(path);
            }
            catch (PickerFailedException)
            {
                // Logged by the picker.
            }
        };
    }
}
