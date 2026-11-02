using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>One drive of this computer in the panel: what it is, whether the machine has it, and whether it may write.</summary>
public sealed partial class DriveRow(HostDrive drive, DrivesPanelViewModel panel) : ObservableObject
{
    public HostDrive Drive { get; } = drive;

    public string Name => Drive.Model;

    public string Detail
    {
        get
        {
            var parts = new List<string> { Drive.DevicePath, Drive.Empty ? (Drive.Kind == HostDriveKind.Optical ? "no disc" : "no media") : HostDrive.FormatSize(Drive.SizeBytes) };
            if (Drive.Transport is { Length: > 0 } t) parts.Add(t.ToUpperInvariant());
            if (Drive.FileSystem is { Length: > 0 } fs) parts.Add(fs);
            if (Drive.Mounts.Count > 0) parts.Add("mounted at " + string.Join(", ", Drive.Mounts));
            return string.Join(" · ", parts);
        }
    }

    public bool IsPartition => Drive.Kind == HostDriveKind.Partition;

    public Thickness Indent => IsPartition ? new Thickness(28, 0, 0, 0) : default;

    public Avalonia.Media.Geometry? Icon =>
        Avalonia.Application.Current?.TryGetResource(Drive.Kind == HostDriveKind.Optical ? "IconDisc" : Drive.Transport == "usb" ? "IconUsb" : "IconDisk", null, out var g) == true ? g as Avalonia.Media.Geometry : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ActionText), nameof(IsHere), nameof(IsBusyHere), nameof(CanMove))]
    private bool _inMachine;

    /// <summary>Connected writable right now (what QEMU was given, which may differ from the saved setting).</summary>
    public bool WritableNow { get; set; }

    public string StateText => InMachine ? $"In {panel.MachineName}{(WritableNow ? "" : ", read-only")}"
        : Drive.HoldsSystem ? "This computer's system"
        : Drive.Empty ? (Drive.Kind == HostDriveKind.Optical ? "No disc" : "No media")
        : Drive.InUse ? "In use here"
        : "On this computer";

    public bool IsHere => !InMachine && !Drive.InUse && !Drive.HoldsSystem && !Drive.Empty;

    public bool IsBusyHere => !InMachine && (Drive.InUse || Drive.HoldsSystem || Drive.Empty);

    public bool CanMove => InMachine || !Drive.Empty;

    public string ActionText => InMachine ? "Give back" : "Connect";

    [ObservableProperty]
    private bool _automatic;

    [ObservableProperty]
    private bool _writable;

    /// <summary>Writing is offered only where it cannot hurt this computer; the tip says why it is not.</summary>
    public bool CanWrite => Drive.WriteRefusal is null;

    public string WriteTip => Drive.WriteRefusal is { } why
        ? $"The machine can only read this drive: {why}."
        : "Let the machine change what is on the drive. Off, the guest can read it but nothing it does reaches the drive.";

    /// <summary>Set while the panel puts back a value the person did not confirm.</summary>
    internal bool Quiet { get; set; }

    partial void OnAutomaticChanged(bool value)
    {
        if (!Quiet) panel.SetAutomatic(this);
    }

    partial void OnWritableChanged(bool value)
    {
        if (!Quiet) panel.SetWritable(this, value);
    }

    [RelayCommand]
    private Task ToggleAsync() => panel.ToggleAsync(this);
}

/// <summary>
/// The drives of this computer for one machine: whole disks, partitions and disc drives, each connected to the running
/// machine or given back with one click, the ones it connects at every power on, and which of them it may write to.
/// Reading is the default; writing needs the drive's name typed, and is never offered for a drive this computer uses.
/// </summary>
public sealed partial class DrivesPanelViewModel : ObservableObject
{
    private readonly MachineViewModel _machine;
    private readonly ShellViewModel _shell;
    private bool _loading;

    public DrivesPanelViewModel(MachineViewModel machine, ShellViewModel shell)
    {
        _machine = machine;
        _shell = shell;
        _ = RefreshAsync();
    }

    public ObservableCollection<DriveRow> Drives { get; } = [];

    public string Title => $"Host drives for {_machine.Name}";

    public string MachineName => _machine.Name;

    public bool Running => _machine.Watcher is not null;

    public string Explain => Running
        ? "Connect plugs a drive into the machine as a USB disk; the guest sees it at once. Your password may be asked for once, as when GNOME Disks opens a drive."
        : "The machine is off. Choose the drives it connects at every power on; Connect works while it runs. Drives are read-only unless you allow writing.";

    [ObservableProperty] private string _note = "";

    public event Action? Closed;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var running = _machine.Running;
        var (drives, connected) = await Task.Run(async () =>
        {
            var list = HostDrives.List();
            IReadOnlyList<ConnectedHostDrive> now = [];
            if (running is not null)
            {
                try
                {
                    await using var qmp = await MachineRunner.ControlAsync(running).ConfigureAwait(false);
                    now = await LiveHostDrives.ListAsync(qmp).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or QmpException or System.Net.Sockets.SocketException)
                {
                }
            }

            return (list, now);
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            _loading = true;
            Drives.Clear();
            foreach (var d in drives)
            {
                var saved = _machine.Machine.HostDrives.FirstOrDefault(h => h.Path == d.StablePath);
                var live = connected.FirstOrDefault(c => c.Path == d.StablePath);
                var row = new DriveRow(d, this) { Quiet = true, InMachine = live is not null, WritableNow = live?.Writable == true, Automatic = saved is not null, Writable = saved?.Writable == true };
                row.Quiet = false;
                Drives.Add(row);
            }

            _loading = false;
            Note = drives.Count == 0 ? "No drives were found on this computer." : "";
        });
    }

    public async Task ToggleAsync(DriveRow row)
    {
        if (_machine.Running is not { } running)
        {
            Ui.Post(() => Note = "Power the machine on first, or tick \"Always connect\".");
            return;
        }

        Ui.Post(() => Note = row.InMachine ? $"Giving {row.Name} back…" : $"Connecting {row.Name}…");
        try
        {
            await using var qmp = await MachineRunner.ControlAsync(running).ConfigureAwait(false);
            if (row.InMachine) await LiveHostDrives.DetachAsync(qmp, row.Drive.StablePath).ConfigureAwait(false);
            else await LiveHostDrives.AttachAsync(qmp, new HostDriveConfig { Path = row.Drive.StablePath, Name = row.Drive.Describe(), Kind = row.Drive.Kind, Writable = row.Writable && row.CanWrite }).ConfigureAwait(false);
            Ui.Post(() =>
            {
                row.WritableNow = !row.InMachine && row.Writable && row.CanWrite;
                row.InMachine = !row.InMachine;
                Note = row.InMachine ? $"{row.Name} is in {_machine.Name} now{(row.WritableNow ? "" : ", read-only")}. Eject it in the guest before giving it back." : $"{row.Name} is back on this computer.";
            });
        }
        catch (Exception ex) when (ex is DriveAccessException or QmpException or IOException or System.Net.Sockets.SocketException)
        {
            Ui.Post(() => Note = $"{row.Name} could not be {(row.InMachine ? "given back" : "connected")}: {(ex is QmpException q ? q.Description : ex.Message)}");
        }
    }

    public void SetAutomatic(DriveRow row)
    {
        if (_loading) return;
        var automatic = row.Automatic;
        var writable = row.Writable;
        _ = Task.Run(() =>
        {
            _machine.SetHostDrive(row.Drive, automatic, writable);
            Ui.Post(() => Note = automatic ? $"{row.Name} will be connected at every power on{(writable ? ", writable" : ", read-only")}." : $"{row.Name} is no longer connected at power on.");
        });
    }

    /// <summary>Writing is the one choice that can lose data: the drive's own device name is typed to confirm it.</summary>
    public void SetWritable(DriveRow row, bool on)
    {
        if (_loading) return;
        if (!on)
        {
            Save(row);
            return;
        }

        if (!row.CanWrite)
        {
            Ui.Post(() =>
            {
                row.Quiet = true;
                row.Writable = false;
                row.Quiet = false;
                Note = row.WriteTip;
            });
            return;
        }

        var name = row.Drive.Name;
        var prompt = new PromptViewModel($"Let {_machine.Name} write to {row.Name}?",
            [
                $"The guest will be able to change or erase everything on {row.Drive.DevicePath} ({HostDrive.FormatSize(row.Drive.SizeBytes)}).",
                "While the machine has it, this computer cannot mount it, so the two never write at once.",
                $"Type {name} to confirm.",
            ], "Drive name", "", "Allow writing", t => t.Trim() == name, isDanger: true);
        _ = Task.Run(async () =>
        {
            var answer = await prompt.Answer.ConfigureAwait(false);
            prompt.Finish();
            Ui.Post(() =>
            {
                if (answer == name)
                {
                    Save(row);
                    if (row.InMachine && !row.WritableNow) Note = $"{row.Name} may be written to from the next time it is connected. Give it back and connect it again to write now.";
                }
                else
                {
                    row.Quiet = true;
                    row.Writable = false;
                    row.Quiet = false;
                }
            });
        });
        Ui.Post(() => _shell.ShowPrompt(prompt));
    }

    private void Save(DriveRow row)
    {
        if (!row.Automatic) return; // the choice applies to the next Connect; only a saved drive keeps it
        SetAutomatic(row);
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
