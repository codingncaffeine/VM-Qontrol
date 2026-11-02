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
        // A folder heading row takes no pointer and no focus (containers are recycled, so set both ways).
        if (this.FindControl<ListBox>("Library") is { } library)
        {
            library.ContainerPrepared += (_, e) =>
            {
                var heading = e.Container.DataContext is FolderHeading;
                e.Container.IsHitTestVisible = !heading;
                e.Container.Focusable = !heading;
            };
        }

        // Every Opened, not once: a window shown again gets a new Wayland toplevel that needs its app id again.
        Opened += (_, _) =>
        {
            DesktopIdentity.Apply(this);
            WindowingBackend.WindowOpened(this);
        };
        Closed += (_, _) => (DataContext as ShellViewModel)?.Stop();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
                shell.Clipboard = text => Clipboard is { } clipboard ? Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text) : Task.CompletedTask;
        };
        // Tunnel: the palette's keys win over whatever control has focus (a text box would take Enter and the arrows).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not ShellViewModel shell) return;
            var palette = shell.Palette;
            if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
            {
                if (palette.IsOpen) palette.Close();
                else
                {
                    palette.Open();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("PaletteQuery")?.Focus());
                }

                e.Handled = true;
                return;
            }

            if (!palette.IsOpen) return;
            switch (e.Key)
            {
                case Key.Escape: palette.Close(); e.Handled = true; break;
                case Key.Enter: palette.RunHighlighted(); e.Handled = true; break;
                case Key.Down: palette.Move(1); e.Handled = true; break;
                case Key.Up: palette.Move(-1); e.Handled = true; break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            // Escape closes a prompt, the USB panel, the folder prompt, or a notice the way its Cancel/OK button does.
            if (e.Key == Key.Escape && DataContext is ShellViewModel { Prompt: { Busy: false } open })
            {
                open.CancelCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { DrivesPanel: { } drives })
            {
                drives.CloseCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { UsbPanel: { } usb })
            {
                usb.CloseCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { FolderPrompt: { } prompt })
            {
                prompt.CancelCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { Notice: { } notice })
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
