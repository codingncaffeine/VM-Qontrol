using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

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
    }

    public PerformanceViewModel Performance { get; } = new();

    public ConsoleViewModel Console { get; } = new();

    public LogViewModel Logs { get; }

    private int _cores = 1;
    private long _memoryMiB = 1024;

    /// <summary>0 Summary, 1 Performance, 2 Console, 3 Log: work for a tab runs only while it is open.</summary>
    [ObservableProperty]
    private int _tab;

    partial void OnTabChanged(int value) => UpdateTabWork();

    private void UpdateTabWork()
    {
        if (Tab == 1 && IsSelected && _watcher is { } w) Performance.Start(w, _cores, _memoryMiB);
        else Performance.Stop();
        if (Tab == 2 && IsSelected && _watcher?.Machine is { } r) _ = Console.ConnectAsync(r);
        if (Tab == 3) _ = Logs.LoadAsync();
    }

    public LibraryEntry Entry { get; private set; }

    public MachineConfig Machine { get; private set; }

    public string Id => Machine.Id;

    public string Name => Machine.Name;

    public string GuestLabel => GuestNames.Describe(Machine.Guest);

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
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsOff), nameof(IsPaused), nameof(CanPause), nameof(IsActive), nameof(ScreenPlaceholder))]
    private PowerState _state = PowerState.Off;

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

    public string StateText => Busy && State == PowerState.Off ? "Starting…" : State switch
    {
        PowerState.Running => "Running",
        PowerState.Paused => "Paused",
        PowerState.ShuttingDown => "Shutting down…",
        PowerState.Crashed => "Crashed",
        _ => "Powered off",
    };

    public string ScreenPlaceholder => State == PowerState.Off ? "Powered off" : "Waiting for the screen…";

    public RunningMachine? Running => _watcher?.Machine;

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
                SshCommand = running.SshPort is { } p ? $"ssh -p {p} user@localhost" : null;
                OnState(watcher.State);
            });
            _ = watcher.Exited.ContinueWith(_ => Ui.Post(OnExited), TaskScheduler.Default);
            _ = PollGuestAsync(watcher);
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
        _shell.Note($"{Name} powered off.");
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

    [RelayCommand]
    private async Task PowerOnAsync()
    {
        if (Busy || State != PowerState.Off) return;
        Busy = true;
        _shell.Note($"Starting {Name}…");
        try
        {
            var host = await _shell.HostAsync().ConfigureAwait(false);
            var running = await Task.Run(() => MachineRunner.StartAsync(Machine, Entry.Directory, _shell.Paths, host, new StartOptions())).ConfigureAwait(false);
            await AttachAsync(running).ConfigureAwait(false);
            Ui.Post(() => _shell.Note($"{Name} is running."));
            if (Machine.Display.Backend == DisplayBackend.Spice) OpenScreen();
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
