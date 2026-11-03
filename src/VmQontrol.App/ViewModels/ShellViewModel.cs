using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Library;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>
/// The main window's state: the library, the selected machine, the welcome page and messages. Every scan and probe runs
/// on a worker; results come back through <see cref="Ui.Post"/>.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly List<MachineViewModel> _all = [];
    private Lazy<Task<HostCapabilities>> _host = new(() => Task.Run(() => HostProbe.Probe()));

    public ShellViewModel(QontrolPaths paths, AppSettings settings)
    {
        Paths = paths;
        Settings = settings;
        Palette = new PaletteViewModel(this);
        // The theme chosen in Preferences, unless VMQONTROL_THEME pins one (screenshots, bug reports).
        if (Environment.GetEnvironmentVariable("VMQONTROL_THEME") is null or "" && settings.Theme != "system")
            Ui.Post(() => PreferencesViewModel.ApplyTheme(settings.Theme));
    }

    /// <summary>The app's preferences while they are open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome), nameof(ShowMachine), nameof(ShowPreferences), nameof(ShowSettings), nameof(ShowImport))]
    private PreferencesViewModel? _preferencesPage;

    public bool ShowPreferences => PreferencesPage is not null && Wizard is null;

    [RelayCommand]
    public void OpenPreferences()
    {
        if (Wizard is not null) return;
        if (PreferencesPage is { } open)
        {
            open.Refresh();
            return;
        }

        var page = new PreferencesViewModel(this);
        page.Closed += () => Ui.Post(() =>
        {
            if (PreferencesPage == page) PreferencesPage = null;
        });
        SettingsPage = null;
        if (ImportPage is { Busy: false }) ImportPage = null;
        PreferencesPage = page;
    }

    public PaletteViewModel Palette { get; }

    public QontrolPaths Paths { get; }

    public AppSettings Settings { get; private set; }

    public WelcomeViewModel Welcome { get; } = new();

    /// <summary>The machines the sidebar shows: favourites first, then by name, filtered by the search box.</summary>
    public ObservableCollection<MachineViewModel> Machines { get; } = [];

    /// <summary>What the sidebar lists: folder headings and machines, in order.</summary>
    public ObservableCollection<object> SidebarItems { get; } = [];

    /// <summary>The sidebar's selection: a machine selects it; a heading cannot be selected.</summary>
    public object? SidebarSelection
    {
        get => Selected;
        set
        {
            if (value is MachineViewModel m) Selected = m;
            OnPropertyChanged();
        }
    }

    /// <summary>A machine to open when the library has loaded (vmqontrol NAME).</summary>
    public string? OpenOnStart { get; set; }

    [ObservableProperty]
    private FolderPromptViewModel? _folderPrompt;

    /// <summary>The New Machine wizard while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome), nameof(ShowMachine), nameof(ShowWizard), nameof(ShowSettings), nameof(ShowImport), nameof(ShowPreferences))]
    private NewMachineViewModel? _wizard;

    public bool ShowWizard => Wizard is not null;

    /// <summary>Import a machine (VMware, VirtualBox, OVF, virt-manager, a disk) while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome), nameof(ShowMachine), nameof(ShowImport), nameof(ShowSettings), nameof(ShowPreferences))]
    private ImportViewModel? _importPage;

    public bool ShowImport => ImportPage is not null && Wizard is null && PreferencesPage is null;

    [RelayCommand]
    public void OpenImport()
    {
        if (ImportPage is not null || Wizard is not null) return;
        var page = new ImportViewModel(this);
        page.Closed += () => Ui.Post(() =>
        {
            if (ImportPage == page) ImportPage = null;
        });
        SettingsPage = null;
        PreferencesPage = null;
        ImportPage = page;
    }

    /// <summary>A machine's settings while they are open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome), nameof(ShowMachine), nameof(ShowSettings))]
    private SettingsViewModel? _settingsPage;

    public bool ShowSettings => SettingsPage is not null && Wizard is null && ImportPage is null && PreferencesPage is null;

    /// <summary>A typed-answer question while it is open (clone, delete).</summary>
    [ObservableProperty]
    private PromptViewModel? _prompt;

    public void ShowPrompt(PromptViewModel prompt)
    {
        prompt.Closed += () => Ui.Post(() =>
        {
            if (Prompt == prompt) Prompt = null;
        });
        Prompt = prompt;
    }

    /// <summary>The library folder machines are made in.</summary>
    public string LibraryRoot => Settings.LibraryRoots[0];

    public IReadOnlyList<string> LibraryRoots => Settings.LibraryRoots;

    /// <summary>
    /// "Add machines from a folder…": the folder joins the library and its machines are listed. Picking a machine's own
    /// folder (it holds vm.json or a quickemu .conf beside it) adds the folder it sits in.
    /// </summary>
    public async Task AddLibraryFolderAsync(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd('/');
        var root = await Task.Run(() => File.Exists(Path.Combine(folder, VmQontrol.Core.Machines.MachineStore.FileName)) || File.Exists(folder + ".conf")
            ? Path.GetDirectoryName(folder) ?? folder
            : folder).ConfigureAwait(false);
        var known = IsLibraryRoot(root);
        if (!known) await AddRootAsync(root, "added from the welcome page").ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        Ui.Post(() =>
        {
            var found = _all.Count(m => string.Equals(Path.GetDirectoryName(m.Folder), root, StringComparison.Ordinal));
            var name = Path.GetFileName(root) is { Length: > 0 } n ? n : root;
            Note(known
                ? $"\u201c{name}\u201d is already in your library."
                : $"\u201c{name}\u201d added to your library: {found} machine{(found == 1 ? "" : "s")} found there.");
        });
    }

    private async Task AddRootAsync(string root, string why)
    {
        var settings = await Ui.InvokeAsync(() =>
        {
            Settings = Settings with { LibraryRoots = [.. Settings.LibraryRoots, root] };
            OnPropertyChanged(nameof(LibraryText));
            return Settings;
        }).ConfigureAwait(false);
        await Task.Run(() => settings.Save(Paths)).ConfigureAwait(false);
        Log.Info($"Added {root} to the library ({why}).");
    }

    private bool IsLibraryRoot(string folder) =>
        Settings.LibraryRoots.Any(r => string.Equals(Path.GetFullPath(r).TrimEnd('/'), Path.GetFullPath(folder).TrimEnd('/'), StringComparison.Ordinal));

    /// <summary>After a clone or a delete: the library is read again and, for a clone, the new machine selected.</summary>
    public async Task AfterLifecycleAsync(string? selectDirectory)
    {
        await RefreshAsync().ConfigureAwait(false);
        Ui.Post(() =>
        {
            if (selectDirectory is not null) Selected = _all.FirstOrDefault(m => m.Folder == selectDirectory);
        });
    }

    /// <summary>The USB panel while it is open.</summary>
    [ObservableProperty]
    private UsbPanelViewModel? _usbPanel;

    public void OpenUsb(MachineViewModel machine)
    {
        var panel = new UsbPanelViewModel(machine, this);
        panel.Closed += () => Ui.Post(() =>
        {
            if (UsbPanel == panel) UsbPanel = null;
        });
        UsbPanel = panel;
    }

    /// <summary>The host drives panel while it is open.</summary>
    [ObservableProperty]
    private DrivesPanelViewModel? _drivesPanel;

    public DrivesPanelViewModel OpenDrives(MachineViewModel machine)
    {
        var panel = new DrivesPanelViewModel(machine, this);
        panel.Closed += () => Ui.Post(() =>
        {
            if (DrivesPanel == panel) DrivesPanel = null;
        });
        DrivesPanel = panel;
        return panel;
    }

    public void OpenSettings(MachineViewModel machine) => _ = Task.Run(async () =>
    {
        var host = await HostAsync().ConfigureAwait(false);
        Ui.Post(() =>
        {
            var page = new SettingsViewModel(machine, host);
            page.Closed += () => Ui.Post(() =>
            {
                if (SettingsPage == page) SettingsPage = null;
            });
            SettingsPage = page;
        });
    });

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome), nameof(ShowMachine))]
    private MachineViewModel? _selected;

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryEmpty))]
    private int _machineCount;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private NoticeViewModel? _notice;

    [ObservableProperty]
    private bool _scanning;

    public bool ShowWelcome => Selected is null && Wizard is null && SettingsPage is null && ImportPage is null && PreferencesPage is null;

    public bool ShowMachine => Selected is not null && Wizard is null && SettingsPage is null && ImportPage is null && PreferencesPage is null;

    public bool LibraryEmpty => MachineCount == 0 && !Scanning;

    public bool HasNotice => Notice is not null;

    public string LibraryText => string.Join(", ", Settings.LibraryRoots);

    /// <summary>Set by the window: puts text on the system clipboard.</summary>
    public Func<string, Task>? Clipboard { get; set; }

    public Task<HostCapabilities> HostAsync() => _host.Value;

    /// <summary>
    /// The memory check before <paramref name="machine"/> powers on (Preferences → Memory), read on a worker: free memory
    /// now, less what running machines can still claim. <c>null</c> when it fits, the warning is off, or the machine's
    /// memory comes from reserved huge pages (already set aside).
    /// </summary>
    public Task<MemoryShortfall?> MemoryShortfallAsync(MachineViewModel machine, HostCapabilities host)
    {
        var settings = Settings;
        if (!settings.WarnLowMemory || machine.Machine.Performance.HugePages is not null) return Task.FromResult<MemoryShortfall?>(null);
        var want = machine.Machine.MemoryMiB ?? Recommendations.QuickemuMemoryMiB(host.MemoryGB);
        var running = Machines.Where(m => m != machine && m.Running is not null)
            .Select(m => (Memory: m.Machine.MemoryMiB ?? Recommendations.QuickemuMemoryMiB(host.MemoryGB), m.Running!.Pid, Huge: m.Machine.Performance.HugePages is not null))
            .ToList();
        return Task.Run(() =>
        {
            var (total, available) = MemoryHeadroom.ReadHost();
            var promised = running.Where(r => !r.Huge).Sum(r => MemoryHeadroom.ResidentMiB(r.Pid) is { } rss ? MemoryHeadroom.Promised(r.Memory, rss) : 0);
            var shortfall = MemoryHeadroom.Check(want, available, promised, settings.KeepFreeMiB(total));
            if (shortfall is not null) Log.Info($"{machine.Name}: memory check: wants {want} MiB, {available} MiB available, {promised} MiB promised, keep {shortfall.KeepFreeMiB} MiB free.");
            return shortfall;
        });
    }

    /// <summary>Collects support data (system, checks, logs, machine settings, nothing personal) into one file in <paramref name="folder"/>.</summary>
    public async Task<string?> CollectSupportAsync(string folder)
    {
        Ui.Post(() => Note("Collecting support data…"));
        try
        {
            var host = await (await Ui.InvokeAsync(HostAsync).ConfigureAwait(false)).ConfigureAwait(false);
            var file = Path.Combine(folder, $"vmqontrol-support {DateTime.Now:yyyy-MM-dd HH.mm.ss}.tar.gz");
            var count = await Task.Run(() => SupportBundle.Create(file, Paths, host, Doctor.Run(host, Doctor.DetectThisDistro(), Settings.LibraryRoots[0])).Count).ConfigureAwait(false);
            Ui.Post(() => Note($"Saved {file} ({count} files: logs and settings, with your home folder, user name, notes and account names left out)."));
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => Show(new NoticeViewModel("Support data could not be saved", [ex.Message], isError: true)));
            return null;
        }
    }

    /// <summary>The host check again, from scratch (after installing something it said was missing).</summary>
    [RelayCommand]
    private async Task RecheckHostAsync()
    {
        Ui.Post(() =>
        {
            _host = new(() => Task.Run(() => HostProbe.Probe()));
            Welcome.Rechecking = true;
        });
        await Task.Yield();
        var host = await Ui.InvokeAsync(HostAsync).ConfigureAwait(false);
        var h = await host.ConfigureAwait(false);
        var checks = await Task.Run(() => Doctor.Run(h, Doctor.DetectThisDistro(), Settings.LibraryRoots[0])).ConfigureAwait(false);
        Ui.Post(() =>
        {
            Welcome.Show(checks);
            Welcome.Rechecking = false;
            Note("This computer was checked again.");
        });
    }

    /// <summary>The first look: probe the host, run the host check and read the library, all off the UI thread.</summary>
    public async Task StartAsync()
    {
        Ui.Post(() => Scanning = true);
        _ = FindGuestToolsAsync();
        var host = await HostAsync().ConfigureAwait(false);
        var checks = await Task.Run(() => Doctor.Run(host, Doctor.DetectThisDistro(), Settings.LibraryRoots[0])).ConfigureAwait(false);
        Ui.Post(() => Welcome.Show(checks));
        await RefreshAsync().ConfigureAwait(false);
        _ = WatchHostLoadAsync();
        if (WatchUsb && _hotplug is null)
        {
            _hotplug = new UsbHotplug();
            _hotplug.Plugged += d => Ui.Post(() => UsbPlugged(d));
            _hotplug.Start();
        }
    }

    private UsbHotplug? _hotplug;
    private int[] _runningPids = [];
    private CancellationTokenSource? _load;

    /// <summary>This computer's load once a second, on a worker; the running machines' process ids come back with each post.</summary>
    private async Task WatchHostLoadAsync()
    {
        if (_load is not null) return;
        _load = new CancellationTokenSource();
        var ct = _load.Token;
        var load = new HostLoad();
        while (!ct.IsCancellationRequested)
        {
            var pids = _runningPids;
            if (await Task.Run(() => load.Sample(pids)).ConfigureAwait(false) is { } s)
                Ui.Post(() =>
                {
                    Welcome.AddLoad(s);
                    _runningPids = _all.Where(m => m.Running is not null).Select(m => m.Running!.Pid).ToArray();
                });
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Whether USB devices plugged in while the app runs are offered to the running machine (tests turn it off).</summary>
    public bool WatchUsb { get; init; } = true;

    /// <summary>
    /// The one case VM Qontrol cannot hand a device over by itself: this computer lacks spice-gtk's USB helper, which
    /// opens USB devices to the person at the computer (the same one virt-viewer and GNOME Boxes use).
    /// </summary>
    public void ShowUsbSetup(string deviceName)
    {
        var install = Doctor.InstallCommand(Doctor.DetectThisDistro(), "usb-helper");
        var notice = new NoticeViewModel("One package is needed for USB devices",
            [$"Handing {deviceName} to a machine needs spice-gtk's USB helper, the part virt-viewer and GNOME Boxes use to open USB devices to you. It is not installed on this computer. Install it once; after that, Connect simply works:"],
            confirm: "Copy the install command", dismiss: "Not now", icon: "IconUsb", code: install);
        Show(notice);
        _ = notice.Answer.ContinueWith(t =>
        {
            if (t.Result) Ui.Post(() => CopyToClipboard(install, "The install command is on the clipboard."));
        }, TaskScheduler.Default);
    }

    /// <summary>The window closed: stop watching for USB devices and this computer's load.</summary>
    public void Stop()
    {
        _hotplug?.Dispose();
        _hotplug = null;
        _load?.Cancel();
    }

    /// <summary>
    /// A USB device was plugged into this computer. A running machine with a rule for it takes it (opened to the person
    /// through spice-gtk's helper first, as virt-viewer does), and that is only reported. Otherwise, as Workstation does,
    /// the machine in view (or the only running one) is offered it, with "Always connect it" to make the rule.
    /// </summary>
    public void UsbPlugged(HostUsbDevice device)
    {
        var running = _all.Where(m => m.Watcher is not null).ToList();
        if (running.Count == 0) return;
        _ = Task.Run(async () =>
        {
            // A device a machine holds by its port that re-enumerated (receivers do when taken over): QEMU finds it again
            // by itself; its new node only needs opening to QEMU. Nothing to ask.
            foreach (var m in running)
            {
                if (m.Watcher is { } w && (await UsbHost.AttachedAsync(w).ConfigureAwait(false)).Contains(device.QemuId))
                {
                    await UsbAccess.GrantAsync(device, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    return;
                }
            }

            Ui.Post(() => OfferPlugged(device, running));
        });
    }

    private void OfferPlugged(HostUsbDevice device, List<MachineViewModel> running)
    {
        if (running.FirstOrDefault(m => m.Machine.Usb.Passthrough.Any(u => u.VendorId == device.VendorId && u.ProductId == device.ProductId)) is { } ruled)
        {
            _ = Task.Run(async () =>
            {
                var refused = await UsbAccess.GrantAsync(device, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Ui.Post(() =>
                {
                    if (refused is null) Note($"{device.Name} was plugged in and went to {ruled.Name} (it always connects there).");
                    else if (!UsbAccess.Available) ShowUsbSetup(device.Name);
                    else Note($"{device.Name} was plugged in but could not be opened for {ruled.Name}: {refused}.");
                });
            });
            return;
        }

        if (Settings.UsbNeverAsk.Contains(device.Id))
        {
            Note($"{device.Name} was plugged in. It is set not to be offered to machines; its row in USB devices can change that.");
            return;
        }

        var target = Selected is { Watcher: not null } s ? s : running.Count == 1 ? running[0] : null;
        if (target is null || Notice is not null || Prompt is not null || Wizard is not null)
        {
            Note($"{device.Name} was plugged in. To hand it to a machine, open that machine's USB devices.");
            return;
        }

        if (!device.Writable && !UsbAccess.Available)
        {
            ShowUsbSetup(device.Name);
            return;
        }

        var notice = new NoticeViewModel("New USB device",
            [$"Connect it to {target.Name}? It leaves this computer until you give it back or {target.Name} stops."],
            confirm: $"Connect to {target.Name}", dismiss: "Keep it on this computer", checkText: $"Always connect it to {target.Name}",
            icon: "IconUsb", itemTitle: device.Name, itemDetail: $"{device.Id} · {device.Speed} · port {device.Port}", aside: "Don't ask about this device");
        Show(notice);
        _ = Task.Run(async () =>
        {
            var yes = await notice.Answer.ConfigureAwait(false);
            if (notice.AsideChosen)
            {
                SetUsbNeverAsk(device.Id, true);
                Ui.Post(() => Note($"{device.Name} stays on this computer when plugged in; its USB devices panel can offer it again."));
                return;
            }

            if (!yes || target.Watcher is not { } watcher) return;
            try
            {
                // A rule takes the device by kind, so it also follows when it is unplugged and plugged back.
                if (notice.Checked)
                {
                    if (await target.SetUsbRuleAsync(device, true).ConfigureAwait(false) is { } refused)
                    {
                        Ui.Post(() => Show(new NoticeViewModel($"{device.Name} could not be connected", [refused, "The rule is saved: it connects at the next power on."], isError: true)));
                        return;
                    }
                }
                else
                {
                    await UsbHost.AttachAsync(watcher, device).ConfigureAwait(false);
                }

                Ui.Post(() => Note(notice.Checked ? $"{device.Name} is in {target.Name}, and always connects there." : $"{device.Name} is in {target.Name}."));
            }
            catch (Exception ex) when (ex is QmpException or IOException)
            {
                Ui.Post(() => Show(new NoticeViewModel($"{device.Name} could not be connected", [ex.Message], isError: true)));
            }
        });
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Ui.Post(() => Scanning = true);
        var entries = await Task.Run(() => Settings.LibraryRoots.SelectMany(r => LibraryScanner.Scan(r, Paths.State)).Where(e => e.Machine is not null).ToList()).ConfigureAwait(false);
        var host = await HostAsync().ConfigureAwait(false);
        var models = new List<MachineViewModel>();
        Ui.Post(() =>
        {
            var selectedId = Selected?.Id;
            _all.Clear();
            foreach (var entry in entries)
            {
                var vm = new MachineViewModel(this, entry)
                {
                    IsFavourite = Settings.Favourites.Contains(entry.Machine!.Id),
                    FolderName = Settings.Folders.GetValueOrDefault(entry.Machine!.Id, ""),
                };
                _all.Add(vm);
                models.Add(vm);
            }

            ApplyFilter();
            MachineCount = _all.Count;
            Scanning = false;
            Selected = _all.FirstOrDefault(m => m.Id == selectedId);
            if (OpenOnStart is { } open)
            {
                Selected = _all.FirstOrDefault(m => string.Equals(m.Name, open, StringComparison.OrdinalIgnoreCase)) ?? Selected;
                if (Selected is null) Note($"No machine called '{open}' in the library.");
                OpenOnStart = null;
            }
            Note(_all.Count == 0 ? "No machines yet." : $"{_all.Count} machine{(_all.Count == 1 ? "" : "s")} in the library.");
            foreach (var m in models) _ = m.LoadAsync(host);
        });
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(MachineViewModel? oldValue, MachineViewModel? newValue)
    {
        SettingsPage = null;
        PreferencesPage = null;
        // Picking a machine leaves the import page, unless it is copying (its progress stays on screen).
        if (newValue is not null && ImportPage is { Busy: false }) ImportPage = null;
        OnPropertyChanged(nameof(SidebarSelection));
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    private void ApplyFilter()
    {
        var term = Search.Trim();
        var shown = _all
            .Where(m => term.Length == 0 || m.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || m.LibraryLabel.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.IsFavourite).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var keep = Selected;
        Machines.Clear();
        foreach (var m in shown.OrderBy(m => m.FolderName.Length == 0 ? "" : "\u0001" + m.FolderName, StringComparer.CurrentCultureIgnoreCase)) Machines.Add(m);
        // Unfiled machines first, then each folder under its heading.
        string? previous = null;
        SidebarItems.Clear();
        foreach (var m in Machines)
        {
            m.ShowFolderHeader = m.FolderName.Length > 0 && m.FolderName != previous;
            if (m.ShowFolderHeader) SidebarItems.Add(new FolderHeading(m.FolderName));
            SidebarItems.Add(m);
            previous = m.FolderName;
        }

        OnPropertyChanged(nameof(SidebarSelection));
        if (keep is not null && shown.Contains(keep)) Selected = keep;
    }

    /// <summary>Whether a device is offered to machines when it is plugged in. Any thread.</summary>
    public void SetUsbNeverAsk(string id, bool never) => UpdateSettings(s =>
    {
        var set = new HashSet<string>(s.UsbNeverAsk, StringComparer.Ordinal);
        if (never) set.Add(id);
        else set.Remove(id);
        return s with { UsbNeverAsk = set };
    });

    /// <summary>Changes the app's settings and saves them on a worker. Any thread; at once on the UI thread.</summary>
    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        void Apply()
        {
            Settings = change(Settings);
            OnPropertyChanged(nameof(LibraryText));
            var settings = Settings;
            _ = Task.Run(() => settings.Save(Paths));
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Apply();
        else Ui.Post(Apply);
    }

    /// <summary>The virtual network editor while it is open.</summary>
    [ObservableProperty]
    private NetworksViewModel? _networks;

    public void OpenNetwork() => OpenNetworks();

    [RelayCommand]
    public void OpenNetworks()
    {
        var editor = new NetworksViewModel(this);
        editor.Closed += () => Ui.Post(() =>
        {
            if (Networks == editor) Networks = null;
        });
        Networks = editor;
    }

    /// <summary>Keeps the VirtIO driver disc's path for next time. Any thread.</summary>
    public void RememberDriversIso(string path)
    {
        Ui.Post(() =>
        {
            if (Settings.DriversIso == path) return;
            Settings = Settings with { DriversIso = path };
            var settings = Settings;
            _ = Task.Run(() => settings.Save(Paths));
        });
    }

    public void SetFavourite(MachineViewModel machine, bool favourite)
    {
        machine.IsFavourite = favourite;
        var set = new HashSet<string>(Settings.Favourites);
        if (favourite) set.Add(machine.Id);
        else set.Remove(machine.Id);
        Settings = Settings with { Favourites = set };
        var settings = Settings;
        _ = Task.Run(() => settings.Save(Paths));
        ApplyFilter();
    }

    [RelayCommand]
    private void ShowHome()
    {
        Wizard = null;
        Selected = null;
    }

    [RelayCommand]
    public void NewMachine()
    {
        if (Wizard is not null) return;
        var wizard = new NewMachineViewModel(this);
        wizard.Closed += () => Ui.Post(() =>
        {
            if (Wizard == wizard) Wizard = null;
        });
        Wizard = wizard;
    }

    /// <summary>The wizard's Easy Install pages for a machine that exists and is not installed yet.</summary>
    public void SetUpEasyInstall(MachineViewModel machine)
    {
        if (Wizard is not null) return;
        var wizard = new NewMachineViewModel(this, machine.Machine, machine.Folder);
        wizard.Closed += () => Ui.Post(() =>
        {
            if (Wizard == wizard) Wizard = null;
            Selected ??= machine;
        });
        Wizard = wizard;
    }

    /// <summary>After Easy Install was set up on an existing machine: its page shows the new card, and it powers on if asked.</summary>
    public void FinishEasyInstallSetup(string directory, Core.Machines.MachineConfig machine, bool powerOn)
    {
        var vm = _all.FirstOrDefault(m => m.Folder == directory);
        if (vm is null) return;
        vm.Reload(machine);
        Wizard = null;
        Selected = vm;
        Note($"{machine.Name} will install {machine.EasyInstall?.System ?? "its system"} by itself.");
        if (powerOn) vm.PowerOnCommand.Execute(null);
    }

    /// <summary>After a move: the new folder joins the library if it is not in it yet, and the machine stays selected.</summary>
    public async Task FinishMoveAsync(string directory)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) is { } parent && !IsLibraryRoot(parent))
            await AddRootAsync(parent, "a machine was moved there").ConfigureAwait(false);

        await RefreshAsync().ConfigureAwait(false);
        Ui.Post(() =>
        {
            if (_all.FirstOrDefault(m => m.Folder == directory) is { } moved)
            {
                Selected = moved;
                Note($"{moved.Name} is now in {directory}.");
            }
        });
    }

    /// <summary>After the wizard: the new machine appears selected in the library and, if asked, powers on.</summary>
    public async Task FinishNewMachineAsync(string directory, bool powerOn)
    {
        // A machine made in a folder the library does not look in yet: that folder joins the library, or the machine
        // would exist on disk and nowhere in the app.
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) is { } parent && !IsLibraryRoot(parent))
            await AddRootAsync(parent, "a new machine was made there").ConfigureAwait(false);

        await RefreshAsync().ConfigureAwait(false);
        Ui.Post(() =>
        {
            var created = _all.FirstOrDefault(m => m.Folder == directory);
            if (created is null) return;
            Selected = created;
            Note($"{created.Name} is ready.");
            if (powerOn) created.PowerOnCommand.Execute(null);
        });
    }

    /// <summary>The library's templates, by name (the wizard offers them as a source).</summary>
    public IReadOnlyList<MachineViewModel> Templates => _all.Where(m => m.IsTemplate).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Powers on every machine of a library folder that is off, one after another, without opening their screens
    /// (VirtualBox's group Start; Workstation's old Teams). Each still gets its own checks (memory, missing files).</summary>
    [RelayCommand]
    private async Task PowerOnFolderAsync(string folder)
    {
        var machines = _all.Where(m => m.FolderName == folder && m.CanPowerOn).ToList();
        Note(machines.Count == 0 ? $"Every machine in {folder} is already on." : $"Powering on {machines.Count} machine{(machines.Count == 1 ? "" : "s")} in {folder}…");
        foreach (var m in machines) await m.PowerOnInBackgroundCommand.ExecuteAsync(null);
    }

    /// <summary>Asks every running machine of a folder to shut down, as its power button would.</summary>
    [RelayCommand]
    private async Task ShutDownFolderAsync(string folder)
    {
        var machines = _all.Where(m => m.FolderName == folder && m.IsRunning).ToList();
        Note(machines.Count == 0 ? $"No machine in {folder} is running." : $"Asking {machines.Count} machine{(machines.Count == 1 ? "" : "s")} in {folder} to shut down.");
        foreach (var m in machines) await m.ShutDownCommand.ExecuteAsync(null);
    }

    public IReadOnlyList<string> FolderNames => _all.Select(m => m.FolderName).Where(f => f.Length > 0).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    public void AskFolder(MachineViewModel machine)
    {
        var prompt = new FolderPromptViewModel(machine, FolderNames);
        prompt.Done += name =>
        {
            FolderPrompt = null;
            if (name is not null) SetFolder(machine, name);
        };
        FolderPrompt = prompt;
    }

    public void SetFolder(MachineViewModel machine, string folder)
    {
        folder = folder.Trim();
        machine.FolderName = folder;
        var folders = new Dictionary<string, string>(Settings.Folders);
        if (folder.Length == 0) folders.Remove(machine.Id);
        else folders[machine.Id] = folder;
        Settings = Settings with { Folders = folders };
        var settings = Settings;
        _ = Task.Run(() => settings.Save(Paths));
        ApplyFilter();
        Note(folder.Length == 0 ? $"{machine.Name} is no longer in a folder." : $"{machine.Name} is in {folder}.");
    }

    public void Show(NoticeViewModel notice)
    {
        notice.Closed += () => Ui.Post(() =>
        {
            if (Notice == notice) Notice = null;
        });
        Notice = notice;
    }

    /// <summary>A line for the status bar and the log.</summary>
    /// <summary>
    /// The status bar's standing hint while a viewer can hold a machine's mouse (the viewer is not ours, so this is where
    /// VM Qontrol says how to get it back); <c>null</c> when every machine's mouse moves freely.
    /// </summary>
    [ObservableProperty] private string? _mouseHint;

    /// <summary>A machine's capture state changed: the bar shows the first machine that can hold the mouse.</summary>
    public void MouseCaptureChanged() =>
        MouseHint = _all.FirstOrDefault(m => m.CanHoldMouse) is { } m
            ? $"releases the mouse from {m.Name}. Once its system is installed, the mouse moves freely by itself."
            : null;

    public void Note(string text)
    {
        Status = text;
        Log.Info(text);
    }

    public void CopyToClipboard(string text, string done)
    {
        if (Clipboard is null) return;
        _ = Clipboard(text);
        Note(done);
    }

    [RelayCommand]
    private void CopyFix(string? command)
    {
        if (command is not null) CopyToClipboard(command, "The command is on the clipboard. Paste it into a terminal.");
    }
}

/// <summary>A folder's heading in the sidebar.</summary>
public sealed record FolderHeading(string Name)
{
    public string Header => Name.ToUpperInvariant();
}
