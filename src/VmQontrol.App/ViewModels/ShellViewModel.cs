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
    [NotifyPropertyChangedFor(nameof(ShowWelcome), nameof(ShowMachine), nameof(ShowWizard))]
    private NewMachineViewModel? _wizard;

    public bool ShowWizard => Wizard is not null;

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

    public bool ShowWelcome => Selected is null && Wizard is null;

    public bool ShowMachine => Selected is not null && Wizard is null;

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
        OnPropertyChanged(nameof(SidebarSelection));
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

    /// <summary>After the wizard: the new machine appears selected in the library and, if asked, powers on.</summary>
    public async Task FinishNewMachineAsync(string directory, bool powerOn)
    {
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
