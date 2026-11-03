using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;
using VmQontrol.Core.Snapshots;

namespace VmQontrol.App.ViewModels;

public sealed record DetailRow(string Label, string Value);

/// <summary>One machine in the library: its summary, its live state and screen, and its power commands.</summary>
public sealed partial class MachineViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private MachineWatcher? _watcher;
    private CancellationTokenSource? _thumbnailLoop;

    public MachineViewModel(ShellViewModel shell, LibraryEntry entry)
    {
        _shell = shell;
        Entry = entry;
        Machine = entry.Machine!;
        Problems = LibraryScanner.MissingFiles(entry);
        Logs = new LogViewModel(shell.Paths, Machine);
        Console.CopyRequested = text => shell.CopyToClipboard(text, "The console output is on the clipboard.");
        Snapshots = new SnapshotsViewModel(this, shell);
        _ = RefreshSuspendedAsync();
        _ = RefreshInstallAsync();
    }

    public PerformanceViewModel Performance { get; } = new();

    public ConsoleViewModel Console { get; } = new();

    public LogViewModel Logs { get; }

    public SnapshotsViewModel Snapshots { get; }

    private int _cores = 1;
    private long _memoryMiB = 1024;

    /// <summary>0 Summary, 1 Snapshots, 2 Performance, 3 Console, 4 Log: work for a tab runs only while it is open.</summary>
    [ObservableProperty]
    private int _tab;

    partial void OnTabChanged(int value) => UpdateTabWork();

    private void UpdateTabWork()
    {
        if (Tab == 1) _ = Snapshots.LoadAsync();
        if (Tab == 2 && IsSelected && _watcher is { } w) Performance.Start(w, _cores, _memoryMiB);
        else Performance.Stop();
        if (Tab == 3 && IsSelected && _watcher?.Machine is { } r) _ = Console.ConnectAsync(r);
        if (Tab == 4) _ = Logs.LoadAsync();
    }

    public LibraryEntry Entry { get; private set; }

    public MachineConfig Machine { get; private set; }

    public string Id => Machine.Id;

    public string Name => Machine.Name;

    public string GuestLabel => GuestNames.Describe(Machine.Guest);

    /// <summary>The sidebar's line under the name: it has no state text, so a template says so here.</summary>
    public string LibraryLabel => Install.Stage == Core.Install.InstallStage.Installing ? $"Installing {InstallSystem}…" : GuestLabel + (IsTemplate ? " · Template" : "");

    /// <summary>A template is never started, only cloned (see <see cref="MachineConfig.IsTemplate"/>).</summary>
    public bool IsTemplate => Machine.IsTemplate;

    public bool CanPowerOn => IsOff && !IsTemplate;

    public bool CanMakeTemplate => IsOff && !IsTemplate && !IsQuickemu;

    /// <summary>Two letters for the sidebar badge (no vendor logos).</summary>
    public string Badge => GuestNames.Badge(Machine.Guest);

    public string BadgeFamily => GuestNames.Family(Machine.Guest);

    public bool FamilyWindows => BadgeFamily == "windows";

    public bool FamilyApple => BadgeFamily == "apple";

    public bool FamilyLinux => BadgeFamily == "linux";

    public bool FamilyBsd => BadgeFamily == "bsd";

    public string KindLabel => Entry.Kind == MachineKind.Quickemu ? "quickemu machine" : "VM Qontrol machine";

    public bool IsQuickemu => Entry.Kind == MachineKind.Quickemu;

    public string Folder => Entry.Directory;

    public IReadOnlyList<string> Problems { get; private set; }

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    private IReadOnlyList<DetailRow> _details = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsOff), nameof(IsPaused), nameof(CanPause), nameof(IsActive), nameof(ScreenPlaceholder),
        nameof(CanOpenFirmware), nameof(CanDiscardSuspended), nameof(CanPowerOn), nameof(CanMakeTemplate), nameof(GuestToolsStatus), nameof(GuestToolsDot), nameof(GuestToolsWarn), nameof(GuestToolsOk),
        nameof(CanConnectCable), nameof(CanDisconnectCable), nameof(CanCapture), nameof(DiscardsChanges), nameof(CanOpenDisk), nameof(CanOpenDiskForChanges))]
    private PowerState _state = PowerState.Off;

    /// <summary>Off with its state saved: Power on carries on from where it was.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ScreenPlaceholder), nameof(PowerOnText), nameof(PowerOnTip), nameof(CanOpenFirmware), nameof(CanDiscardSuspended), nameof(CanOpenDiskForChanges))]
    private bool _isSuspended;

    private bool _suspending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(StateText), nameof(CanRecord))]
    private bool _busy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreen))]
    private Bitmap? _screen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuestAddressText))]
    private string? _guestAddress;

    [ObservableProperty]
    private string? _sshCommand;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The library folder (group) the machine is in; empty = none.</summary>
    [ObservableProperty]
    private string _groupName = "";

    public bool HasGroup => GroupName.Length > 0;

    public string GroupPowerOnText => $"Power on everything in {GroupName}";

    public string GroupShutDownText => $"Shut down everything in {GroupName}";

    partial void OnGroupNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasGroup));
        OnPropertyChanged(nameof(GroupPowerOnText));
        OnPropertyChanged(nameof(GroupShutDownText));
    }

    public bool HasNotes => !string.IsNullOrWhiteSpace(Machine.Notes);

    public string Notes => Machine.Notes;

    public bool HasScreen => Screen is not null;

    public bool IsRunning => State is PowerState.Running or PowerState.ShuttingDown;

    public bool IsPaused => State == PowerState.Paused;

    public bool IsOff => State == PowerState.Off;

    public bool IsActive => State != PowerState.Off;

    public bool CanPause => State == PowerState.Running;

    public string PowerOnText => IsSuspended ? "Resume" : "Power on";

    public string PowerOnTip => IsSuspended ? "Carry on from where the machine was suspended, with every program as it was (Ctrl+B)" : "Start the machine and open its screen (Ctrl+B)";

    /// <summary>The firmware screen means a fresh start, so a suspended machine offers Discard first.</summary>
    public bool CanOpenFirmware => IsOff && !IsSuspended && !IsTemplate;

    public bool CanDiscardSuspended => IsOff && IsSuspended;

    public string StateText => Busy && State == PowerState.Off ? "Starting…" : State switch
    {
        PowerState.Running => "Running",
        PowerState.Paused => "Paused",
        PowerState.ShuttingDown => "Shutting down…",
        PowerState.Crashed => "Crashed",
        _ => IsTemplate ? "Template" : IsSuspended ? "Suspended" : "Powered off",
    };

    public string ScreenPlaceholder => State != PowerState.Off ? "Waiting for the screen…"
        : IsTemplate ? "A template is never started: make a machine from it" : IsSuspended ? "Suspended" : "Powered off";

    public RunningMachine? Running => _watcher?.Machine;

    /// <summary>The live connection to the running machine, for settings that apply at once (changing a disc).</summary>
    public MachineWatcher? Watcher => _watcher;

    /// <summary>Where the settings history is kept: beside vm.json, or in VM Qontrol's own state for a quickemu machine.</summary>
    public string HistoryDirectory => IsQuickemu ? Path.Combine(_shell.Paths.State, "machines", Machine.Id) : Folder;

    /// <summary>Takes new settings (after Save in Settings) and refreshes everything shown from them.</summary>
    /// <summary>
    /// Saves (or drops) the rule that always connects this kind of USB device to the machine, and while it runs puts the
    /// rule in (or takes it out) at once. Returns QEMU's reason when the running machine refused it, else <c>null</c>.
    /// Call on a worker.
    /// </summary>
    public async Task<string?> SetUsbRuleAsync(HostUsbDevice device, bool on)
    {
        var before = Machine;
        var list = before.Usb.Passthrough.Where(u => !(u.VendorId == device.VendorId && u.ProductId == device.ProductId)).ToList();
        if (on) list.Add(new UsbDevice(device.VendorId, device.ProductId, device.Name));
        var after = before with { Usb = before.Usb with { Passthrough = list } };
        var target = IsQuickemu ? Path.Combine(HistoryDirectory, MachineStore.FileName) : Path.Combine(Folder, MachineStore.FileName);
        SettingsHistory.Save(before, after, target, HistoryDirectory);
        Ui.Post(() => Reload(after));
        if (Watcher is not { } watcher) return null;
        try
        {
            if (on) await UsbHost.AddRuleAsync(watcher, device.VendorId, device.ProductId).ConfigureAwait(false);
            else await UsbHost.RemoveRuleAsync(watcher, device.VendorId, device.ProductId).ConfigureAwait(false);
            return null;
        }
        catch (QmpException ex)
        {
            return ex.Message;
        }
    }

    // ── Guest tools: whether the agent answers, and how to install the tools when it does not.

    private string? _guestReleaseId;

    /// <summary>The guest agent answered the last poll; <c>null</c> = not asked yet (off, or just started).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuestToolsStatus))]
    [NotifyPropertyChangedFor(nameof(GuestToolsDot))]
    [NotifyPropertyChangedFor(nameof(GuestToolsWarn))]
    [NotifyPropertyChangedFor(nameof(GuestToolsOk))]
    private bool? _guestToolsAnswering;

    /// <summary>
    /// The machine has run past <see cref="ClipboardGrace"/> and its guest agent still does not answer: the tools are
    /// taken as not installed (the card says so in the warning color, the header carries a pill that leads to it).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuestToolsStatus))]
    [NotifyPropertyChangedFor(nameof(GuestToolsPrimary))]
    [NotifyPropertyChangedFor(nameof(GuestAddressText))]
    private bool _guestToolsMissing;

    /// <summary>The address line: the guest's address, or why there is none yet.</summary>
    public string GuestAddressText => GuestAddress ?? (GuestToolsMissing ? "Needs the guest tools (not installed)" : "Waiting for the guest tools…");

    /// <summary>"How to install" is the card's main button while the tools are missing.</summary>
    public bool GuestToolsPrimary => GuestToolsMissing && !ShowGuestToolsSteps;

    [RelayCommand]
    private void ShowGuestTools() => RevealGuestTools();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuestToolsStepsButton))]
    [NotifyPropertyChangedFor(nameof(GuestToolsPrimary))]
    private bool _showGuestToolsSteps;

    public string GuestToolsStepsButton => ShowGuestToolsSteps ? "Hide the steps" : "How to install";

    public bool GuestToolsWarn => GuestToolsDot == "warn";

    public bool GuestToolsOk => GuestToolsDot == "ok";

    public GuestToolsAdvice GuestToolsAdvice => GuestTools.Advise(Machine.Guest, _guestReleaseId);

    public string GuestToolsStatus => !IsActive ? "Power the machine on to check for them." : GuestToolsAnswering switch
    {
        true => "Running: the address, a clean shut down and the clock work through them.",
        false when GuestToolsMissing => "Not installed. Copy and paste between this computer and the machine needs them, and so do a screen that follows the window's size, the machine's address and a clean shut down.",
        false => "Not answering yet: the system is still starting (or the tools are not installed).",
        null => "Checking…",
    };

    /// <summary>"ok", "warn" or "off": the color of the status dot.</summary>
    public string GuestToolsDot => !IsActive || GuestToolsAnswering is null ? "off" : GuestToolsAnswering == true ? "ok" : "warn";

    [RelayCommand]
    private void ToggleGuestToolsSteps() => ShowGuestToolsSteps = !ShowGuestToolsSteps;

    /// <summary>The page shows its Guest tools card (scrolled into view), with the steps open.</summary>
    public event EventHandler? GuestToolsRevealed;

    public void RevealGuestTools()
    {
        Tab = 0;
        ShowGuestToolsSteps = true;
        _revealPending = true;
        GuestToolsRevealed?.Invoke(this, EventArgs.Empty);
    }

    private bool _revealPending;

    /// <summary>A reveal asked for before the page existed (the hint clicked from another page), taken once by the page.</summary>
    public bool TakeRevealRequest()
    {
        var pending = _revealPending;
        _revealPending = false;
        return pending;
    }

    [RelayCommand]
    private void CopyGuestToolsCommand()
    {
        if (GuestToolsAdvice.Command is { } command) _shell.CopyToClipboard(command, "The command is on the clipboard. Paste it in the machine's terminal.");
    }

    /// <summary>
    /// A portable copy into <paramref name="destination"/> (a folder, or one .tar there). The machine must be off: a disk
    /// being written is no copy. Progress in the status line; what to know in a notice at the end.
    /// </summary>
    public async Task ExportAsync(string destination, bool asTar)
    {
        if (!IsOff)
        {
            _shell.Show(new NoticeViewModel($"Shut {Name} down first", ["A copy of a running machine's disk would be caught half-written. Shut it down (or suspend it), then export."]));
            return;
        }

        var machine = Machine;
        var dir = Folder;
        var progress = new Progress<Core.Import.ImportProgress>(p => Ui.Post(() => _shell.Note($"Exporting {Name}: {p.Status} {p.Percent:0}%")));
        try
        {
            var (path, notes) = await Task.Run(() => MachineExport.ExportAsync(machine, dir, destination, asTar, progress, CancellationToken.None)).ConfigureAwait(false);
            Ui.Post(() =>
            {
                _shell.Note($"{Name} is exported to {path}.");
                _shell.Show(new NoticeViewModel($"{Name} is exported", [$"The copy is in {path}, with a checksum of every file.", .. notes,
                    asTar ? "On any computer, Import a machine brings it back." : "On any computer, Add machines from a folder lists it (or Import a machine copies it in)."]));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{Name}: export failed ({ex.Message}).");
            Ui.Post(() => _shell.Show(new NoticeViewModel($"{Name} was not exported", [ex.Message, "Nothing of the copy was kept; the machine is as it was."], isError: true)));
        }
    }

    /// <summary>Moves the machine's folder into <paramref name="parent"/> (machine off); the library follows it there.</summary>
    public async Task MoveAsync(string parent)
    {
        var machine = Machine;
        var dir = Folder;
        if (await Task.Run(() => MachineMove.Refusal(dir, parent, !IsOff, IsQuickemu)).ConfigureAwait(false) is { } why)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel($"{Name} was not moved", [why], isError: true)));
            return;
        }

        Ui.Post(() => _shell.Note($"Moving {Name}… (a move to another drive copies every disk)"));
        try
        {
            var moved = await Task.Run(() => MachineMove.MoveAsync(machine, dir, parent)).ConfigureAwait(false);
            Log.Info($"{Name}: moved from {dir} to {moved}.");
            await _shell.FinishMoveAsync(moved).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{Name}: move failed ({ex.Message}).");
            Ui.Post(() => _shell.Show(new NoticeViewModel($"{Name} was not moved", [ex.Message], isError: true)));
        }
    }

    [RelayCommand]
    private async Task ExportQuickemuAsync()
    {
        var machine = Machine;
        var dir = Folder;
        try
        {
            var conf = await Task.Run(() => MachineExport.WriteQuickemuConf(machine, dir)).ConfigureAwait(false);
            Ui.Post(() => _shell.Show(new NoticeViewModel("A quickemu .conf is written", [$"quickemu --vm \"{conf}\" runs this machine's disk.",
                "The library still lists the machine once. Do not run it from quickemu and from VM Qontrol at the same time.",
                "quickemu keeps TPM state beside the disk, not in tpm/: a Windows 11 machine there starts with a new TPM."])));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("The .conf was not written", [ex.Message], isError: true)));
        }
    }

    /// <summary>The app, for the guest tools download's progress shown on this page.</summary>
    public ShellViewModel Shell => _shell;

    /// <summary>The guest tools disc into the machine: the machine's own, the one chosen or downloaded before, or downloaded now.</summary>
    [RelayCommand]
    private async Task InsertGuestToolsAsync()
    {
        var machine = Machine;
        var folder = Folder;
        var path = await Task.Run(() => GuestTools.MachineDriversDisc(machine, folder) is { } own && Core.Downloads.GuestToolsDownload.IsDriverDisc(own) ? own : null).ConfigureAwait(false)
                   ?? await _shell.GetGuestToolsAsync().ConfigureAwait(false);
        if (path is not null) await Task.Run(() => InsertDriversDiscAsync(path)).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the VirtIO driver disc in the machine's drive: at once while it runs, else (or when every drive is in use)
    /// as a disc kept in the drive from the next power on. Call on a worker.
    /// </summary>
    public async Task InsertDriversDiscAsync(string path)
    {
        _shell.RememberDriversIso(path);

        if (Watcher is { } watcher)
        {
            try
            {
                await LiveMedia.ChangeAsync(watcher, null, path).ConfigureAwait(false);
                Ui.Post(() => _shell.Note($"The VirtIO driver disc is in {Name}'s drive. Open it in File Explorer."));
                return;
            }
            catch (QmpException ex)
            {
                Log.Warn($"{Name}: the driver disc did not go in live ({ex.Message}); kept for the next power on.");
            }
        }

        var before = Machine;
        if (!before.Media.Any(x => x.Role == MediaRole.Drivers && x.File == path))
        {
            var media = before.Media.Where(x => x.Role != MediaRole.Drivers).Append(new MediaConfig { Role = MediaRole.Drivers, File = path }).ToList();
            var after = before with { Media = media };
            var target = IsQuickemu ? Path.Combine(HistoryDirectory, MachineStore.FileName) : Path.Combine(Folder, MachineStore.FileName);
            SettingsHistory.Save(before, after, target, HistoryDirectory);
            Ui.Post(() => Reload(after));
        }

        Ui.Post(() => _shell.Note(IsActive
            ? $"{Name} has no free disc drive now; the driver disc goes in at its next power on."
            : $"The driver disc goes in {Name}'s drive when it powers on."));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn($"Could not open {url} ({ex.Message}).");
        }
    }

    public void Reload(MachineConfig machine)
    {
        Machine = machine;
        Entry = Entry with { Machine = machine };
        Problems = LibraryScanner.MissingFiles(Entry);
        OnPropertyChanged(string.Empty);
        _ = RefreshInstallAsync();
        _ = Task.Run(async () =>
        {
            var host = await _shell.HostAsync().ConfigureAwait(false);
            var details = BuildDetails(host);
            Ui.Post(() => Details = details);
        });
    }

    [RelayCommand]
    private void OpenSettings() => _shell.OpenSettings(this);

    [RelayCommand]
    private void OpenUsb() => _shell.OpenUsb(this);

    [RelayCommand]
    private void OpenDrives() => _shell.OpenDrives(this);

    /// <summary>A terminal with SSH into the machine through its forwarded port (the guest's own SSH server answers).</summary>
    [RelayCommand]
    private void OpenSsh()
    {
        if (SshCommand is not { } command) return;
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _ = Task.Run(() =>
        {
            var opened = Platform.TerminalLauncher.Run(parts);
            Ui.Post(() => _shell.Note(opened ? $"A terminal is connecting to {Name} over SSH." : "No terminal was found on this computer; the command is on the clipboard instead."));
            if (!opened) Ui.Post(() => _shell.CopyToClipboard(command, "The SSH command is on the clipboard."));
        });
    }

    [RelayCommand]
    private void CopySsh()
    {
        if (SshCommand is { } command) _shell.CopyToClipboard(command, "The SSH command is on the clipboard.");
    }

    public DrivesPanelViewModel OpenDrivesPanel() => _shell.OpenDrives(this);

    public void OpenNetworks() => _shell.OpenNetworks();

    /// <summary>Saves whether the machine connects a host drive at power on, and whether it may write to it. Call on a worker.</summary>
    public void SetHostDrive(HostDrive drive, bool on, bool writable)
    {
        var before = Machine;
        var list = before.HostDrives.Where(h => h.Path != drive.StablePath).ToList();
        if (on) list.Add(new HostDriveConfig { Path = drive.StablePath, Name = drive.Describe(), Kind = drive.Kind, Writable = writable && drive.WriteRefusal is null });
        var after = before with { HostDrives = list };
        var target = IsQuickemu ? Path.Combine(HistoryDirectory, MachineStore.FileName) : Path.Combine(Folder, MachineStore.FileName);
        SettingsHistory.Save(before, after, target, HistoryDirectory);
        Ui.Post(() => Reload(after));
    }

    /// <summary>The USB menu's devices: everything plugged in, and whether each is in this machine now. Call on a worker.</summary>
    public async Task<IReadOnlyList<(HostUsbDevice Device, bool InMachine)>> UsbMenuAsync()
    {
        var devices = UsbHost.List();
        var attached = Watcher is { } w ? await UsbHost.AttachedAsync(w).ConfigureAwait(false) : new HashSet<string>();
        return devices.Select(d => (d, attached.Contains(d.QemuId) || attached.Contains(d.RuleId))).ToList();
    }

    /// <summary>One click in the USB menu: the device goes to the machine, or comes back to this computer.</summary>
    public async Task ToggleUsbAsync(HostUsbDevice device, bool inMachine)
    {
        if (Watcher is not { } watcher) return;
        if (!inMachine && !device.Writable && !UsbAccess.Available)
        {
            Ui.Post(() => _shell.ShowUsbSetup(device.Name));
            return;
        }

        try
        {
            var attached = await UsbHost.AttachedAsync(watcher).ConfigureAwait(false);
            if (inMachine && attached.Contains(device.RuleId)) await UsbHost.RemoveRuleAsync(watcher, device.VendorId, device.ProductId).ConfigureAwait(false);
            else if (inMachine) await UsbHost.DetachAsync(watcher, device).ConfigureAwait(false);
            else await UsbHost.AttachAsync(watcher, device).ConfigureAwait(false);
            Ui.Post(() => _shell.Note(inMachine ? $"{device.Name} is back on this computer." : $"{device.Name} is in {Name} now."));
        }
        catch (Exception ex) when (ex is QmpException or IOException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel($"{device.Name} could not be {(inMachine ? "given back" : "connected")}", [ex.Message], isError: true)));
        }
    }

    /// <summary>Ctrl+Alt+Del for the machine (Windows' sign-in screen asks for it; the desktop would take the keys itself).</summary>
    [RelayCommand]
    private Task SendCtrlAltDelAsync() => ControlAsync("got Ctrl+Alt+Del", (r, ct) => MachineRunner.SendKeysAsync(r, ["ctrl", "alt", "delete"], ct));

    /// <summary>
    /// Key combinations this computer would take for itself if typed in the viewer (as virt-manager's Send Key menu):
    /// a QEMU key code list in the parameter, joined by "+" ("ctrl+alt+f2").
    /// </summary>
    [RelayCommand]
    private Task SendKeysAsync(string keys) =>
        ControlAsync($"got {Text.KeyLabel(keys)}", (r, ct) => MachineRunner.SendKeysAsync(r, keys.Split('+'), ct));

    /// <summary>Saves the machine's screen as a picture in the Pictures folder.</summary>
    [RelayCommand]
    private async Task SaveScreenshotAsync()
    {
        if (_watcher is not { } watcher) return;
        var pictures = UserFolder(Environment.SpecialFolder.MyPictures, "Pictures");
        var file = Path.Combine(pictures, $"{Name} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        try
        {
            Directory.CreateDirectory(pictures);
            await watcher.ScreenshotAsync(file).ConfigureAwait(false);
            Ui.Post(() => _shell.Note($"Saved the screen to {file}."));
        }
        catch (Exception ex) when (ex is QmpException or IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("The screen could not be saved", [ex.Message], isError: true)));
        }
    }

    /// <summary>The person's Pictures or Videos folder; inside the profile when it is isolated (a trial or a test).</summary>
    private string UserFolder(Environment.SpecialFolder folder, string name) =>
        _shell.Paths.Isolated ? Path.Combine(_shell.Paths.Data, name)
        : Environment.GetFolderPath(folder) is { Length: > 0 } f ? f : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name);

    // ── Discard changes at power off (Parallels' rollback mode, UTM's disposable mode).

    /// <summary>The running machine writes to a throwaway layer: its changes go at power off unless kept.</summary>
    public bool DiscardsChanges => IsActive && Machine.OnPowerOff != DiscardMode.Keep;

    /// <summary>Throws the session away, or asks Keep or Discard, once the machine is off (never for a suspended one).</summary>
    private async Task EndSessionAsync(DiscardMode mode)
    {
        var dir = Entry.Directory;
        if (await Task.Run(() => MachineSuspend.IsSuspended(dir) || !DiscardSession.Exists(dir)).ConfigureAwait(false)) return;
        var keep = false;
        if (mode == DiscardMode.Ask)
        {
            var notice = new NoticeViewModel($"Keep what changed in {Name}?",
                ["This machine is set to ask at power off. Keep writes everything done in this session into its disks; Discard puts them back as they were at power on.",
                 "Nobody answering counts as Discard: the next power on starts from the disks as they were."],
                confirm: "Keep the changes", dismiss: "Discard them");
            Ui.Post(() => _shell.Show(notice));
            keep = await notice.Answer.ConfigureAwait(false);
        }

        try
        {
            if (keep) await Task.Run(() => DiscardSession.KeepAsync(Machine, dir)).ConfigureAwait(false);
            else await Task.Run(() => DiscardSession.Discard(dir)).ConfigureAwait(false);
            Ui.Post(() => _shell.Note(keep ? $"{Name}'s changes were written into its disks." : $"{Name}'s changes were discarded; it starts from its disks as they were."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel(keep ? "The changes could not be kept" : "The changes could not be discarded", [ex.Message, "They wait in the machine's session folder; the next power on asks again or discards them."], isError: true)));
        }
    }

    // ── Network cable and traffic capture (Workstation's Connected, VirtualBox's network trace).

    /// <summary>The running machine's network cable is pulled out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectCable), nameof(CanDisconnectCable))]
    private bool _cableOut;

    /// <summary>The .pcap a running capture writes, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCapturing), nameof(CanCapture))]
    private string? _captureFile;

    public bool HasNetwork => Machine.Nics.FirstOrDefault() is { Mode: not NetMode.None };

    public bool CanConnectCable => IsActive && HasNetwork && CableOut;

    public bool CanDisconnectCable => IsActive && HasNetwork && !CableOut;

    public bool IsCapturing => CaptureFile is not null;

    public bool CanCapture => IsActive && HasNetwork && !IsCapturing;

    [RelayCommand]
    private async Task SetCableAsync(bool connected)
    {
        if (_watcher is not { } watcher) return;
        try
        {
            await LiveNetwork.SetCableAsync((c, a) => watcher.ExecuteAsync(c, a), watcher.Machine.Paths, connected).ConfigureAwait(false);
            Ui.Post(() =>
            {
                CableOut = !connected;
                _shell.Note(connected ? $"{Name}'s network cable is plugged in." : $"{Name}'s network cable is out: the system sees no network until Connect the network cable.");
            });
        }
        catch (Exception ex) when (ex is QmpException or IOException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel(connected ? "The cable could not be plugged in" : "The cable could not be pulled out", [ex is QmpException q ? q.Description : ex.Message], isError: true)));
        }
    }

    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        if (_watcher is not { } watcher || CaptureFile is not null) return;
        var file = Path.Combine(Entry.Directory, "captures", $"{Name} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.pcap");
        try
        {
            await LiveNetwork.StartCaptureAsync((c, a) => watcher.ExecuteAsync(c, a), watcher.Machine.Paths, file).ConfigureAwait(false);
            Ui.Post(() =>
            {
                CaptureFile = file;
                _shell.Note($"Capturing {Name}'s network traffic.");
            });
        }
        catch (Exception ex) when (ex is QmpException or IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("Network traffic could not be captured", [ex is QmpException q ? q.Description : ex.Message], isError: true)));
        }
    }

    [RelayCommand]
    private async Task StopCaptureAsync()
    {
        if (_watcher is not { } watcher || CaptureFile is not { } file) return;
        try
        {
            await LiveNetwork.StopCaptureAsync((c, a) => watcher.ExecuteAsync(c, a), watcher.Machine.Paths).ConfigureAwait(false);
            Ui.Post(() =>
            {
                CaptureFile = null;
                _shell.Note($"Saved the capture to {file} (Wireshark opens it).");
            });
        }
        catch (Exception ex) when (ex is QmpException or IOException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("The capture could not be stopped", [ex is QmpException q ? q.Description : ex.Message], isError: true)));
        }
    }

    // ── Screen recording: frames of the guest's display into a video (D26).

    private ScreenRecorder? _recorder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecord))]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingText = "";

    public bool CanRecord => IsActive && !IsRecording;

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (_watcher is not { } watcher || _recorder is not null) return;
        var encoders = await RecordingFormat.EncodersAsync().ConfigureAwait(false);
        if (encoders is null || RecordingFormat.Choose(encoders) is not { } format)
        {
            var install = Doctor.InstallCommand(Doctor.DetectThisDistro(), "ffmpeg");
            Ui.Post(() => _shell.Show(new NoticeViewModel("Recording needs ffmpeg",
                [encoders is null ? "ffmpeg turns the machine's screen into a video, and it is not installed." : "This computer's ffmpeg has no H.264 or VP9 encoder.", "Install it, then choose Record the screen again."],
                code: install)));
            return;
        }

        var videos = UserFolder(Environment.SpecialFolder.MyVideos, "Videos");
        var file = Path.Combine(videos, $"{Name} {DateTime.Now:yyyy-MM-dd HH.mm.ss}{format.Extension}");
        try
        {
            var recorder = await ScreenRecorder.StartAsync(watcher, file, format).ConfigureAwait(false);
            _recorder = recorder;
            Ui.Post(() =>
            {
                IsRecording = true;
                RecordingText = "Recording 0:00";
                _shell.Note($"Recording {Name}'s screen.");
            });
            _ = Task.Run(() => FollowRecordingAsync(recorder));
        }
        catch (Exception ex) when (ex is QmpException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("The screen could not be recorded", [ex.Message], isError: true)));
        }
    }

    /// <summary>The elapsed time in the pill, and the end of a recording the machine ended by stopping.</summary>
    private async Task FollowRecordingAsync(ScreenRecorder recorder)
    {
        while (_recorder == recorder)
        {
            await Task.Delay(500).ConfigureAwait(false);
            var t = DateTimeOffset.Now - recorder.Started;
            Ui.Post(() => RecordingText = $"Recording {(int)t.TotalMinutes}:{t.Seconds:00}");
            if (recorder.Ended is not null || !IsActive)
            {
                await StopRecordingAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (Interlocked.Exchange(ref _recorder, null) is not { } recorder) return;
        var problem = await recorder.StopAsync().ConfigureAwait(false);
        await recorder.DisposeAsync().ConfigureAwait(false);
        Ui.Post(() =>
        {
            IsRecording = false;
            if (problem is null) _shell.Note($"Saved the recording to {recorder.Output}{(recorder.Ended is { } why ? $" ({why})" : "")}.");
            else _shell.Show(new NoticeViewModel("The recording could not be finished", [problem], isError: true));
        });
    }

    [RelayCommand]
    private async Task CloneAsync()
    {
        if (IsActive)
        {
            _shell.Show(new NoticeViewModel($"Power {Name} off first", ["A clone copies the machine's disks, which must not change while they are copied."], isError: true));
            return;
        }

        // From a template a linked clone is the usual choice: the template never changes, so sharing its disks is safe.
        var full = new PromptOption("Full clone",
            "A separate machine with copies of this one's disks, firmware settings and TPM. Recommended when the clone will live long or move to another computer. Needs as much free space as this machine's disks use, and takes minutes for a large disk.",
            chosen: !IsTemplate);
        var linked = new PromptOption("Linked clone",
            IsTemplate
                ? $"Recommended. Ready in seconds and needs almost no space: the new machine shares {Name}'s disks, kept read-only, and keeps its own changes. The shared disks stay until neither the template nor any machine made from it is left."
                : $"Ready in seconds and needs almost no space: both machines share {Name}'s disks as they are now, kept read-only, and each keeps its own changes from then on. Best for trying things out. The shared disks stay until neither machine is left.",
            chosen: IsTemplate);
        var prompt = IsTemplate
            ? new PromptViewModel($"New machine from {Name}", ["The new machine starts exactly as the template is; the template itself never changes."],
                "Name of the new machine", $"{Name} 1", "Create machine", t => t.Trim().Length > 0, options: [full, linked])
            : new PromptViewModel($"Clone {Name}", ["Changes to either machine never touch the other."],
                "Name of the new machine", $"{Name} (clone)", "Clone", t => t.Trim().Length > 0, options: [full, linked]);
        _shell.ShowPrompt(prompt);
        if (await prompt.Answer.ConfigureAwait(false) is not { } name) return;
        var isLinked = prompt.Choice == 1;
        Ui.Post(() => prompt.Busy = true);
        try
        {
            var progress = new Progress<string>(p => Ui.Post(() => prompt.Progress = p));
            string dir;
            if (isLinked)
            {
                var result = await Task.Run(() => MachineLifecycle.LinkedCloneAsync(Machine, Entry.Directory, _shell.LibraryRoot, name, progress)).ConfigureAwait(false);
                dir = result.Directory;
                // The original now writes to a layer of its own on the shared base.
                Ui.Post(() => Reload(result.Original));
            }
            else
            {
                dir = (await Task.Run(() => MachineLifecycle.CloneAsync(Machine, Entry.Directory, _shell.LibraryRoot, name, progress)).ConfigureAwait(false)).Directory;
            }

            Ui.Post(() =>
            {
                prompt.Finish();
                _shell.Note(isLinked ? $"{name} is a linked clone of {Name}." : $"{name} is a clone of {Name}.");
            });
            await _shell.AfterLifecycleAsync(dir).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() =>
            {
                prompt.Finish();
                _shell.Show(new NoticeViewModel("The clone could not be made", [ex.Message], isError: true));
            });
        }
    }

    /// <summary>For deleting several machines at once (worker thread): why this one cannot go, or the files that would.</summary>
    internal (string? Refusal, IReadOnlyList<string> Files) DeletionPlan(IReadOnlyList<string> roots)
    {
        var conf = IsQuickemu ? Entry.Path : null;
        return (MachineLifecycle.DeleteRefusal(Entry.Directory, conf, roots), MachineLifecycle.FilesToDelete(Machine, Entry.Directory, conf));
    }

    /// <summary>Deletes this machine's files (worker thread; the caller confirmed, prunes shared disks and refreshes).</summary>
    internal void DeleteFiles(IReadOnlyList<string> roots) => MachineLifecycle.Delete(Machine, Entry.Directory, IsQuickemu ? Entry.Path : null, roots);

    [RelayCommand]
    private async Task DeleteMachineAsync()
    {
        if (IsActive)
        {
            _shell.Show(new NoticeViewModel($"Power {Name} off first", ["A running machine's files cannot be deleted."], isError: true));
            return;
        }

        var conf = IsQuickemu ? Entry.Path : null;
        var roots = _shell.LibraryRoots;
        var (files, freed, refusal) = await Task.Run(() => (MachineLifecycle.FilesToDelete(Machine, Entry.Directory, conf), MachineLifecycle.BasesFreedBy(Entry.Directory, roots),
            MachineLifecycle.DeleteRefusal(Entry.Directory, conf, roots))).ConfigureAwait(false);
        if (refusal is not null)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel($"{Name} cannot be deleted from here", [refusal], isError: true)));
            return;
        }

        var prompt = new PromptViewModel($"Delete {Name}?",
            ["This deletes the machine and everything in it, for good:", .. files.Select(f => "    " + f),
             .. freed.Count > 0 ? ["and the shared disks no other machine uses any more:", .. freed.Select(f => "    " + f)] : Array.Empty<string>(),
             "Installer discs kept elsewhere, and shared disks other machines still use, are not touched.", $"Type the machine's name to confirm."],
            "Machine name", "", "Delete for good", t => t.Trim() == Name, isDanger: true);
        Ui.Post(() => _shell.ShowPrompt(prompt));
        if (await prompt.Answer.ConfigureAwait(false) is null) return;
        try
        {
            await Task.Run(() =>
            {
                MachineLifecycle.Delete(Machine, Entry.Directory, conf, roots);
                MachineLifecycle.PruneBases(roots);
            }).ConfigureAwait(false);
            Ui.Post(() =>
            {
                prompt.Finish();
                _shell.Note($"{Name} was deleted.");
            });
            await _shell.AfterLifecycleAsync(null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() =>
            {
                prompt.Finish();
                _shell.Show(new NoticeViewModel("The machine could not be deleted", [ex.Message], isError: true));
            });
        }
    }

    /// <summary>Reads the machine's live state and figures (worker thread); posts the results.</summary>
    public async Task LoadAsync(HostCapabilities? host)
    {
        var details = await Task.Run(() => BuildDetails(host)).ConfigureAwait(false);
        var running = await Task.Run(() => MachineRunner.Find(Machine, _shell.Paths)).ConfigureAwait(false);
        Ui.Post(() => Details = details);
        if (running is not null) await AttachAsync(running).ConfigureAwait(false);
    }

    private IReadOnlyList<DetailRow> BuildDetails(HostCapabilities? host)
    {
        var rows = new List<DetailRow>();
        if (host is not null)
        {
            var launch = QemuCommandBuilder.Build(Machine, new LaunchContext { Host = host, MachineDirectory = Entry.Directory, Runtime = new RuntimePaths("/run") });
            _cores = launch.Cores;
            _memoryMiB = launch.MemoryMiB;
            rows.Add(new("Memory", $"{FormatMiB(launch.MemoryMiB)}{(Machine.MemoryMiB is null ? " (recommended for this computer)" : "")}"));
            rows.Add(new("Processors", $"{launch.Cores} core{(launch.Cores == 1 ? "" : "s")}{(Machine.Cpu.Cores is null ? " (recommended)" : "")}"));
        }

        foreach (var (disk, i) in Machine.Disks.Select((d, i) => (d, i)))
        {
            var path = Path.IsPathRooted(disk.File) ? disk.File : Path.Combine(Entry.Directory, disk.File);
            var size = File.Exists(path) ? $"{disk.SizeGiB} GB, {FormatBytes(new FileInfo(path).Length)} used" : $"{disk.SizeGiB} GB (made at first power on)";
            rows.Add(new(i == 0 ? "Hard disk" : $"Hard disk {i + 1}", size));
        }

        foreach (var media in Machine.Media.Where(m => m.Role is MediaRole.Installer or MediaRole.Drivers or MediaRole.Recovery))
            rows.Add(new(media.Role == MediaRole.Drivers ? "CD/DVD 2" : "CD/DVD", Path.GetFileName(media.File) + (media.Connect == Connect.UntilInstalled ? " (until installed)" : "")));
        var nic = Machine.Nics.FirstOrDefault();
        rows.Add(new("Network", nic?.Mode switch
        {
            NetMode.User => "NAT (shares this computer's connection)",
            NetMode.Restricted => "NAT, no internet",
            NetMode.Bridge => $"Bridged to {nic.Bridge}",
            NetMode.Segment => $"Private network \"{nic.Segment}\"",
            _ => "None",
        }));
        rows.Add(new("Firmware", Machine.Firmware.Kind == FirmwareKind.Bios ? "BIOS" : Machine.Firmware.SecureBoot ? "UEFI with Secure Boot" : "UEFI"));
        if (Machine.Tpm) rows.Add(new("TPM", "Version 2.0"));
        if (Machine.Usb.Passthrough.Count > 0) rows.Add(new("USB devices", string.Join(", ", Machine.Usb.Passthrough.Select(u => u.Name ?? $"{u.VendorId}:{u.ProductId}"))));
        rows.Add(new("Display", Machine.Display.Backend == DisplayBackend.Spice ? "Shown in a viewer window" : Machine.Display.Backend.ToString()));
        return rows;
    }

    private async Task AttachAsync(RunningMachine running)
    {
        try
        {
            var watcher = await MachineWatcher.AttachAsync(running).ConfigureAwait(false);
            watcher.StateChanged += (_, s) => Ui.Post(() => OnState(s));
            _watcher = watcher;
            Ui.Post(() =>
            {
                SshCommand = running.SshPort is { } p ? Machine.Guest.SshCommand(p)
                    : Machine.Devices.Vsock && System.IO.File.Exists("/etc/ssh/ssh_config.d/20-systemd-ssh-proxy.conf") ? Machine.Guest.SshVsockCommand(QemuCommandBuilder.VsockCid(Machine.Id))
                    : null;
                OnState(watcher.State);
                CableOut = LiveNetwork.IsUnplugged(running.Paths);
                CaptureFile = LiveNetwork.CapturePath(running.Paths);
            });
            _ = watcher.Exited.ContinueWith(_ => Ui.Post(OnExited), TaskScheduler.Default);
            _ = PollGuestAsync(watcher);
            _ = AutoProtectLoopAsync(watcher);
            _ = InstallLoopAsync(watcher);
            _ = MouseCaptureLoopAsync(watcher);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or QmpException or TimeoutException)
        {
            Log.Warn($"{Name}: could not attach to the running machine ({ex.Message}).");
        }
    }

    private void OnState(PowerState state)
    {
        State = state;
        if (state is PowerState.Running or PowerState.Paused or PowerState.ShuttingDown) StartThumbnails();
        UpdateTabWork();
        if (state == PowerState.Crashed) _shell.Show(new NoticeViewModel($"{Name} crashed", ["The guest operating system reported a crash. Its screen shows the details; Reset restarts it."], isError: true));
    }

    private void OnExited()
    {
        _thumbnailLoop?.Cancel();
        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null) _ = watcher.DisposeAsync().AsTask();
        State = PowerState.Off;
        Performance.Stop();
        Console.Disconnect();
        Screen = null;
        GuestAddress = null;
        GuestToolsAnswering = null;
        GuestToolsMissing = false;
        SshCommand = null;
        CableOut = false;
        CaptureFile = null;
        _shell.Note(_suspending ? $"{Name} is suspended." : $"{Name} powered off.");
        if (!_suspending && Machine.OnPowerOff != DiscardMode.Keep) _ = EndSessionAsync(Machine.OnPowerOff);
        _suspending = false;
        _ = RefreshSuspendedAsync();
        _ = RefreshInstallAsync();
        _ = Task.Run(() => MachineRunner.Find(Machine, _shell.Paths));
    }

    /// <summary>The screen, every two seconds while the machine runs and its page is open.</summary>
    private void StartThumbnails()
    {
        if (_thumbnailLoop is { IsCancellationRequested: false }) return;
        _thumbnailLoop = new CancellationTokenSource();
        var ct = _thumbnailLoop.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _watcher?.Machine is { } running)
            {
                if (IsSelected && State != PowerState.Off)
                {
                    try
                    {
                        var file = Path.Combine(running.RuntimeDirectory, "thumbnail.png");
                        if (_watcher is not { } watcher) break;
                        await watcher.ScreenshotAsync(file, ct).ConfigureAwait(false);
                        await using var stream = File.OpenRead(file);
                        var bitmap = Bitmap.DecodeToWidth(stream, 960);
                        Ui.Post(() =>
                        {
                            var old = Screen;
                            Screen = bitmap;
                            old?.Dispose();
                        });
                    }
                    catch (Exception ex) when (ex is IOException or QmpException or System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
                    {
                    }
                }

                try
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, ct);
    }

    private async Task PollGuestAsync(MachineWatcher watcher)
    {
        var agent = new GuestAgentClient(watcher.Machine.Paths.Agent);
        while (!watcher.Exited.IsCompleted)
        {
            var addresses = await agent.AddressesAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var text = addresses is { Count: > 0 } ? string.Join(", ", addresses.Where(a => !a.IPv6).Select(a => a.Address).DefaultIfEmpty(addresses[0].Address)) : null;
            // The guest's own name for its distribution sharpens the install command (asked once, when the agent first answers).
            string? releaseId = null;
            if (addresses is not null && _guestReleaseId is null && (await agent.OsInfoAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))?["id"] is { } id)
                releaseId = id.GetValue<string>();
            Ui.Post(() =>
            {
                GuestAddress = text;
                GuestToolsAnswering = addresses is not null;
                GuestToolsMissing = addresses is null && !watcher.Exited.IsCompleted && DateTimeOffset.Now - watcher.Machine.Started > ClipboardGrace;
                if (releaseId is not null) { _guestReleaseId = releaseId; OnPropertyChanged(nameof(GuestToolsAdvice)); }
            });
            await Task.WhenAny(watcher.Exited, Task.Delay(10_000)).ConfigureAwait(false);
        }
    }

    /// <summary>How long a machine may boot before a missing guest agent counts as missing guest tools.</summary>
    internal static TimeSpan ClipboardGrace { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A viewer shows the machine, copy and paste is on, the system has SPICE guest tools, and its agent does not answer:
    /// paste in the guest is grayed out until they are in (the SPICE agent comes with them; the QEMU guest agent, which
    /// is what answers here, comes in the same installer on Windows and the same package set on Linux).
    /// </summary>
    public bool ClipboardNeedsTools { get; private set; }

    private bool NeedsToolsForClipboard() =>
        GuestToolsAnswering == false && Machine.Display.CopyPaste && GuestToolsAdvice is { } advice && (advice.Steps.Count > 0 || advice.Command is not null);

    private void SetClipboardNeedsTools(bool value)
    {
        if (value == ClipboardNeedsTools) return;
        ClipboardNeedsTools = value;
        Log.Info(value ? $"{Name}: a viewer is open and the guest tools do not answer; copy and paste hint shown." : $"{Name}: copy and paste hint removed.");
        _shell.ClipboardChanged();
    }

    /// <summary>A viewer is connected and a click in it captures the mouse (see <see cref="MouseCapture"/>).</summary>
    public bool CanHoldMouse { get; private set; }

    /// <summary>
    /// Once a second while the machine runs: whether a viewer can be holding its mouse. While it can, the status bar says
    /// how to let go; the hint goes once the guest drives the pointer itself, the viewer closes, or the machine stops.
    /// </summary>
    private async Task MouseCaptureLoopAsync(MachineWatcher watcher)
    {
        try
        {
            while (!watcher.Exited.IsCompleted)
            {
                bool capture, viewer;
                try
                {
                    (viewer, capture) = await MouseCapture.ReadAsync(watcher).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is QmpException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    (viewer, capture) = (false, false);
                }

                var running = watcher.Machine is { } r ? DateTimeOffset.Now - r.Started : TimeSpan.Zero;
                Ui.Post(() => SetClipboardNeedsTools(viewer && running > ClipboardGrace && NeedsToolsForClipboard()));

                if (capture != CanHoldMouse)
                {
                    Log.Info(capture
                        ? $"{Name}: the viewer can capture the mouse (server mouse mode); release hint shown."
                        : $"{Name}: the mouse moves freely now (client mouse mode, or no viewer); release hint removed.");
                    Ui.Post(() => SetCanHoldMouse(capture));
                }

                await Task.WhenAny(watcher.Exited, Task.Delay(1000)).ConfigureAwait(false);
            }
        }
        finally
        {
            Ui.Post(() =>
            {
                SetCanHoldMouse(false);
                SetClipboardNeedsTools(false);
            });
        }
    }

    private void SetCanHoldMouse(bool value)
    {
        if (CanHoldMouse == value) return;
        CanHoldMouse = value;
        _shell.MouseCaptureChanged();
    }

    /// <summary>AutoProtect while the machine runs: a look once a minute, a snapshot when one is due.</summary>
    private async Task AutoProtectLoopAsync(MachineWatcher watcher)
    {
        while (!watcher.Exited.IsCompleted)
        {
            await Task.WhenAny(watcher.Exited, Task.Delay(TimeSpan.FromMinutes(1))).ConfigureAwait(false);
            if (watcher.Exited.IsCompleted || _suspending || watcher.Machine is not { } running) return;
            var config = Machine.AutoProtect;
            if (!config.Enabled || Machine.OnPowerOff != DiscardMode.Keep) continue;
            try
            {
                var taken = await Task.Run(() => AutoProtect.RunIfDueAsync(Snapshots.Store, config, watcher, running.Started, DateTimeOffset.Now)).ConfigureAwait(false);
                if (taken is null) continue;
                Ui.Post(() => _shell.Note($"AutoProtect took a snapshot of {Name}."));
                await Snapshots.LoadAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is QmpException or IOException or InvalidOperationException)
            {
                // To the log, not a notice: a notice would come back every minute until the cause is fixed.
                Log.Warn($"{Name}: AutoProtect could not take a snapshot ({ex.Message}).");
            }
        }
    }

    [RelayCommand]
    private Task PowerOnAsync() => StartAsync(new StartOptions());

    /// <summary>Workstation's "Start in background": the machine runs with its screen ready, but no viewer opens until asked.</summary>
    [RelayCommand]
    private Task PowerOnInBackgroundAsync() => StartAsync(new StartOptions(), openScreen: false);

    /// <summary>Power on into the firmware's setup screen (UEFI) or boot menu (BIOS), as a computer's F2 or Esc does.</summary>
    [RelayCommand]
    private Task PowerOnToFirmwareAsync() => StartAsync(new StartOptions { FirmwareSetup = true });

    private async Task StartAsync(StartOptions options, bool openScreen = true)
    {
        if (Busy || State != PowerState.Off) return;
        Busy = true;
        try
        {
            var host = await _shell.HostAsync().ConfigureAwait(false);
            // A machine that cannot start (a missing disc, no disk) says why at once; the memory question comes only
            // for one that would otherwise start.
            var startable = !Machine.IsTemplate && await Task.Run(() => MachineChecks.Problems(Machine, Entry.Directory).Count == 0).ConfigureAwait(false);
            if (startable && await _shell.MemoryShortfallAsync(this, host).ConfigureAwait(false) is { } shortfall)
            {
                var notice = new NoticeViewModel($"{Name} may not have enough memory", MemoryHeadroom.Describe(shortfall, Name), confirm: "Power on anyway", isDanger: true);
                Ui.Post(() => _shell.Show(notice));
                if (!await notice.Answer.ConfigureAwait(false))
                {
                    Ui.Post(() => _shell.Note($"{Name} was not started."));
                    return;
                }
            }

            Ui.Post(() => _shell.Note($"Starting {Name}…"));
            var running = await Task.Run(() => MachineRunner.StartAsync(Machine, Entry.Directory, _shell.Paths, host, options)).ConfigureAwait(false);
            await AttachAsync(running).ConfigureAwait(false);
            Ui.Post(() =>
            {
                _shell.Note(IsSuspended ? $"{Name} carried on where it was." : openScreen ? $"{Name} is running." : $"{Name} is running in the background. Open screen shows it.");
                IsSuspended = false;
                if (running.Notices.Count > 0) _shell.Show(new NoticeViewModel($"{Name} started, with something left out", running.Notices, isError: false));
            });
            if (openScreen && Machine.Display.Backend == DisplayBackend.Spice) OpenScreen();
        }
        catch (StartRefusedException ex)
        {
            Ui.Post(() =>
            {
                _shell.Note($"{Name} did not start.");
                _shell.Show(new NoticeViewModel($"{Name} could not start", ex.Reasons, isError: true));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() =>
            {
                _shell.Note($"{Name} did not start.");
                _shell.Show(new NoticeViewModel($"{Name} could not start", [ex.Message], isError: true));
            });
        }
        finally
        {
            Ui.Post(() => Busy = false);
        }
    }

    [RelayCommand]
    private Task ShutDownAsync() => ControlAsync("asked to shut down", MachineRunner.ShutDownAsync);

    [RelayCommand]
    private async Task PowerOffAsync()
    {
        if (!_shell.Settings.ConfirmPowerOff)
        {
            await ControlAsync("powered off", MachineRunner.PowerOffAsync).ConfigureAwait(false);
            return;
        }

        var notice = new NoticeViewModel($"Power off {Name}?",
            ["This turns the machine off at once, like pulling the plug. Anything not saved inside it is lost.", "Shut Down asks the guest to close its programs and turn itself off instead."],
            confirm: "Power off", isDanger: true);
        _shell.Show(notice);
        if (await notice.Answer.ConfigureAwait(false)) await ControlAsync("powered off", MachineRunner.PowerOffAsync).ConfigureAwait(false);
    }

    /// <summary>Saves what the machine is doing to its folder and turns it off; Power on carries on from there.</summary>
    [RelayCommand]
    private async Task SuspendAsync()
    {
        if (_watcher?.Machine is not { } running) return;
        _suspending = true;
        _shell.Note($"Suspending {Name}…");
        try
        {
            await Task.Run(() => MachineSuspend.SuspendAsync(running, new Progress<string>(p => Ui.Post(() => _shell.Note(p))))).ConfigureAwait(false);
            // The state file takes its name only after QEMU has quit, so the exit's own look may have come too early.
            await RefreshSuspendedAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is QmpException or IOException or System.Net.Sockets.SocketException)
        {
            Ui.Post(() =>
            {
                _suspending = false;
                _shell.Note($"{Name} is still running.");
                _shell.Show(new NoticeViewModel($"{Name} could not be suspended", [
                    $"QEMU could not save the machine's state: {(ex is QmpException q ? q.Description : ex.Message)}",
                    "Shared folders and USB devices handed over from this computer keep a machine from being suspended. The machine is still running; Shut down works instead."], isError: true));
            });
        }
    }

    [RelayCommand]
    private async Task MakeTemplateAsync()
    {
        var notice = new NoticeViewModel($"Make {Name} a template?",
            ["A template is never started, only used to make new machines, so each of them begins exactly as it is now: install and set up a system once, then make as many machines from it as you like.",
             "Linked machines made from it share its disks and take seconds. You can turn it back into a machine at any time."],
            confirm: "Make template");
        _shell.Show(notice);
        if (!await notice.Answer.ConfigureAwait(false)) return;
        await SetTemplateAsync(true).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task UndoTemplateAsync() => SetTemplateAsync(false);

    private async Task SetTemplateAsync(bool template)
    {
        var (machine, dir) = (Machine, Entry.Directory);
        try
        {
            var changed = await Task.Run(() => MachineLifecycle.SetTemplate(machine, dir, template)).ConfigureAwait(false);
            Ui.Post(() =>
            {
                Reload(changed);
                _shell.Note(template ? $"{Name} is a template." : $"{Name} is a machine again.");
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel(template ? $"{Name} cannot be a template" : $"{Name} is still a template", [ex.Message], isError: true)));
        }
    }

    [RelayCommand]
    private async Task DiscardSuspendedAsync()
    {
        var notice = new NoticeViewModel($"Discard {Name}'s suspended state?",
            ["The machine will start afresh, as after a power cut: anything open and unsaved inside it is lost.", "Resume carries on from where it was instead."],
            confirm: "Discard", isDanger: true);
        _shell.Show(notice);
        if (!await notice.Answer.ConfigureAwait(false)) return;
        var dir = Entry.Directory;
        await Task.Run(() => MachineSuspend.Discard(dir)).ConfigureAwait(false);
        Ui.Post(() => _shell.Note($"{Name} will start afresh."));
        await RefreshSuspendedAsync().ConfigureAwait(false);
    }

    private async Task RefreshSuspendedAsync()
    {
        var dir = Entry.Directory;
        var paths = _shell.Paths;
        var id = Machine.Id;
        var (suspended, open) = await Task.Run(() => (MachineSuspend.IsSuspended(dir), DiskOpener.OpenFor(paths, id))).ConfigureAwait(false);
        Ui.Post(() =>
        {
            IsSuspended = suspended;
            OpenDisks = open;
        });
    }

    /// <summary>The machine's disks open on this computer now (D37); the machine cannot start until they are closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenDisk), nameof(OpenDiskText), nameof(CanOpenDisk), nameof(CanOpenDiskForChanges))]
    private IReadOnlyList<OpenedDisk> _openDisks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenDisk), nameof(CanOpenDiskForChanges))]
    private bool _changingDisk;

    public bool HasOpenDisk => OpenDisks.Count > 0;

    public string OpenDiskText => OpenDisks switch
    {
        [] => "",
        [var d] => $"{(d.Writable ? "Open for changes" : "Open read-only")} at {d.MountPoint}. {Name} cannot start until it is closed.",
        _ => $"{OpenDisks.Count} disks are open in {Path.GetDirectoryName(OpenDisks[0].MountPoint)}. {Name} cannot start until they are closed.",
    };

    public bool CanOpenDisk => IsOff && !HasOpenDisk && !ChangingDisk;

    public bool CanOpenDiskForChanges => CanOpenDisk && !IsSuspended && !IsTemplate;

    [RelayCommand]
    private Task OpenDiskAsync() => OpenDisksAsync(writable: false);

    [RelayCommand]
    private async Task OpenDiskForChangesAsync()
    {
        var notice = new NoticeViewModel($"Open {Name}'s disk for changes?",
            ["Whatever you change, add or delete there goes straight into the machine's disk.", "Take a snapshot first if you may want to go back. Read-only is the safe way to copy files out."],
            confirm: "Open for changes");
        _shell.Show(notice);
        if (!await notice.Answer.ConfigureAwait(false)) return;
        await OpenDisksAsync(writable: true).ConfigureAwait(false);
    }

    private async Task OpenDisksAsync(bool writable)
    {
        if (!await Task.Run(() => DiskOpener.Available).ConfigureAwait(false))
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("libguestfs is not installed",
                ["VM Qontrol opens a machine's disk with libguestfs, which reads it safely in a small helper machine of its own.", Doctor.InstallCommand(Doctor.DetectThisDistro(), "guestmount")], isError: true)));
            return;
        }

        Ui.Post(() => ChangingDisk = true);
        var m = Machine;
        var dir = Entry.Directory;
        var paths = _shell.Paths;
        var opened = new List<OpenedDisk>();
        var errors = new List<string>();
        var allowedKernel = false;
        for (var i = 0; i < m.Disks.Count; i++)
        {
            try
            {
                opened.Add(await Task.Run(() => DiskOpener.OpenAsync(m, dir, paths, i, writable)).ConfigureAwait(false));
            }
            catch (DiskOpenException ex) when (ex.KernelUnreadable && !allowedKernel)
            {
                var ask = new NoticeViewModel("Let libguestfs read this computer's kernel?", [ex.Message, "This makes /boot/vmlinuz readable, as libguestfs advises; nothing else changes."], confirm: "Allow");
                Ui.Post(() => _shell.Show(ask));
                if (!await ask.Answer.ConfigureAwait(false)) break;
                allowedKernel = true;
                if (await Task.Run(() => DiskOpener.AllowKernelAsync()).ConfigureAwait(false) is { } refused) { errors.Add(refused); break; }
                i--; // the same disk again
            }
            catch (DiskOpenException ex)
            {
                errors.Add(m.Disks.Count > 1 ? $"Disk {i + 1}: {ex.Message}" : ex.Message);
            }
        }

        await RefreshSuspendedAsync().ConfigureAwait(false);
        Ui.Post(() =>
        {
            ChangingDisk = false;
            if (errors.Count > 0) _shell.Show(new NoticeViewModel(opened.Count > 0 ? "Not every disk could be opened" : "The disk could not be opened", errors, isError: true));
            if (opened.Count > 0) ShowOpenDisk();
        });
    }

    [RelayCommand]
    private void ShowOpenDisk()
    {
        var folder = OpenDisks.Count == 1 ? OpenDisks[0].MountPoint : OpenDisks.Count > 1 ? Path.GetDirectoryName(OpenDisks[0].MountPoint) : null;
        if (folder is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { folder }, UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        });
    }

    [RelayCommand]
    private async Task CloseDiskAsync()
    {
        var paths = _shell.Paths;
        var open = OpenDisks;
        Ui.Post(() => ChangingDisk = true);
        string? error = null;
        foreach (var d in open)
        {
            try
            {
                await Task.Run(() => DiskOpener.CloseAsync(paths, d)).ConfigureAwait(false);
            }
            catch (DiskOpenException ex)
            {
                error = ex.Message;
            }
        }

        await RefreshSuspendedAsync().ConfigureAwait(false);
        Ui.Post(() =>
        {
            ChangingDisk = false;
            if (error is not null) _shell.Show(new NoticeViewModel("The disk is still in use", [error, "Close the file manager window or terminal that is in its folder, then try again."], isError: true));
            else _shell.Note($"{Name}'s disk is closed; it can start again.");
        });
    }

    [RelayCommand]
    private Task PauseAsync() => ControlAsync("paused", MachineRunner.PauseAsync);

    [RelayCommand]
    private Task ResumeAsync() => ControlAsync("resumed", MachineRunner.ResumeAsync);

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!_shell.Settings.ConfirmReset)
        {
            await ControlAsync("reset", MachineRunner.ResetAsync).ConfigureAwait(false);
            return;
        }

        var notice = new NoticeViewModel($"Reset {Name}?", ["This restarts the machine at once, like a computer's reset button. Anything not saved inside it is lost."], confirm: "Reset", isDanger: true);
        _shell.Show(notice);
        if (await notice.Answer.ConfigureAwait(false)) await ControlAsync("reset", MachineRunner.ResetAsync).ConfigureAwait(false);
    }

    [RelayCommand]
    private void OpenScreen()
    {
        if (_watcher?.Machine is not { } running) return;
        var machine = Machine with { Display = Machine.Display with { Viewer = _shell.Settings.Viewer } };
        _ = Task.Run(() =>
        {
            var viewer = ViewerLauncher.Open(machine, running, App.Paths, remember: true);
            Ui.Post(() =>
            {
                if (viewer is null)
                    _shell.Show(new NoticeViewModel("No screen viewer is installed", ["VM Qontrol shows a machine's screen with remote-viewer or spicy.", Doctor.InstallCommand(Doctor.DetectThisDistro(), "viewer")], isError: true));
            });
        });
    }

    [RelayCommand]
    private void OpenFolder() => _ = Task.Run(() =>
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Entry.Directory }, UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    });

    [RelayCommand]
    private async Task CopyCommandLineAsync()
    {
        var host = await _shell.HostAsync().ConfigureAwait(false);
        var text = await Task.Run(() =>
        {
            var launch = QemuCommandBuilder.Build(Machine, new LaunchContext { Host = host, MachineDirectory = Entry.Directory, Runtime = new RuntimePaths(_shell.Paths.RuntimeFor(Machine.Id)) });
            return "qemu-system-x86_64 " + string.Join(' ', launch.Args.Select(a => a.Any(c => c is ' ' or '(' or ')' or '\'') ? $"'{a}'" : a));
        }).ConfigureAwait(false);
        Ui.Post(() => _shell.CopyToClipboard(text, "The QEMU command line is on the clipboard."));
    }

    [RelayCommand]
    private void ToggleFavorite() => _shell.SetFavorite(this, !IsFavorite);

    [RelayCommand]
    private void MoveToGroup() => _shell.AskGroup(this);

    [RelayCommand]
    private void RemoveFromGroup() => _shell.SetGroup(this, "");

    private async Task ControlAsync(string done, Func<RunningMachine, CancellationToken, Task> action)
    {
        if (_watcher?.Machine is not { } running) return;
        try
        {
            await Task.Run(() => action(running, CancellationToken.None)).ConfigureAwait(false);
            Ui.Post(() => _shell.Note($"{Name} {done}."));
        }
        catch (Exception ex) when (ex is QmpException or IOException or System.Net.Sockets.SocketException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel($"{Name} did not respond", [ex.Message], isError: true)));
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        UpdateTabWork();
        if (!value || _watcher is null) return;
        _thumbnailLoop?.Cancel();
        _thumbnailLoop = null;
        StartThumbnails();
    }

    private static string FormatMiB(long mib) => mib % 1024 == 0 ? $"{mib / 1024} GB" : $"{mib} MB";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024} KB",
    };
}
