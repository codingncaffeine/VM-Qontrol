using Avalonia.Controls;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class MachineView : UserControl
{
    private ConsoleViewModel? _console;

    public MachineView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_console is not null) _console.OutputAdded -= OnOutput;
            _console = (DataContext as MachineViewModel)?.Console;
            if (_console is not null) _console.OutputAdded += OnOutput;
        };
        // One live collection: the menu's presenter takes its items when it opens, so the list is changed in place.
        if (this.FindControl<Button>("OpenUsb")?.Flyout is MenuFlyout usb)
        {
            usb.ItemsSource = _usbItems;
            usb.Opening += (_, _) => FillUsbMenu();
        }
    }

    /// <summary>
    /// The USB menu, filled each time it opens: a placeholder at once, then every device plugged in (read on a worker),
    /// ticked when it is in the machine; Manage… opens the full panel (Always connect, details).
    /// </summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<Control> _usbItems = [];

    private void FillUsbMenu()
    {
        if (DataContext is not MachineViewModel machine) return;
        _usbItems.Clear();
        _usbItems.Add(new MenuItem { Header = "Looking for devices…", IsEnabled = false });
        _ = Task.Run(async () =>
        {
            var devices = await machine.UsbMenuAsync().ConfigureAwait(false);
            Ui.Post(() =>
            {
                _usbItems.Clear();
                foreach (var item in UsbMenuItems(devices, machine.IsActive, (d, inMachine) => _ = Task.Run(() => machine.ToggleUsbAsync(d, inMachine)), () => machine.OpenUsbCommand.Execute(null)))
                    _usbItems.Add(item);
            });
        });
    }

    /// <summary>The menu's items for a list of devices (internal so a test can render exactly these).</summary>
    internal static List<Control> UsbMenuItems(IReadOnlyList<(VmQontrol.Core.Devices.HostUsbDevice Device, bool InMachine)> devices, bool active,
        Action<VmQontrol.Core.Devices.HostUsbDevice, bool> toggle, Action manage)
    {
        var items = new List<Control>();
        if (!active) items.Add(new MenuItem { Header = "Power the machine on to connect devices", IsEnabled = false });
        foreach (var (device, inMachine) in devices)
        {
            var item = new MenuItem { Header = DeviceHeader(device), ToggleType = MenuItemToggleType.CheckBox, IsChecked = inMachine, IsEnabled = active, Tag = device.Id };
            item.Click += (_, _) => toggle(device, inMachine);
            items.Add(item);
        }

        if (devices.Count == 0) items.Add(new MenuItem { Header = "No USB devices are plugged in", IsEnabled = false });
        items.Add(new Separator());
        var more = new MenuItem { Header = "Manage USB devices…", Name = "ManageUsb" };
        more.Click += (_, _) => manage();
        items.Add(more);
        return items;
    }

    private static StackPanel DeviceHeader(VmQontrol.Core.Devices.HostUsbDevice device) => new()
    {
        Spacing = 1,
        Children =
        {
            new TextBlock { Text = device.Name },
            new TextBlock { Text = $"{device.Id} · {device.Speed}", Classes = { "muted" }, FontSize = 11 },
        },
    };

    /// <summary>New console output keeps the view at the bottom, unless the user has scrolled up to read.</summary>
    private void OnOutput()
    {
        if (this.FindControl<ListBox>("ConsoleLines") is not { Scroll: ScrollViewer scroll } list || _console is null) return;
        var atBottom = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 40;
        if (atBottom && _console.Lines.Count > 0) list.ScrollIntoView(_console.Lines.Count - 1);
    }
}
