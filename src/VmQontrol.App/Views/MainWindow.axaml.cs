using Avalonia.Controls;
using Avalonia.Input;
using VmQontrol.App.Platform;
using VmQontrol.App.ViewModels;

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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
                shell.Clipboard = text => Clipboard is { } clipboard ? Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text) : Task.CompletedTask;
        };
        KeyDown += (_, e) =>
        {
            // Escape closes a notice the way its Cancel/OK button does.
            if (e.Key == Key.Escape && DataContext is ShellViewModel { Notice: { } notice })
            {
                notice.DismissCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                this.FindControl<TextBox>("Search")?.Focus();
                e.Handled = true;
            }
        };
    }
}
