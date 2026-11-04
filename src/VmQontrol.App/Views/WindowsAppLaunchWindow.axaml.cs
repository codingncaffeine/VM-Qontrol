using Avalonia.Controls;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class WindowsAppLaunchWindow : Window
{
    public WindowsAppLaunchWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not WindowsAppLaunchViewModel vm) return;
            vm.Done += Close;
            vm.PropertyChanged += (_, e) =>
            {
                // The password field takes the keyboard as soon as it appears.
                if (e.PropertyName == nameof(WindowsAppLaunchViewModel.ShowPassword) && vm.ShowPassword)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("LaunchPassword")?.Focus());
            };
        };
    }
}
