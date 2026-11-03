using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VmQontrol.App.Platform;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class MainWindow : Window
{
    private Avalonia.Size? _normalSize;
    private Avalonia.PixelPoint? _normalPosition;

    /// <summary>Whether the desktop lets the app place its own window (X11 does; Wayland compositors place windows).</summary>
    private bool PlacesItself => !(PlatformImpl?.GetType().Namespace ?? "").StartsWith("Avalonia.Wayland", StringComparison.Ordinal);

    /// <summary>
    /// Opens at the size (and on X11 the place) it was closed at, maximized again if it was. A size larger than every
    /// screen shrinks to the largest; a place off every screen (a monitor unplugged since) is left to the desktop.
    /// </summary>
    public void RestorePlacement(VmQontrol.Core.WindowPlacement? saved)
    {
        if (saved is null) return;
        var screens = Screens.All;
        var widest = screens.Count == 0 ? (Avalonia.Size?)null : screens.Select(sc => new Avalonia.Size(sc.WorkingArea.Width / sc.Scaling, sc.WorkingArea.Height / sc.Scaling)).MaxBy(z => z.Width * z.Height);
        Width = Math.Max(MinWidth, widest is { } w ? Math.Min(saved.Width, w.Width) : saved.Width);
        Height = Math.Max(MinHeight, widest is { } h ? Math.Min(saved.Height, h.Height) : saved.Height);
        _normalSize = new Avalonia.Size(Width, Height);
        if (saved.X is { } x && saved.Y is { } y && PlacesItself)
        {
            var corner = new Avalonia.PixelPoint(x, y);
            // At least the title bar's start must be on a screen, or the window could not be dragged back.
            if (screens.Any(sc => sc.WorkingArea.Contains(corner + new Avalonia.PixelPoint(48, 16))))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = corner;
                _normalPosition = corner;
            }
        }

        if (saved.Maximized && WindowState == WindowState.Normal) WindowState = WindowState.Maximized;
    }

    /// <summary>What to remember at close: never a minimized size, and the size from before maximizing.</summary>
    internal VmQontrol.Core.WindowPlacement? Placement()
    {
        var size = WindowState == WindowState.Normal ? ClientSize : _normalSize ?? ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return null;
        var place = PlacesItself ? (WindowState == WindowState.Normal ? Position : _normalPosition) : null;
        return new VmQontrol.Core.WindowPlacement
        {
            Width = Math.Round(size.Width),
            Height = Math.Round(size.Height),
            X = place?.X,
            Y = place?.Y,
            Maximized = WindowState is WindowState.Maximized or WindowState.FullScreen,
        };
    }

    public MainWindow()
    {
        InitializeComponent();
        PaletteViewModel.KeysFor = Shortcuts.ShownFor;
        // A folder heading row is never selected or focused (its button folds the folder); containers are recycled, so set both ways.
        if (this.FindControl<ListBox>("Library") is { } library)
        {
            library.ContainerPrepared += (_, e) => e.Container.Focusable = e.Container.DataContext is not GroupHeading;
            WireDragAndDrop(library);
        }

        // Every Opened, not once: a window shown again gets a new Wayland toplevel that needs its app id again.
        Opened += (_, _) =>
        {
            DesktopIdentity.Apply(this);
            WindowingBackend.WindowOpened(this);
        };
        Closed += (_, _) => (DataContext as ShellViewModel)?.Stop();
        // The size (and on X11 the place) while not maximized, so a window closed maximized comes back to both.
        PropertyChanged += (_, e) =>
        {
            if (WindowState != WindowState.Normal) return;
            if (e.Property == ClientSizeProperty) _normalSize = ClientSize;
        };
        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal) _normalPosition = e.Point;
        };
        Closing += (_, _) =>
        {
            if (DataContext is ShellViewModel shell && Placement() is { } placement)
                shell.UpdateSettings(s => s with { MainWindow = placement });
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
                shell.Clipboard = text => Clipboard is { } clipboard ? Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text) : Task.CompletedTask;
        };
        // Tunnel: the palette's keys and the shortcuts win over whatever control has focus (a text box would take Enter and
        // the arrows); a text box keeps the keys that edit text (Shortcut.WhileTyping).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not ShellViewModel shell) return;
            var palette = shell.Palette;
            if (palette.IsOpen)
            {
                switch (e.Key)
                {
                    case Key.Escape: palette.Close(); e.Handled = true; return;
                    case Key.Enter: palette.RunHighlighted(); e.Handled = true; return;
                    case Key.Down: palette.Move(1); e.Handled = true; return;
                    case Key.Up: palette.Move(-1); e.Handled = true; return;
                }

                if (!(e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)) return;
            }

            if (shell.ShowShortcuts && e.Key == Key.Escape)
            {
                shell.ShowShortcuts = false;
                e.Handled = true;
                return;
            }

            // An open dialog holds the shortcuts back; the palette and the sheet still close on their own keys.
            if (shell.HasOverlay)
            {
                var closesOverlay = (palette.IsOpen && e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
                                    || (shell.ShowShortcuts && e.Key is Key.OemQuestion or Key.Oem2 && e.KeyModifiers.HasFlag(KeyModifiers.Control));
                if (!closesOverlay) return;
            }

            var typing = FocusManager?.GetFocusedElement() is TextBox;
            // Enter and Delete act on the machine only from the library list (or nothing focused), never from a button or a page's field.
            if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Enter or Key.Delete
                && FocusManager?.GetFocusedElement() is { } focused and not ListBoxItem and not ListBox) return;
            if (Shortcuts.TryRun(shell, this, e, typing)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            // Escape closes a prompt, the USB panel, the folder prompt, or a notice the way its Cancel/OK button does.
            if (e.Key == Key.Escape && DataContext is ShellViewModel { Prompt: { Busy: false } open })
            {
                open.CancelCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { Networks: { } networks })
            {
                networks.CloseCommand.Execute(null);
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
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { GroupPrompt: { } prompt })
            {
                prompt.CancelCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { Notice: { } notice })
            {
                notice.DismissCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { SettingsPage: { } settings })
            {
                CloseSettings(settings);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { PreferencesPage: { } preferences })
            {
                preferences.CloseCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>Focuses a named text box once the page that holds it is on screen.</summary>
    public void FocusLater(string name) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == name && t.IsEffectivelyVisible) is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }, Avalonia.Threading.DispatcherPriority.Background);

    public void TogglePalette()
    {
        if (DataContext is not ShellViewModel shell) return;
        if (shell.Palette.IsOpen) shell.Palette.Close();
        else
        {
            shell.Palette.Open();
            FocusLater("PaletteQuery");
        }
    }

    /// <summary>Escape on a machine's Settings: closes it, asking first when there are changes not saved.</summary>
    private async void CloseSettings(SettingsViewModel settings)
    {
        if (DataContext is not ShellViewModel shell) return;
        var changes = settings.UnsavedChanges();
        if (changes.Count > 0)
        {
            var notice = new NoticeViewModel("Discard your changes?", [$"Not saved: {string.Join("; ", changes)}."], confirm: "Discard", dismiss: "Keep editing", isDanger: true);
            shell.Show(notice);
            if (!await notice.Answer) return;
        }

        settings.CloseCommand.Execute(null);
    }

    /// <summary>
    /// A machine only this window understands (its id): offering it as text would paste an id into any other window on
    /// the desktop that takes a drop.
    /// </summary>
    internal static readonly DataFormat<byte[]> MachineDrag = DataFormat.CreateBytesApplicationFormat("vmqontrol-machine-id");

    private bool _dragging;

    private static string? Carried(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(MachineDrag) is { Length: > 0 } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    /// <summary>The folder a drop at this spot goes to: a heading's, a machine's own (dropped beside it), or "" (no folder).</summary>
    private static string? TargetGroup(object? source, Control libraryHeading) =>
        source is Control c && (c == libraryHeading || c.FindAncestorOfType<Grid>(includeSelf: true) is { Name: "LibraryHeading" })
            ? ""
            : (source as Control)?.DataContext switch
            {
                GroupHeading heading => heading.Name,
                MachineViewModel machine => machine.GroupName,
                _ => null,
            };

    /// <summary>Drag a machine onto a folder's heading (or a machine in it) to move it in; onto LIBRARY to take it out.</summary>
    private void WireDragAndDrop(ListBox library)
    {
        var heading = this.FindControl<Grid>("LibraryHeading")!;
        // Begun from the press, as the platform's drag needs; Avalonia starts it only once the pointer moves, so a click
        // still just selects (the list item has handled the press by then, so this listens to handled presses too).
        library.AddHandler(PointerPressedEvent, async (_, e) =>
        {
            if (_dragging || !e.GetCurrentPoint(library).Properties.IsLeftButtonPressed) return;
            if ((e.Source as Control)?.DataContext is not MachineViewModel machine) return;
            _dragging = true;
            try
            {
                using var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.Create(MachineDrag, System.Text.Encoding.UTF8.GetBytes(machine.Id)));
                await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
            }
            finally
            {
                _dragging = false;
                (DataContext as ShellViewModel)?.MarkDrop(null);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        foreach (var target in new Control[] { library, heading })
        {
            DragDrop.SetAllowDrop(target, true);
            target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                var group = Carried(e) is not null ? TargetGroup(e.Source, heading) : null;
                e.DragEffects = group is null ? DragDropEffects.None : DragDropEffects.Move;
                (DataContext as ShellViewModel)?.MarkDrop(group);
                e.Handled = true;
            });
            target.AddHandler(DragDrop.DragLeaveEvent, (_, _) => (DataContext as ShellViewModel)?.MarkDrop(null));
            target.AddHandler(DragDrop.DropEvent, (_, e) =>
            {
                e.Handled = true;
                if (DataContext is not ShellViewModel shell) return;
                shell.MarkDrop(null);
                if (Carried(e) is { } id && TargetGroup(e.Source, heading) is { } group) shell.Drop(id, group);
            });
        }
    }
}
