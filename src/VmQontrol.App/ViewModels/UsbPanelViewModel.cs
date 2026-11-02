using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>One USB device in the panel: where it is now, and whether the machine takes it at every power on.</summary>
public sealed partial class UsbRow(HostUsbDevice device, UsbPanelViewModel panel) : ObservableObject
{
    public HostUsbDevice Device { get; } = device;

    public string Name => Device.Name;

    public string Detail => $"{Device.Id} · {Device.Speed} · port {Device.Port}";

    public bool NeedsPermission => !Device.Writable;

    public string PermissionFix => UsbHost.PermissionFix(Device);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Where), nameof(ActionText))]
    private bool _inMachine;

    [ObservableProperty]
    private bool _automatic;

    public string Where => InMachine ? "In the machine" : "On this computer";

    public string ActionText => InMachine ? "Give back" : "Connect";

    partial void OnAutomaticChanged(bool value) => panel.SetAutomatic(this, value);

    [RelayCommand]
    private Task ToggleAsync() => panel.ToggleAsync(this);

    [RelayCommand]
    private void CopyFix() => panel.CopyFix(this);
}

/// <summary>
/// USB devices for one machine: everything plugged into this computer, connected to the running machine or given back
/// with one click, and the ones the machine takes at every power on.
/// </summary>
public sealed partial class UsbPanelViewModel : ObservableObject
{
    private readonly MachineViewModel _machine;
    private readonly ShellViewModel _shell;
    private bool _loading;

    public UsbPanelViewModel(MachineViewModel machine, ShellViewModel shell)
    {
        _machine = machine;
        _shell = shell;
        _ = RefreshAsync();
    }

    public ObservableCollection<UsbRow> Devices { get; } = [];

    public string Title => $"USB devices for {_machine.Name}";

    public bool Running => _machine.Watcher is not null;

    public string Explain => Running
        ? "Connect hands a device to the machine at once; it leaves this computer until you give it back or the machine stops."
        : "The machine is off. Choose the devices it takes at every power on; Connect works while it runs.";

    [ObservableProperty] private string _note = "";

    public event Action? Closed;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var watcher = _machine.Watcher;
        var (devices, attached) = await Task.Run(async () =>
        {
            var list = UsbHost.List();
            var ids = watcher is null ? new HashSet<string>() : await UsbHost.AttachedAsync(watcher).ConfigureAwait(false);
            return (list, ids);
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            _loading = true;
            Devices.Clear();
            foreach (var d in devices)
            {
                Devices.Add(new UsbRow(d, this)
                {
                    InMachine = attached.Contains(d.QemuId),
                    Automatic = _machine.Machine.Usb.Passthrough.Any(u => u.VendorId == d.VendorId && u.ProductId == d.ProductId),
                });
            }

            _loading = false;
            Note = devices.Count == 0 ? "No USB devices were found on this computer." : "";
        });
    }

    public async Task ToggleAsync(UsbRow row)
    {
        if (_machine.Watcher is not { } watcher)
        {
            Ui.Post(() => Note = "Power the machine on first, or tick \"At every power on\".");
            return;
        }

        if (!row.InMachine && row.NeedsPermission)
        {
            Ui.Post(() => Note = $"Your account may not hand {row.Name} to a machine yet. Run the command shown under it once, then try again.");
            return;
        }

        try
        {
            if (row.InMachine) await UsbHost.DetachAsync(watcher, row.Device).ConfigureAwait(false);
            else await UsbHost.AttachAsync(watcher, row.Device).ConfigureAwait(false);
            Ui.Post(() =>
            {
                row.InMachine = !row.InMachine;
                Note = row.InMachine ? $"{row.Name} is in {_machine.Name} now." : $"{row.Name} is back on this computer.";
            });
        }
        catch (Exception ex) when (ex is QmpException or IOException)
        {
            Ui.Post(() => Note = $"{row.Name} could not be moved: {ex.Message}");
        }
    }

    public void SetAutomatic(UsbRow row, bool automatic)
    {
        if (_loading) return;
        var before = _machine.Machine;
        var list = before.Usb.Passthrough.Where(u => !(u.VendorId == row.Device.VendorId && u.ProductId == row.Device.ProductId)).ToList();
        if (automatic) list.Add(new UsbDevice(row.Device.VendorId, row.Device.ProductId, row.Name));
        var after = before with { Usb = before.Usb with { Passthrough = list } };
        var target = _machine.IsQuickemu ? Path.Combine(_machine.HistoryDirectory, MachineStore.FileName) : Path.Combine(_machine.Folder, MachineStore.FileName);
        var history = _machine.HistoryDirectory;
        _ = Task.Run(() => SettingsHistory.Save(before, after, target, history)).ContinueWith(_ => Ui.Post(() =>
        {
            _machine.Reload(after);
            Note = automatic ? $"{row.Name} will be connected at every power on." : $"{row.Name} stays on this computer at power on.";
        }), TaskScheduler.Default);
    }

    public void CopyFix(UsbRow row) => _shell.CopyToClipboard(row.PermissionFix, "The command is on the clipboard. Paste it into a terminal, then unplug the device and plug it in again.");

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
