using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.OsInfo;

namespace VmQontrol.App.ViewModels;

public enum SourceKind
{
    Disc,
    Download,
    Later,
    Template,
}

/// <summary>A guest system the person can pick by hand when nothing was detected (or to correct a detection).</summary>
public sealed record GuestChoice(string Label, string Os, string Release)
{
    public override string ToString() => Label;
}

/// <summary>
/// The New Machine wizard. Every page answers one question, says why it matters, and starts on the recommended answer;
/// detection fills in what can be known from the disc so the person is never asked it. Slow work (reading a disc,
/// loading the catalogue, downloading, creating) runs on workers.
/// </summary>
public sealed partial class NewMachineViewModel : ObservableObject
{
    public static readonly string[] StepTitles = ["Installer", "System", "Easy Install", "Name", "Hardware", "Summary"];

    private readonly ShellViewModel _shell;
    private OsInfoDb? _osinfo;
    private HostCapabilities? _host;
    private CancellationTokenSource? _download;

    public NewMachineViewModel(ShellViewModel shell)
    {
        _shell = shell;
        LibraryRoot = shell.Settings.LibraryRoots[0];
        Locale = CultureInfo.CurrentCulture.Name is { Length: >= 2 } c && c.Contains('-') ? c : "en-US";
        TimeZone = TimeZoneInfo.TryConvertIanaIdToWindowsId(TimeZoneInfo.Local.Id, out var tz) ? tz : "UTC";
        UserName = Environment.UserName is { Length: > 0 } u ? char.ToUpperInvariant(u[0]) + u[1..] : "User";
        Templates = shell.Templates;
        _ = Task.Run(async () =>
        {
            var host = await shell.HostAsync().ConfigureAwait(false);
            var db = OsInfoDb.LoadInstalled();
            Ui.Post(() =>
            {
                _host = host;
                _osinfo = db;
                Recalculate();
            });
        });
    }

    public IReadOnlyList<GuestChoice> GuestChoices { get; } = new GuestChoiceList();

    /// <summary>The systems a person can pick by hand (the wizard and Settings share it).</summary>
    public sealed class GuestChoiceList : List<GuestChoice>
    {
        public GuestChoiceList()
            : base(
            [
                new("Windows 11", "windows", "11"),
                new("Windows 10", "windows", "10"),
                new("Windows Server", "windows-server", ""),
                new("Linux (modern)", "linux", ""),
                new("Linux (older than 2010)", "linux_old", ""),
                new("macOS", "macos", ""),
                new("FreeBSD", "freebsd", ""),
                new("Other BSD", "netbsd", ""),
                new("Haiku", "haiku", ""),
                new("Solaris / illumos", "solaris", ""),
                new("ReactOS", "reactos", ""),
                new("FreeDOS", "freedos", ""),
            ])
        {
        }
    }

    // ── Steps
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(IsFirst), nameof(IsLast), nameof(NextText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private int _step;

    public string StepTitle => StepTitles[Step];

    public bool IsFirst => Step == 0;

    public bool IsLast => Step == StepTitles.Length - 1;

    public string NextText => IsLast ? (Source == SourceKind.Download ? "Download and create" : "Create machine") : "Continue";

    /// <summary>A machine made from a template takes its system and hardware from it: only the name and the kind of copy are asked.</summary>
    private bool Skipped(int step) => step switch
    {
        1 or 4 => IsFromTemplate,
        2 => !EasyInstallPossible,
        _ => false,
    };

    /// <summary>The step rail: each step's title and whether it is the current one.</summary>
    public IReadOnlyList<StepItem> Steps => StepTitles.Select((t, i) => new StepItem(t, i == Step, i < Step)).ToList();

    partial void OnStepChanged(int value) => OnPropertyChanged(nameof(Steps));

    // ── Installer
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisc), nameof(IsDownload), nameof(IsLater), nameof(IsFromTemplate), nameof(NextText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private SourceKind _source = SourceKind.Disc;

    public bool IsDisc => Source == SourceKind.Disc;

    public bool IsDownload => Source == SourceKind.Download;

    public bool IsLater => Source == SourceKind.Later;

    public bool IsFromTemplate => Source == SourceKind.Template;

    // From a template
    /// <summary>The library's templates, newest list from the shell when the wizard opened.</summary>
    public IReadOnlyList<MachineViewModel> Templates { get; }

    public bool HasTemplates => Templates.Count > 0;

    public string TemplateCardText => Templates.Count switch
    {
        0 => "No templates yet: turn an installed machine into one from its More menu.",
        1 => $"A copy of your template {Templates[0].Name}, installed and ready.",
        var n => $"A copy of one of your {n} templates, installed and ready.",
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private MachineViewModel? _template;

    /// <summary>Linked is the recommended copy from a template: the template never changes, so sharing its disks is safe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullClone))]
    private bool _linkedClone = true;

    public bool FullClone
    {
        get => !LinkedClone;
        set => LinkedClone = !value;
    }

    partial void OnTemplateChanged(MachineViewModel? value)
    {
        if (value is null) return;
        Guest = value.Machine.Guest;
        DetectedText = $"From the template {value.Name}.";
        if (Name == "New machine" || Name.Length == 0 || Templates.Any(t => Name == $"{t.Name} 1")) Name = $"{value.Name} 1";
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string? _isoPath;

    [ObservableProperty]
    private string _isoSummary = "";

    [ObservableProperty]
    private bool _isoProblem;

    [ObservableProperty]
    private bool _detecting;

    [ObservableProperty]
    private IReadOnlyList<WindowsEdition> _editions = [];

    [ObservableProperty]
    private WindowsEdition? _edition;

    public bool HasEditions => Editions.Count > 1;

    partial void OnEditionsChanged(IReadOnlyList<WindowsEdition> value) => OnPropertyChanged(nameof(HasEditions));

    // Download catalogue
    public ObservableCollection<CatalogueSystem> Systems { get; } = [];

    private IReadOnlyList<CatalogueSystem> _allSystems = [];

    [ObservableProperty]
    private string _catalogueSearch = "";

    [ObservableProperty]
    private string _catalogueNote = "";

    [ObservableProperty]
    private CatalogueSystem? _system;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private CatalogueEntry? _entry;

    public bool CatalogueAvailable => _allSystems.Count > 0;

    // ── System
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindows), nameof(GuestLabel))]
    private GuestConfig _guest = new();

    [ObservableProperty]
    private string _detectedText = "";

    [ObservableProperty]
    private GuestChoice? _guestOverride;

    public bool IsWindows => Guest.Os is "windows" or "windows-server";

    public string GuestLabel => GuestNames.Describe(Guest);

    partial void OnGuestOverrideChanged(GuestChoice? value)
    {
        if (value is null) return;
        Guest = new GuestConfig { Os = value.Os, Release = value.Release, OsInfoId = Guest.OsInfoId };
        Recalculate();
    }

    // ── Easy Install (Windows)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EasyInstallShown))]
    private bool _easyInstall = true;

    public bool EasyInstallShown => EasyInstallPossible && EasyInstall;

    public bool EasyInstallPossible => Source == SourceKind.Disc && IsoPath is not null && (IsWindows || LinuxInstallerFound != LinuxInstaller.None);

    /// <summary>The Linux installer on the disc, when Easy Install knows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EasyInstallPossible), nameof(EasyInstallShown), nameof(IsLinuxEasy), nameof(EasyInstallTitle), nameof(EasyInstallExplain))]
    private LinuxInstaller _linuxInstallerFound;

    public bool IsLinuxEasy => !IsWindows && LinuxInstallerFound != LinuxInstaller.None;

    public bool IsWindowsEasy => IsWindows;

    public string EasyInstallTitle => IsWindows ? "Install Windows for you?" : "Install it for you?";

    public string EasyInstallExplain => IsWindows
        ? "Windows Setup normally asks a dozen questions. VM Qontrol answers them from what you enter here, then you come back to a desktop."
        : LinuxInstallerFound switch
        {
            LinuxInstaller.Ubuntu => "Ubuntu's installer is answered from what you enter here (its autoinstall), with the disk used whole, SSH, the guest agent and the clipboard agent installed.",
            LinuxInstaller.Anaconda => "The installer (Anaconda) is answered with a kickstart made from what you enter here: the disk used whole, your account, the guest agent and the clipboard agent.",
            LinuxInstaller.Debian => "Debian's installer is answered with a preseed made from what you enter here: the disk used whole, your account, SSH, the guest agent and the clipboard agent.",
            _ => "",
        };

    /// <summary>Linux needs a password: its installers make no account without one.</summary>
    public bool PasswordRequired => IsLinuxEasy && EasyInstall;

    public string PasswordNote => PasswordRequired
        ? "Required: Linux installers make no account without a password. It is stored hashed in the machine's answer file."
        : "May be left empty. It is stored in the machine's answer file; use one you do not use elsewhere.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _userName = "User";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _password = "";

    /// <summary>The IANA time zone for Linux answer files (this computer's).</summary>
    public string IanaTimeZone { get; } = TimeZoneInfo.Local.Id.Contains('/') ? TimeZoneInfo.Local.Id : "UTC";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsOwnKey), nameof(KeyNote))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _productKey = "";

    /// <summary>Home editions have no published setup key: Easy Install needs the person's own.</summary>
    public bool NeedsOwnKey => EasyInstallShown && IsWindows && ProductKey.Trim().Length == 0 && WindowsSetupKeys.For(Edition?.Name ?? "", ConsumerDisc) is null;

    /// <summary>A consumer disc (Home, Pro…) takes retail setup keys; a volume-licence disc takes KMS client setup keys.</summary>
    public bool ConsumerDisc => !Editions.Any(e => e.Name.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)) || Editions.Any(e => e.Name.Contains("Home", StringComparison.OrdinalIgnoreCase));

    public string KeyNote => NeedsOwnKey
        ? $"{Edition?.DisplayName ?? "This edition"} needs your product key to install without questions. Enter it here, or choose a Pro edition on the first page."
        : "Leave empty to install now and activate later with your own licence.";

    [ObservableProperty]
    private string _locale;

    [ObservableProperty]
    private string _timeZone;

    /// <summary>Languages and regions by name ("English (United States)"), each with the code Windows uses ("en-US").</summary>
    public IReadOnlyList<NamedChoice> Locales { get; } = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Where(c => c.Name.Contains('-'))
        .Select(c => new NamedChoice(c.Name, c.EnglishName))
        .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Windows time zones by their names, from this computer's zone list.</summary>
    public IReadOnlyList<NamedChoice> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(z => TimeZoneInfo.TryConvertIanaIdToWindowsId(z.Id, out var w) ? new NamedChoice(w, $"{w} ({z.Id})") : null)
        .Where(c => c is not null).Select(c => c!).DistinctBy(c => c.Value)
        .OrderBy(c => c.Label, StringComparer.Ordinal).ToList();

    public NamedChoice? LocaleChoice
    {
        get => Locales.FirstOrDefault(l => l.Value == Locale);
        set
        {
            if (value is not null) Locale = value.Value;
            OnPropertyChanged();
        }
    }

    public NamedChoice? TimeZoneChoice
    {
        get => TimeZones.FirstOrDefault(t => t.Value == TimeZone);
        set
        {
            if (value is not null) TimeZone = value.Value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string? _driversIso;

    // ── Name and location
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderPreview))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _name = "New machine";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderPreview))]
    private string _libraryRoot;

    public string FolderPreview => MachineFactory.FolderFor(LibraryRoot, Name);

    // ── Hardware (whole units for the controls; recommendations come with their reasons)
    [ObservableProperty] private decimal _memoryGb = 4;
    [ObservableProperty] private decimal _cores = 2;
    [ObservableProperty] private decimal _diskGb = 64;
    [ObservableProperty] private decimal _memoryMinimumGb = 1;
    [ObservableProperty] private decimal _memoryMaximumGb = 16;
    [ObservableProperty] private decimal _coresMaximum = 4;
    [ObservableProperty] private decimal _diskMinimumGb = 8;
    [ObservableProperty] private string _memoryReason = "";
    [ObservableProperty] private string _coresReason = "";
    [ObservableProperty] private string _diskReason = "";
    [ObservableProperty] private bool _uefi = true;
    [ObservableProperty] private bool _secureBoot;
    [ObservableProperty] private bool _tpm;
    [ObservableProperty] private string _firmwareReason = "";

    // ── Finish
    [ObservableProperty] private bool _powerOnAfter = true;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressKnown;

    public IReadOnlyList<DetailRow> Summary => BuildSummary();

    public event Action? Closed;

    // ── Navigation
    private bool CanNext() => !Busy && Step switch
    {
        0 => Source switch { SourceKind.Disc => IsoPath is not null && !IsoProblem, SourceKind.Download => Entry is not null, SourceKind.Template => Template is not null, _ => true },
        2 => !EasyInstallShown || (UserName.Trim().Length > 0 && !NeedsOwnKey && (!PasswordRequired || Password.Length > 0)),
        3 => Name.Trim().Length > 0,
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync()
    {
        if (IsLast)
        {
            await CreateAsync().ConfigureAwait(false);
            return;
        }

        var next = Step + 1;
        // Easy Install is only asked when the disc can be installed for you; a template skips system and hardware too.
        while (Skipped(next)) next++;
        Step = next;
        if (Step == 5) OnPropertyChanged(nameof(Summary));
    }

    private bool CanBack() => Step > 0 && !Busy;

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        var previous = Step - 1;
        while (previous > 0 && Skipped(previous)) previous--;
        Step = previous;
    }

    [RelayCommand]
    private void Cancel()
    {
        _download?.Cancel();
        Closed?.Invoke();
    }

    [RelayCommand]
    private void ChooseSource(SourceKind kind)
    {
        Source = kind;
        if (kind == SourceKind.Download && _allSystems.Count == 0) _ = LoadCatalogueAsync();
        if (kind == SourceKind.Later) Detected(new GuestConfig { Os = "linux" }, "Choose the system you will install on the next page.");
        if (kind == SourceKind.Template && Template is null && Templates.Count == 1) Template = Templates[0];
        NextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(EasyInstallPossible));
        OnPropertyChanged(nameof(EasyInstallShown));
    }

    // ── Disc detection
    public async Task UseIsoAsync(string path)
    {
        IsoPath = path;
        Source = SourceKind.Disc;
        Detecting = true;
        IsoSummary = "Reading the disc…";
        var result = await Task.Run(() =>
        {
            var volume = IsoVolume.Read(path);
            if (volume is null) return new DiscDetection(true, null, "This file is not a disc image. A failed download can save an error page with an .iso name; download it again.", [], "");
            if (WindowsMedia.Inspect(path) is { } w)
            {
                var g = new GuestConfig { Os = w.Server ? "windows-server" : "windows", Release = w.Release };
                return new DiscDetection(false, g, $"{w.Description}, with {w.Editions.Count} edition{(w.Editions.Count == 1 ? "" : "s")}.", w.Editions, $"Windows {w.Release}");
            }

            var db = _osinfo ?? OsInfoDb.LoadInstalled();
            var linux = LinuxEasyInstall.Detect(path);
            if (db?.Identify(volume) is { } match)
            {
                var also = match.Ambiguous.Count > 0 ? $" It could also be {string.Join(" or ", match.Ambiguous.Select(a => a.Name))}; change it on the next page if so." : "";
                return new DiscDetection(false, GuestMapping.ToGuest(match.Os), $"{match.Os.Name}.{also}", [], match.Os.Name, linux);
            }

            return new DiscDetection(false, new GuestConfig { Os = "linux" }, $"A disc labelled \"{volume.VolumeId}\". Its system is not known; choose it on the next page.", [], volume.VolumeId, linux);
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            Detecting = false;
            IsoProblem = result.Problem;
            IsoSummary = result.Text;
            Editions = result.Editions;
            Edition = result.Editions.FirstOrDefault(e => e.DisplayName.EndsWith(" Pro", StringComparison.Ordinal)) ?? result.Editions.FirstOrDefault();
            if (result.Guest is { } g) Detected(g, result.Text);
            LinuxInstallerFound = result.Linux;
            if (result.Name.Length > 0 && (Name == "New machine" || Name.Length == 0)) Name = Edition?.DisplayName ?? result.Name;
            OnPropertyChanged(nameof(EasyInstallPossible));
            OnPropertyChanged(nameof(EasyInstallShown));
            NextCommand.NotifyCanExecuteChanged();
        });
    }

    partial void OnEasyInstallChanged(bool value)
    {
        OnPropertyChanged(nameof(PasswordRequired));
        OnPropertyChanged(nameof(PasswordNote));
        NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnEditionChanged(WindowsEdition? value)
    {
        OnPropertyChanged(nameof(NeedsOwnKey));
        OnPropertyChanged(nameof(KeyNote));
        if (value is not null && (Name.StartsWith("Windows", StringComparison.Ordinal) || Name == "New machine")) Name = value.DisplayName;
    }

    private void Detected(GuestConfig guest, string text)
    {
        Guest = guest;
        DetectedText = text;
        GuestOverride = null;
        Recalculate();
    }

    // ── Catalogue
    private async Task LoadCatalogueAsync()
    {
        CatalogueNote = "Loading the catalogue…";
        var systems = await Catalogue.LoadAsync(_shell.Paths).ConfigureAwait(false);
        Ui.Post(() =>
        {
            _allSystems = systems;
            CatalogueNote = systems.Count == 0
                ? "The download catalogue needs quickget (part of quickemu). " + Doctor.InstallCommand(Doctor.DetectThisDistro(), "quickemu")
                : $"{systems.Count} systems. Downloads are checked before the machine is made.";
            FilterCatalogue();
            OnPropertyChanged(nameof(CatalogueAvailable));
        });
    }

    partial void OnCatalogueSearchChanged(string value) => FilterCatalogue();

    private void FilterCatalogue()
    {
        var term = CatalogueSearch.Trim();
        Systems.Clear();
        foreach (var s in _allSystems.Where(s => term.Length == 0 || s.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) || s.Os.Contains(term, StringComparison.OrdinalIgnoreCase)))
            Systems.Add(s);
    }

    partial void OnSystemChanged(CatalogueSystem? value) => Entry = value?.Entries.FirstOrDefault();

    partial void OnEntryChanged(CatalogueEntry? value)
    {
        if (value is null) return;
        var os = value.Os switch
        {
            "windows" => new GuestConfig { Os = "windows", Release = value.Release },
            "windows-server" => new GuestConfig { Os = "windows-server", Release = value.Release },
            "macos" => new GuestConfig { Os = "macos", Release = value.Release },
            "freebsd" or "ghostbsd" or "netbsd" or "openbsd" or "haiku" or "freedos" or "reactos" or "kolibrios" or "solaris" => new GuestConfig { Os = value.Os, Release = value.Release },
            _ => new GuestConfig { Os = "linux", Release = "" },
        };
        Detected(os, $"{value.DisplayName} {value.Label}, downloaded and checked by VM Qontrol.");
        Name = $"{value.DisplayName} {value.Release}".Trim();
    }

    // ── Recommendations
    private void Recalculate()
    {
        if (_host is null) return;
        var os = Guest.OsInfoId is { } id ? _osinfo?.Find(id) : null;
        var r = Recommendations.For(Guest, os, _host, GuestLabel);
        MemoryGb = Math.Max(1, r.MemoryMiB.Value / 1024m);
        MemoryMinimumGb = Math.Max(0.5m, r.MemoryMiB.Minimum / 1024m);
        MemoryMaximumGb = Math.Max(MemoryGb, _host.MemoryGB * 1000 / 1024 * 3 / 4);
        MemoryReason = r.MemoryMiB.Reason;
        Cores = r.Cores.Value;
        CoresMaximum = Math.Max(1, _host.LogicalCpus);
        CoresReason = r.Cores.Reason;
        DiskGb = r.DiskGiB.Value;
        DiskMinimumGb = r.DiskGiB.Minimum;
        DiskReason = r.DiskGiB.Reason;
        var modern = Guest.Os is "windows" or "windows-server" or "linux" or "macos" or "freebsd";
        Uefi = modern || Guest.Os == "macos";
        Tpm = Guest.Os == "windows" && Guest.Release == "11";
        SecureBoot = Recommendations.SecureBoot(Guest, _host);
        FirmwareReason = Tpm
            ? SecureBoot
                ? "Windows 11 requires UEFI firmware with Secure Boot and a TPM; all three are set. Setup on current Windows 11 discs stops without Secure Boot."
                : "Windows 11 requires UEFI firmware, Secure Boot and a TPM. This computer has no Secure Boot firmware build, so current Windows 11 discs may refuse to install; the doctor names the package that adds it."
            : Uefi ? "UEFI is the modern firmware every current system expects." : "This system starts with the classic BIOS firmware.";
    }

    // ── Create
    private async Task CreateAsync()
    {
        Busy = true;
        NextCommand.NotifyCanExecuteChanged();
        if (Source == SourceKind.Template && Template is { } template)
        {
            await CreateFromTemplateAsync(template).ConfigureAwait(false);
            return;
        }

        var request = new NewMachineRequest
        {
            Name = Name.Trim(),
            LibraryRoot = LibraryRoot,
            Guest = Guest,
            InstallerIso = Source == SourceKind.Disc ? IsoPath : null,
            DriversIso = DriversIso,
            DiskGiB = (long)DiskGb,
            MemoryMiB = (long)(MemoryGb * 1024),
            Cores = (int)Cores,
            Firmware = Uefi ? FirmwareKind.Uefi : FirmwareKind.Bios,
            SecureBoot = SecureBoot,
            Tpm = Tpm,
            LinuxEasyInstall = EasyInstallShown && IsLinuxEasy
                ? new LinuxInstallChoices
                {
                    UserName = UserName.Trim(),
                    Password = Password,
                    HostName = LinuxEasyInstall.HostName(Name),
                    TimeZone = IanaTimeZone,
                    Locale = Locale.Replace('-', '_') + ".UTF-8",
                }
                : null,
            WindowsEasyInstall = EasyInstallShown && IsWindows
                ? new WindowsInstallChoices
                {
                    UserName = UserName.Trim(),
                    Password = Password,
                    ComputerName = WindowsAnswerFile.ComputerName(Name),
                    EditionIndex = Edition?.Index ?? 1,
                    EditionName = Edition?.Name ?? "",
                    ConsumerDisc = ConsumerDisc,
                    ProductKey = ProductKey.Trim(),
                    Locale = Locale,
                    TimeZone = TimeZone,
                }
                : null,
        };
        try
        {
            string dir;
            if (Source == SourceKind.Download && Entry is { } entry)
            {
                _download = new CancellationTokenSource();
                var job = new DownloadJob(entry, LibraryRoot, request.Name);
                var progress = new Progress<DownloadProgress>(p => Ui.Post(() =>
                {
                    ProgressKnown = p.ExpectedBytes is > 0;
                    ProgressValue = p.ExpectedBytes is { } e and > 0 ? Math.Min(100, p.Bytes * 100.0 / e) : 0;
                    ProgressText = $"{p.Status} {Formats.Bytes(p.Bytes)}{(p.ExpectedBytes is { } ex ? $" of {Formats.Bytes(ex)}" : "")}";
                }));
                var (machine, folder) = await Task.Run(() => job.RunAsync(progress, _download.Token)).ConfigureAwait(false);
                // The catalogue's machine, with the hardware chosen here.
                machine.MemoryMiB = request.MemoryMiB;
                machine.Cpu.Cores = request.Cores;
                if (machine.Disks.Count > 0) machine.Disks[0].SizeGiB = request.DiskGiB;
                MachineStore.Save(machine, Path.Combine(folder, MachineStore.FileName));
                dir = folder;
            }
            else
            {
                Ui.Post(() => ProgressText = "Creating the machine…");
                dir = (await Task.Run(() => MachineFactory.Create(request)).ConfigureAwait(false)).Directory;
            }

            Ui.Post(() => _ = _shell.FinishNewMachineAsync(dir, PowerOnAfter));
            Ui.Post(() => Closed?.Invoke());
        }
        catch (DownloadFailedException ex) when (Entry?.Os == "windows")
        {
            // Microsoft often refuses automated downloads; its own page works in a browser, and the wizard takes the file from there.
            var page = Entry.Release.StartsWith("10", StringComparison.Ordinal) ? "https://www.microsoft.com/software-download/windows10ISO" : "https://www.microsoft.com/software-download/windows11";
            var notice = new NoticeViewModel("Windows could not be downloaded automatically",
                [.. ex.Reasons, "Download the disc image from Microsoft's page in your browser, then choose \"Installer disc image\" here and pick the file."],
                confirm: "Open Microsoft's download page");
            Ui.Post(() =>
            {
                _shell.Show(notice);
                Source = SourceKind.Disc;
                Step = 0;
            });
            if (await notice.Answer.ConfigureAwait(false)) OpenInBrowser(page);
        }
        catch (DownloadFailedException ex)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("The download did not give a usable machine", ex.Reasons, isError: true)));
        }
        catch (OperationCanceledException)
        {
            Ui.Post(() => _shell.Note("The download was cancelled."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("The machine could not be created", [ex.Message], isError: true)));
        }
        finally
        {
            Ui.Post(() =>
            {
                Busy = false;
                ProgressText = "";
                NextCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private async Task CreateFromTemplateAsync(MachineViewModel template)
    {
        var name = Name.Trim();
        var linked = LinkedClone;
        try
        {
            var progress = new Progress<string>(p => Ui.Post(() => ProgressText = p));
            string dir;
            if (linked)
            {
                var result = await Task.Run(() => MachineLifecycle.LinkedCloneAsync(template.Machine, template.Entry.Directory, LibraryRoot, name, progress)).ConfigureAwait(false);
                dir = result.Directory;
                // The template now reads from a layer of its own on the shared base.
                Ui.Post(() => template.Reload(result.Original));
            }
            else
            {
                dir = (await Task.Run(() => MachineLifecycle.CloneAsync(template.Machine, template.Entry.Directory, LibraryRoot, name, progress)).ConfigureAwait(false)).Directory;
            }

            Ui.Post(() => _ = _shell.FinishNewMachineAsync(dir, PowerOnAfter));
            Ui.Post(() => Closed?.Invoke());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("The machine could not be made from the template", [ex.Message], isError: true)));
        }
        finally
        {
            Ui.Post(() =>
            {
                Busy = false;
                ProgressText = "";
                NextCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private static void OpenInBrowser(string url) => _ = Task.Run(() =>
    {
        try
        {
            using var _ = global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
        }
        catch (global::System.ComponentModel.Win32Exception)
        {
        }
    });

    private IReadOnlyList<DetailRow> BuildSummary()
    {
        if (IsFromTemplate && Template is { } t)
        {
            var m = t.Machine;
            return
            [
                new("Name", Name),
                new("Folder", FolderPreview),
                new("Template", t.Name),
                new("System", GuestLabel),
                new("Copy", LinkedClone
                    ? $"Linked: ready in seconds, shares {t.Name}'s disks read-only and keeps its own changes"
                    : $"Full: its own copy of {t.Name}'s disks, firmware settings and TPM"),
                new("Hardware", $"As the template: {(m.MemoryMiB is { } mib ? $"{mib / 1024m:0.#} GB memory" : "recommended memory")}, {(m.Cpu.Cores is { } c ? $"{c} processor{(c == 1 ? "" : "s")}" : "recommended processors")} (change it in Settings)"),
            ];
        }

        var rows = new List<DetailRow>
        {
            new("Name", Name),
            new("Folder", FolderPreview),
            new("System", GuestLabel),
            new("Installer", Source switch
            {
                SourceKind.Disc => Path.GetFileName(IsoPath) ?? "",
                SourceKind.Download => Entry is { } e ? $"Download {e.DisplayName} {e.Label}" : "",
                _ => "None yet (attach one later in Settings)",
            }),
        };
        if (EasyInstallShown && IsWindows) rows.Add(new("Easy Install", $"{Edition?.DisplayName ?? "Windows"} for {UserName}{(Password.Length == 0 ? " (no password)" : "")}{(DriversIso is null ? "" : ", with VirtIO drivers")}"));
        if (EasyInstallShown && IsLinuxEasy) rows.Add(new("Easy Install", $"For {UserName}, host name {LinuxEasyInstall.HostName(Name)}, time zone {IanaTimeZone}"));
        rows.Add(new("Memory", $"{MemoryGb} GB"));
        rows.Add(new("Processors", $"{Cores}"));
        rows.Add(new("Hard disk", $"{DiskGb} GB, grows as it is used"));
        rows.Add(new("Firmware", Uefi ? SecureBoot ? "UEFI with Secure Boot" : "UEFI" : "BIOS"));
        if (Tpm) rows.Add(new("TPM", "Version 2.0"));
        return rows;
    }
}

/// <summary>What reading an installer disc found.</summary>
internal sealed record DiscDetection(bool Problem, GuestConfig? Guest, string Text, IReadOnlyList<WindowsEdition> Editions, string Name, LinuxInstaller Linux = LinuxInstaller.None);

/// <summary>A step in the wizard's rail.</summary>
public sealed record StepItem(string Title, bool IsCurrent, bool IsDone);

/// <summary>A value with the words a person reads for it.</summary>
public sealed record NamedChoice(string Value, string Label)
{
    public override string ToString() => Label;
}
