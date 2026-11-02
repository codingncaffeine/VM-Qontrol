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

    public bool NeedsPermission => !Device.Writable;

    /// <summary>Connect / Give back are offered once the account may open the device; until then, Allow….</summary>
    public bool CanMove => !NeedsPermission || InMachine;

    public bool ShowAllow => !CanMove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Where), nameof(ActionText), nameof(CanMove), nameof(ShowAllow), nameof(StateText), nameof(IsHere), nameof(IsLocked))]
    private bool _inMachine;

    /// <summary>The state pill: where the device is, or that it needs the one-time permission.</summary>
    public string StateText => InMachine ? $"In {panel.MachineName}" : NeedsPermission ? "Needs permission" : "On this computer";

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

    public string LockedText => "Linux lets only the system open a USB device until a one-time rule allows your account. Choose Allow… on a device to see its command; VM Qontrol never asks for your password itself.";

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
            Ui.Post(() => Note = $"Your account may not hand {row.Name} to a machine yet. Run the command shown under it once, then try again.");
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

    /// <summary>The one-time permission for these devices: the command, shown and copied, never run by VM Qontrol.</summary>
    public void Allow(IReadOnlyList<UsbRow> rows)
    {
        if (rows.Count == 0) return;
        var command = string.Join(" && ", rows.Select(r => UsbHost.PermissionFix(r.Device)));
        var one = rows.Count == 1 ? rows[0] : null;
        var notice = new NoticeViewModel(one is null ? $"Allow {rows.Count} USB devices" : "Allow this USB device",
            ["Linux lets only the system open USB devices unless a rule allows your account. This command adds that rule for "
             + (one is null ? "these kinds of device" : "this kind of device") + " (it needs your password once). Run it in a terminal, then unplug the device and plug it in again."],
            confirm: "Copy the command", dismiss: "Not now", icon: "IconUsb",
            itemTitle: one?.Name ?? string.Join(", ", rows.Select(r => r.Name).Take(3)) + (rows.Count > 3 ? $" and {rows.Count - 3} more" : ""),
            itemDetail: one?.Detail ?? $"{rows.Count} devices", code: command);
        _shell.Show(notice);
        _ = notice.Answer.ContinueWith(t =>
        {
            if (t.Result) Ui.Post(() => _shell.CopyToClipboard(command, "The command is on the clipboard. Paste it into a terminal, then unplug the devices and plug them in again."));
        }, TaskScheduler.Default);
    }


    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
