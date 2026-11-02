using Avalonia.Controls;
using VmQontrol.App.Platform;

namespace VmQontrol.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Every Opened, not once: a window shown again gets a new Wayland toplevel that needs its app id again.
        Opened += (_, _) =>
        {
            DesktopIdentity.Apply(this);
            WindowingBackend.WindowOpened(this);
        };
    }
}
