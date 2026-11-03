using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;

namespace VmQontrol.App.Platform;

/// <summary>
/// The tray icon (phase 19 item 9, opt-in in Preferences → Startup): the desktop's StatusNotifierItem tray on X11 and
/// Wayland alike (KDE Plasma, Xfce, Cinnamon, MATE, wlroots bars; GNOME with the AppIndicator extension). While it is on,
/// the window's close button hides VM Qontrol to the tray; Quit (the menu, Ctrl+Q, signing out) ends it. Machines run on
/// in their own processes either way.
/// </summary>
internal sealed class TrayController
{
    private readonly Application _app;
    private readonly Action _shutdown;
    private readonly ShellViewModel _shell;
    private readonly MainWindow _window;
    private TrayIcon? _icon;
    private bool _rebuildPosted;

    // shutdown ends the app: the desktop lifetime's Shutdown; tests pass their own.
    public TrayController(Application app, Action shutdown, ShellViewModel shell, MainWindow window)
    {
        _app = app;
        _shutdown = shutdown;
        _shell = shell;
        _window = window;
        ShowCommand = new RelayCommand(ShowWindow);
        QuitCommand = new RelayCommand(Quit);
        shell.Machines.CollectionChanged += MachinesChanged;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Settings)) Apply();
        };
        Apply();
    }

    public RelayCommand ShowCommand { get; }

    public RelayCommand QuitCommand { get; }

    /// <summary>The window's close button hides to the tray instead of quitting.</summary>
    public bool KeepsRunning => _icon is not null;

    /// <summary>Set while the app is really quitting, so the window's close is not turned into a hide.</summary>
    public bool Quitting { get; private set; }

    public void Quit()
    {
        Quitting = true;
        _shutdown();
    }

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Shows or removes the icon as the preference says.</summary>
    internal TrayIcon? Icon => _icon;

    private void Apply()
    {
        if (_shell.Settings.TrayIcon && _icon is null)
        {
            _icon = new TrayIcon
            {
                Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://vmqontrol/Assets/icon-256.png"))),
                ToolTipText = TrayMenu.Tip(_shell),
                Command = ShowCommand,
            };
            TrayIcon.SetIcons(_app, [_icon]);
            Rebuild();
        }
        else if (!_shell.Settings.TrayIcon && _icon is not null)
        {
            TrayIcon.SetIcons(_app, []);
            _icon.Dispose();
            _icon = null;
            // Hidden to the tray with the tray now gone: the window comes back, or nothing would show the app.
            if (!_window.IsVisible) ShowWindow();
        }
    }

    private void MachinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var m in e.OldItems?.OfType<MachineViewModel>() ?? []) m.PropertyChanged -= MachineChanged;
        foreach (var m in e.NewItems?.OfType<MachineViewModel>() ?? []) m.PropertyChanged += MachineChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var m in _shell.Machines) { m.PropertyChanged -= MachineChanged; m.PropertyChanged += MachineChanged; }
        PostRebuild();
    }

    private void MachineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MachineViewModel.State) or nameof(MachineViewModel.IsFavorite) or nameof(MachineViewModel.IsSuspended)) PostRebuild();
    }

    /// <summary>Many changes in a row (a library refresh) rebuild the menu once.</summary>
    private void PostRebuild()
    {
        if (_icon is null || _rebuildPosted) return;
        _rebuildPosted = true;
        Ui.Post(() =>
        {
            _rebuildPosted = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        if (_icon is null) return;
        _icon.ToolTipText = TrayMenu.Tip(_shell);
        var menu = new NativeMenu();
        foreach (var entry in TrayMenu.Build(_shell, ShowCommand, QuitCommand)) menu.Items.Add(ToNative(entry));
        _icon.Menu = menu;
    }

    private static NativeMenuItemBase ToNative(TrayEntry entry)
    {
        if (entry.Header is null) return new NativeMenuItemSeparator();
        var item = new NativeMenuItem(entry.Header) { Command = entry.Command, IsEnabled = entry.Enabled };
        if (entry.Items is { } children)
        {
            item.Menu = new NativeMenu();
            foreach (var child in children) item.Menu.Items.Add(ToNative(child));
        }

        return item;
    }
}
