using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Library;

namespace VmQontrol.App.ViewModels;

/// <summary>
/// The main window's state: the library, the selected machine, the welcome page and messages. Every scan and probe runs
/// on a worker; results come back through <see cref="Ui.Post"/>.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly List<MachineViewModel> _all = [];
    private readonly Lazy<Task<HostCapabilities>> _host = new(() => Task.Run(() => HostProbe.Probe()));

    public ShellViewModel(QontrolPaths paths, AppSettings settings)
    {
        Paths = paths;
        Settings = settings;
        Palette = new PaletteViewModel(this);
    }

    public PaletteViewModel Palette { get; }

    public QontrolPaths Paths { get; }

    public AppSettings Settings { get; private set; }

    public WelcomeViewModel Welcome { get; } = new();

    /// <summary>The machines the sidebar shows: favourites first, then by name, filtered by the search box.</summary>
    public ObservableCollection<MachineViewModel> Machines { get; } = [];

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

    public bool ShowWelcome => Selected is null;

    public bool ShowMachine => Selected is not null;

    public bool LibraryEmpty => MachineCount == 0 && !Scanning;

    public bool HasNotice => Notice is not null;

    public string LibraryText => string.Join(", ", Settings.LibraryRoots);

    /// <summary>Set by the window: puts text on the system clipboard.</summary>
    public Func<string, Task>? Clipboard { get; set; }

    public Task<HostCapabilities> HostAsync() => _host.Value;

    /// <summary>The first look: probe the host, run the host check and read the library, all off the UI thread.</summary>
    public async Task StartAsync()
    {
        Ui.Post(() => Scanning = true);
        var host = await HostAsync().ConfigureAwait(false);
        var checks = await Task.Run(() => Doctor.Run(host, Doctor.DetectThisDistro(), Settings.LibraryRoots[0])).ConfigureAwait(false);
        Ui.Post(() => Welcome.Show(checks));
        await RefreshAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Ui.Post(() => Scanning = true);
        var entries = await Task.Run(() => Settings.LibraryRoots.SelectMany(LibraryScanner.Scan).Where(e => e.Machine is not null).ToList()).ConfigureAwait(false);
        var host = await HostAsync().ConfigureAwait(false);
        var models = new List<MachineViewModel>();
        Ui.Post(() =>
        {
            var selectedId = Selected?.Id;
            _all.Clear();
            foreach (var entry in entries)
            {
                var vm = new MachineViewModel(this, entry) { IsFavourite = Settings.Favourites.Contains(entry.Machine!.Id) };
                _all.Add(vm);
                models.Add(vm);
            }

            ApplyFilter();
            MachineCount = _all.Count;
            Scanning = false;
            Selected = _all.FirstOrDefault(m => m.Id == selectedId);
            Note(_all.Count == 0 ? "No machines yet." : $"{_all.Count} machine{(_all.Count == 1 ? "" : "s")} in the library.");
            foreach (var m in models) _ = m.LoadAsync(host);
        });
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(MachineViewModel? oldValue, MachineViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    private void ApplyFilter()
    {
        var term = Search.Trim();
        var shown = _all
            .Where(m => term.Length == 0 || m.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || m.GuestLabel.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.IsFavourite).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var keep = Selected;
        Machines.Clear();
        foreach (var m in shown) Machines.Add(m);
        if (keep is not null && shown.Contains(keep)) Selected = keep;
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
    private void ShowHome() => Selected = null;

    public void Show(NoticeViewModel notice)
    {
        notice.Closed += () => Ui.Post(() =>
        {
            if (Notice == notice) Notice = null;
        });
        Notice = notice;
    }

    /// <summary>A line for the status bar and the log.</summary>
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
