using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>One USB device in the panel: where it is now, and whether the machine always takes it (a rule).</summary>
public sealed partial class UsbRow(HostUsbDevice device, UsbPanelViewModel panel) : ObservableObject
{
    public HostUsbDevice Device { get; } = device;

    public string Name => Device.Name;

    public string Detail => $"{Device.Id} · {Device.Speed} · port {Device.Port}";

    public string? Advice => Device.Advice;

    public bool HasAdvice => Advice is not null;

    /// <summary>Not offered to a machine when plugged in (the person chose Don't ask about this device).</summary>
    [ObservableProperty]
    private bool _neverAsk;

    [RelayCommand]
    private void OfferAgain()
    {
        NeverAsk = false;
        panel.OfferAgain(this);
    }

    /// <summary>Only when this computer lacks spice-gtk's USB helper: otherwise Connect opens the device itself.</summary>
    public bool NeedsPermission => !Device.Writable && !UsbAccess.Available;

    /// <summary>Connect / Give back are offered once the account may open the device; until then, Allow….</summary>
    public bool CanMove => !NeedsPermission || InMachine;

    public bool ShowAllow => !CanMove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Where), nameof(ActionText), nameof(CanMove), nameof(ShowAllow), nameof(StateText), nameof(IsHere), nameof(IsLocked))]
    private bool _inMachine;

    /// <summary>The state pill: where the device is, or that this computer needs the USB helper first.</summary>
    public string StateText => InMachine ? $"In {panel.MachineName}" : NeedsPermission ? "Needs setup" : "On this computer";

    public bool IsHere => !InMachine && !NeedsPermission;

    public bool IsLocked => !InMachine && NeedsPermission;

    [ObservableProperty]
    private bool _automatic;

    /// <summary>In the machine through its rule (connected by kind, not by port), so giving it back takes the rule out.</summary>
    public bool ByRule { get; set; }

    public string Where => InMachine ? ByRule ? "In the machine (connects automatically)" : "In the machine" : "On this computer";

    public string ActionText => InMachine ? "Give back" : "Connect";

    partial void OnAutomaticChanged(bool value) => panel.SetAutomatic(this, value);

    [RelayCommand]
    private Task ToggleAsync() => panel.ToggleAsync(this);

    [RelayCommand]
    private void Allow() => panel.Allow([this]);
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

    public string MachineName => _machine.Name;

    /// <summary>Devices this account may not open yet (the banner offers one command for all of them).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocked), nameof(LockedText))]
    private int _lockedCount;

    public bool HasLocked => LockedCount > 0;

    public string LockedText => "This computer lacks spice-gtk's USB helper, which opens USB devices to you (virt-viewer and GNOME Boxes use the same one). Choose Set up… to install it once.";

    public bool Running => _machine.Watcher is not null;

    public string Explain => Running
        ? "Connect hands a device to the machine at once; it leaves this computer until you give it back or the machine stops."
        : "The machine is off. Choose the devices it always connects (at every power on, and when plugged in while it runs); Connect works while it runs.";

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
                var byRule = attached.Contains(d.RuleId) && d.Writable;
                Devices.Add(new UsbRow(d, this)
                {
                    ByRule = byRule,
                    InMachine = attached.Contains(d.QemuId) || byRule,
                    Automatic = _machine.Machine.Usb.Passthrough.Any(u => u.VendorId == d.VendorId && u.ProductId == d.ProductId),
                    NeverAsk = _shell.Settings.UsbNeverAsk.Contains(d.Id),
                });
            }

            _loading = false;
            LockedCount = Devices.Count(r => r.IsLocked);
            Note = devices.Count == 0 ? "No USB devices were found on this computer." : "";
        });
    }

    public async Task ToggleAsync(UsbRow row)
    {
        if (_machine.Watcher is not { } watcher)
        {
            Ui.Post(() => Note = "Power the machine on first, or tick \"Always connect\".");
            return;
        }

        if (!row.InMachine && row.NeedsPermission)
        {
            Ui.Post(() => _shell.ShowUsbSetup(row.Name));
            return;
        }

        try
        {
            var byRule = row.InMachine && row.ByRule;
            if (byRule) await UsbHost.RemoveRuleAsync(watcher, row.Device.VendorId, row.Device.ProductId).ConfigureAwait(false);
            else if (row.InMachine) await UsbHost.DetachAsync(watcher, row.Device).ConfigureAwait(false);
            else await UsbHost.AttachAsync(watcher, row.Device).ConfigureAwait(false);
            Ui.Post(() =>
            {
                row.ByRule = false;
                row.InMachine = !row.InMachine;
                Note = row.InMachine ? $"{row.Name} is in {_machine.Name} now."
                    : byRule ? $"{row.Name} is back on this computer until {_machine.Name} next powers on (it is set to always connect)."
                    : $"{row.Name} is back on this computer.";
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
        var running = _machine.Watcher is not null;
        _ = Task.Run(async () =>
        {
            var live = await _machine.SetUsbRuleAsync(row.Device, automatic).ConfigureAwait(false);
            Ui.Post(() => Note = live is not null ? $"Saved for the next power on; the running machine refused it now: {live}"
                : automatic ? running ? $"{row.Name} connects to {_machine.Name} now, whenever it is plugged in, and at every power on." : $"{row.Name} will be connected at every power on."
                : $"{row.Name} stays on this computer.");
            if (running) await RefreshAsync().ConfigureAwait(false);
        });
    }

    public void OfferAgain(UsbRow row)
    {
        _shell.SetUsbNeverAsk(row.Device.Id, false);
        Note = $"{row.Name} is offered to a running machine again when it is plugged in.";
    }

    /// <summary>The helper is missing: say which package brings it.</summary>
    public void Allow(IReadOnlyList<UsbRow> rows)
    {
        if (rows.Count > 0) _shell.ShowUsbSetup(rows[0].Name);
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
