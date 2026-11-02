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
    public string LibraryLabel => GuestLabel + (IsTemplate ? " · Template" : "");

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
        nameof(CanOpenFirmware), nameof(CanDiscardSuspended), nameof(CanPowerOn), nameof(CanMakeTemplate))]
    private PowerState _state = PowerState.Off;

    /// <summary>Off with its state saved: Power on carries on from where it was.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ScreenPlaceholder), nameof(PowerOnText), nameof(PowerOnTip), nameof(CanOpenFirmware), nameof(CanDiscardSuspended))]
    private bool _isSuspended;

    private bool _suspending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(StateText))]
    private bool _busy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreen))]
    private Bitmap? _screen;

    [ObservableProperty]
    private string? _guestAddress;

    [ObservableProperty]
    private string? _sshCommand;

    [ObservableProperty]
    private bool _isFavourite;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The sidebar folder the machine is filed under; empty = not filed.</summary>
    [ObservableProperty]
    private string _folderName = "";

    /// <summary>This machine opens its folder's group in the sidebar, so it shows the folder's heading above itself.</summary>
    [ObservableProperty]
    private bool _showFolderHeader;

    public string FolderHeader => FolderName.ToUpperInvariant();

    public bool HasFolder => FolderName.Length > 0;

    partial void OnFolderNameChanged(string value)
    {
        OnPropertyChanged(nameof(FolderHeader));
        OnPropertyChanged(nameof(HasFolder));
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

    public string PowerOnTip => IsSuspended ? "Carry on from where the machine was suspended, with every program as it was" : "Start the machine and open its screen";

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

    public void Reload(MachineConfig machine)
    {
        Machine = machine;
        Entry = Entry with { Machine = machine };
        Problems = LibraryScanner.MissingFiles(Entry);
        OnPropertyChanged(string.Empty);
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

    /// <summary>Saves the machine's screen as a picture in the Pictures folder.</summary>
    [RelayCommand]
    private async Task SaveScreenshotAsync()
    {
        if (_watcher is not { } watcher) return;
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } p ? p : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
        var (files, freed) = await Task.Run(() => (MachineLifecycle.FilesToDelete(Machine, Entry.Directory, conf), MachineLifecycle.BasesFreedBy(Entry.Directory, roots))).ConfigureAwait(false);
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
                MachineLifecycle.Delete(Entry.Directory, conf);
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
                SshCommand = running.SshPort is { } p ? Machine.Guest.SshCommand(p) : null;
                OnState(watcher.State);
            });
            _ = watcher.Exited.ContinueWith(_ => Ui.Post(OnExited), TaskScheduler.Default);
            _ = PollGuestAsync(watcher);
            _ = AutoProtectLoopAsync(watcher);
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
        SshCommand = null;
        _shell.Note(_suspending ? $"{Name} is suspended." : $"{Name} powered off.");
        _suspending = false;
        _ = RefreshSuspendedAsync();
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
            Ui.Post(() => GuestAddress = text);
            await Task.WhenAny(watcher.Exited, Task.Delay(10_000)).ConfigureAwait(false);
        }
    }

    /// <summary>AutoProtect while the machine runs: a look once a minute, a snapshot when one is due.</summary>
    private async Task AutoProtectLoopAsync(MachineWatcher watcher)
    {
        while (!watcher.Exited.IsCompleted)
        {
            await Task.WhenAny(watcher.Exited, Task.Delay(TimeSpan.FromMinutes(1))).ConfigureAwait(false);
            if (watcher.Exited.IsCompleted || _suspending || watcher.Machine is not { } running) return;
            var config = Machine.AutoProtect;
            if (!config.Enabled) continue;
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
        _shell.Note($"Starting {Name}…");
        try
        {
            var host = await _shell.HostAsync().ConfigureAwait(false);
            var running = await Task.Run(() => MachineRunner.StartAsync(Machine, Entry.Directory, _shell.Paths, host, options)).ConfigureAwait(false);
            await AttachAsync(running).ConfigureAwait(false);
            Ui.Post(() =>
            {
                _shell.Note(IsSuspended ? $"{Name} carried on where it was." : openScreen ? $"{Name} is running." : $"{Name} is running in the background. Open screen shows it.");
                IsSuspended = false;
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
        var suspended = await Task.Run(() => MachineSuspend.IsSuspended(dir)).ConfigureAwait(false);
        Ui.Post(() => IsSuspended = suspended);
    }

    [RelayCommand]
    private Task PauseAsync() => ControlAsync("paused", MachineRunner.PauseAsync);

    [RelayCommand]
    private Task ResumeAsync() => ControlAsync("resumed", MachineRunner.ResumeAsync);

    [RelayCommand]
    private async Task ResetAsync()
    {
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
            var viewer = ViewerLauncher.Open(machine, running);
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
    private void ToggleFavourite() => _shell.SetFavourite(this, !IsFavourite);

    [RelayCommand]
    private void MoveToFolder() => _shell.AskFolder(this);

    [RelayCommand]
    private void RemoveFromFolder() => _shell.SetFolder(this, "");

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
