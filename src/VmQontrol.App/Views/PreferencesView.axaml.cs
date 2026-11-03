using Avalonia.Controls;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class PreferencesView : UserControl
{
    public PreferencesView()
    {
        InitializeComponent();
        // Ctrl+K → About VM Qontrol: scroll to the About card once the page is laid out.
        PreferencesViewModel? watched = null;
        void Reveal()
        {
            if (watched?.TakeAboutRequest() != true) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<Border>("About")?.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
        }

        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.AboutRequested -= Reveal;
            watched = DataContext as PreferencesViewModel;
            if (watched is not null) watched.AboutRequested += Reveal;
            Reveal();
        };
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
