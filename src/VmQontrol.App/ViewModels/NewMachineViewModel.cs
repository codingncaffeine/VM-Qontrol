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

    /// <summary>A distribution's cloud image: a ready machine in seconds, no installer (D39).</summary>
    Cloud,
}

/// <summary>A guest system the person can pick by hand when nothing was detected (or to correct a detection).</summary>
public sealed record GuestChoice(string Label, string Os, string Release)
{
    public override string ToString() => Label;
}

/// <summary>
/// The New Machine wizard. Every page answers one question, says why it matters, and starts on the recommended answer;
/// detection fills in what can be known from the disc so the person is never asked it. Slow work (reading a disc,
/// loading the catalog, downloading, creating) runs on workers.
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
        // Preferences → New machines.
        PowerOnAfter = shell.Settings.NewMachinePowerOn;
        LeanWindows = shell.Settings.NewMachineLean;
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

    /// <summary>
    /// Easy Install for a machine that exists (its disk still empty): the same disc and Easy Install pages, then
    /// <see cref="MachineFactory.AddEasyInstall"/> instead of a new machine. Name, folder and hardware are the machine's own.
    /// </summary>
    public NewMachineViewModel(ShellViewModel shell, MachineConfig machine, string directory)
        : this(shell)
    {
        _existing = (machine, directory);
        // Asked from the machine's own install card: installing now is the point, whatever new machines do.
        PowerOnAfter = true;
        Name = machine.Name;
        LibraryRoot = Path.GetDirectoryName(directory) ?? LibraryRoot;
        if (machine.Media.FirstOrDefault(x => x.Role == MediaRole.Installer) is { } installer)
            _ = UseIsoAsync(Path.Combine(directory, installer.File));
    }

    private readonly (MachineConfig Machine, string Directory)? _existing;

    public bool ForExisting => _existing is not null;

    public string PowerOnAfterText => ForExisting ? "Power the machine on now and let it install" : "Power on the machine when it is created";

    public string Step0Title => ForExisting ? "The installer disc" : "How will you install the operating system?";

    public string WizardTitle => _existing is { } e ? $"Easy Install for {e.Machine.Name}" : "New virtual machine";

    public string WizardSubtitle => ForExisting
        ? "VM Qontrol answers the installer's questions from what you enter here. The machine's disk, hardware and folder stay as they are."
        : "Each step starts on the recommended answer. Change only what you want to.";

    /// <summary>Under the disc's description when Easy Install cannot answer this disc's installer.</summary>
    public string ExistingNote => !ForExisting || Detecting || EasyInstallPossible ? ""
        : "Easy Install does not know this disc's installer, so it cannot install it for you. Choose another disc image, or power the machine on and answer the installer yourself.";

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

    public string NextText => IsLast && ForExisting ? "Set up Easy Install" : IsLast ? (Source is SourceKind.Download or SourceKind.Cloud ? "Download and create" : "Create machine") : "Continue";

    /// <summary>A machine made from a template takes its system and hardware from it: only the name and the kind of copy are asked.</summary>
    private bool Skipped(int step) => step switch
    {
        1 or 3 or 4 when ForExisting => true,
        1 => IsFromTemplate || IsCloud,
        4 => IsFromTemplate,
        2 => !EasyInstallPossible,
        _ => false,
    };

    /// <summary>The step rail: each step's title and whether it is the current one.</summary>
    public IReadOnlyList<StepItem> Steps => StepTitles.Select((t, i) => (Item: new StepItem(IsCloud && i == 2 ? "Account" : t, i == Step, i < Step), Index: i))
        .Where(s => !ForExisting || !Skipped(s.Index)).Select(s => s.Item).ToList();

    partial void OnStepChanged(int value) => OnPropertyChanged(nameof(Steps));

    // ── Installer
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisc), nameof(IsDownload), nameof(IsLater), nameof(IsFromTemplate), nameof(IsCloud), nameof(ShowEasyInstallSwitch), nameof(Steps), nameof(NextText), nameof(EasyInstallPossible),
        nameof(EasyInstallShown), nameof(PasswordRequired), nameof(PasswordNote), nameof(EasyInstallTitle), nameof(EasyInstallExplain))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private SourceKind _source = SourceKind.Disc;

    public bool IsCloud => Source == SourceKind.Cloud;

    /// <summary>The Easy Install switch: a cloud image has no installer to answer, so it has none.</summary>
    public bool ShowEasyInstallSwitch => !ForExisting && !IsCloud;

    public IReadOnlyList<CloudImage> CloudImageList { get; } = CloudImages.All;

    /// <summary>The cloud image picked; picking one sets the system and the hardware a server machine needs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private CloudImage? _cloudImage;

    /// <summary>The public keys in ~/.ssh, read on a worker when the cloud card opens; they sign in to the new machine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordRequired), nameof(PasswordNote), nameof(CloudKeysText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private IReadOnlyList<string> _cloudKeys = [];

    public string CloudKeysText => CloudKeys.Count switch
    {
        0 => "None found in ~/.ssh: you sign in with the password.",
        1 => $"Your key from ~/.ssh ({KeyComment(CloudKeys[0])}) signs in, with no password asked.",
        var n => $"Your {n} keys from ~/.ssh sign in, with no password asked.",
    };

    private static string KeyComment(string key) => key.Split(' ', 3) is [_, _, var comment] ? comment : key.Split(' ')[0];

    partial void OnCloudImageChanged(CloudImage? value)
    {
        if (value is null) return;
        Detected(new GuestConfig { Os = "linux", OsInfoId = value.OsInfoId }, $"{value.DisplayName}, from its cloud image.");
        // A server image needs far less than a desktop installer: what multipass and Proxmox start with.
        MemoryGb = 2;
        Cores = Math.Min(2, CoresMaximum);
        DiskGb = 20;
        DiskMinimumGb = Math.Min(DiskMinimumGb, 10);
        if (Name == "New machine" || Name.Length == 0 || CloudImageList.Any(i => Name == i.DisplayName)) Name = value.DisplayName;
    }

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
    [NotifyPropertyChangedFor(nameof(IsoOk))]
    private string? _isoPath;

    [ObservableProperty]
    private string _isoSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsoOk))]
    private bool _isoProblem;

    /// <summary>A disc was chosen and read without a problem (the tick beside its summary).</summary>
    public bool IsoOk => IsoPath is not null && !IsoProblem && !Detecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsoOk))]
    private bool _detecting;

    [ObservableProperty]
    private IReadOnlyList<WindowsEdition> _editions = [];

    [ObservableProperty]
    private WindowsEdition? _edition;

    public bool HasEditions => Editions.Count > 1;

    partial void OnEditionsChanged(IReadOnlyList<WindowsEdition> value) => OnPropertyChanged(nameof(HasEditions));

    // Download catalog
    public ObservableCollection<CatalogSystem> Systems { get; } = [];

    private IReadOnlyList<CatalogSystem> _allSystems = [];

    [ObservableProperty]
    private string _catalogSearch = "";

    [ObservableProperty]
    private string _catalogNote = "";

    [ObservableProperty]
    private CatalogSystem? _system;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private CatalogEntry? _entry;

    public bool CatalogAvailable => _allSystems.Count > 0;

    /// <summary>Windows from Microsoft's own catalog (not quickget): its editions, and the line that says what will be downloaded.</summary>
    public bool IsMicrosoftWindows => Source == SourceKind.Download && Entry?.FromMicrosoft == true;

    private IReadOnlyList<WindowsImageFile> _microsoftFiles = [];

    [ObservableProperty]
    private string _windowsDownloadNote = "";

    /// <summary>The edition chosen, with the file that carries it in the language that suits <see cref="Locale"/>.</summary>
    private WindowsDownloadEdition? MicrosoftEdition =>
        Edition is { } e ? WindowsDownload.Editions(_microsoftFiles, Locale).FirstOrDefault(m => m.EditionId == e.EditionId) : null;

    // ── System
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindows), nameof(GuestLabel), nameof(IsWindowsEasy), nameof(IsLinuxEasy), nameof(EasyInstallTitle),
        nameof(EasyInstallExplain), nameof(PasswordRequired), nameof(PasswordNote), nameof(NeedsOwnKey), nameof(KeyNote))]
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

    public bool EasyInstallShown => EasyInstallPossible && (EasyInstall || IsCloud);

    public bool EasyInstallPossible => IsCloud || (Source == SourceKind.Disc && IsoPath is not null && (IsWindows || LinuxInstallerFound != LinuxInstaller.None))
                                       || (IsMicrosoftWindows && Editions.Count > 0);

    /// <summary>The Linux installer on the disc, when Easy Install knows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EasyInstallPossible), nameof(EasyInstallShown), nameof(IsLinuxEasy), nameof(EasyInstallTitle), nameof(EasyInstallExplain),
        nameof(PasswordRequired), nameof(PasswordNote), nameof(ExistingNote))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private LinuxInstaller _linuxInstallerFound;

    public bool IsLinuxEasy => !IsWindows && LinuxInstallerFound != LinuxInstaller.None;

    public bool IsWindowsEasy => IsWindows;

    public string EasyInstallTitle => IsCloud ? "Your account in the machine" : IsWindows ? "Install Windows for you?" : "Install it for you?";

    public string EasyInstallExplain => IsCloud
        ? "There is no installer: the machine starts ready, with this account, SSH, and the guest tools added on first start."
        : IsWindows
        ? "Windows Setup normally asks a dozen questions. VM Qontrol answers them from what you enter here, then you come back to a desktop."
        : LinuxInstallerFound switch
        {
            LinuxInstaller.Ubuntu => "Ubuntu's installer is answered from what you enter here (its autoinstall), with the disk used whole, SSH, the guest agent and the clipboard agent installed.",
            LinuxInstaller.Anaconda => "The installer (Anaconda) is answered with a kickstart made from what you enter here: the disk used whole, your account, the guest agent and the clipboard agent.",
            LinuxInstaller.Debian => "Debian's installer is answered with a preseed made from what you enter here: the disk used whole, your account, SSH, the guest agent and the clipboard agent.",
            _ => "",
        };

    /// <summary>Linux needs a password: its installers make no account without one.</summary>
    public bool PasswordRequired => IsLinuxEasy && EasyInstall || IsCloud && CloudKeys.Count == 0;

    public string PasswordNote => IsCloud
        ? CloudKeys.Count > 0
            ? "Optional with your SSH key. With one, the machine's screen asks for it and so does sudo; without, the screen signs you in by itself and sudo asks for nothing."
            : "Required: no SSH key was found, so the password is how you sign in. It is stored hashed in the machine's cloud-init disc."
        : PasswordRequired
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

    /// <summary>A consumer disc (Home, Pro…) takes retail setup keys; a volume-license disc takes KMS client setup keys.</summary>
    public bool ConsumerDisc => IsMicrosoftWindows ? MicrosoftEdition?.File.Business != true : !Editions.Any(e => e.Name.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)) || Editions.Any(e => e.Name.Contains("Home", StringComparison.OrdinalIgnoreCase));

    public string KeyNote => NeedsOwnKey
        ? $"{Edition?.DisplayName ?? "This edition"} needs your product key to install without questions. Enter it here, or choose a Pro edition on the first page."
        : "Leave empty to install now and activate later with your own license.";

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
        .OrderBy(z => z.Id == TimeZoneInfo.Local.Id ? 0 : 1) // this computer's own city names its Windows zone
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

    /// <summary>
    /// Windows Easy Install with the VirtIO drivers and guest tools (D31): on by default. Without a disc chosen, the one
    /// downloaded before is used, or it is downloaded when the machine is made.
    /// </summary>
    [ObservableProperty]
    private bool _installDrivers = true;

    public string GuestToolsSize => GuestToolsDownload.ApproximateSize;

    public string DriversNote => DriversIso is { } own
        ? $"Using {Path.GetFileName(own)}."
        : _shell.HasGuestTools
            ? "The guest tools disc downloaded before is used."
            : $"VM Qontrol downloads the VirtIO driver disc once ({GuestToolsDownload.ApproximateSize}, from the virtio-win project) when the machine is made.";

    partial void OnDriversIsoChanged(string? value) => OnPropertyChanged(nameof(DriversNote));

    /// <summary>Before making the machine: the driver disc to use (chosen, found or downloaded), or <c>false</c> when the download failed or was stopped.</summary>
    private async Task<bool> ResolveDriversAsync()
    {
        if (!(EasyInstallShown && IsWindows && InstallDrivers) || DriversIso is not null) return true;
        var progress = new Progress<DownloadProgress>(p => Ui.Post(() =>
        {
            ProgressKnown = p.ExpectedBytes is > 0;
            ProgressValue = p.ExpectedBytes is { } e and > 0 ? Math.Min(100, p.Bytes * 100.0 / e) : 0;
            ProgressText = p.ExpectedBytes is { } size and > 0 ? $"{p.Status} {Formats.Bytes(p.Bytes)} of {Formats.Bytes(size)}" : p.Status;
        }));
        Ui.Post(() => ProgressText = "Getting the guest tools…");
        var path = await _shell.GetGuestToolsAsync(progress).ConfigureAwait(false);
        if (path is null)
        {
            Ui.Post(() =>
            {
                Busy = false;
                NextCommand.NotifyCanExecuteChanged();
            });
            return false;
        }

        // Set on the UI thread and waited for: BuildRequest reads it on this worker right after.
        await Ui.InvokeAsync(() => DriversIso = path).ConfigureAwait(false);
        return true;
    }

    /// <summary>"Lean Windows": on by default, as people who download Windows into a machine rarely want its promotions.</summary>
    [ObservableProperty]
    private bool _leanWindows = true;

    public string LeanNote { get; } = "Leaves out Xbox, news, weather, Teams, Outlook, Clipchamp, Copilot, Solitaire, ads and suggested apps. "
        + "Store, Edge and the everyday apps stay; anything removed is one click away in the Store.";

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
        0 when ForExisting => IsoPath is not null && !IsoProblem && !Detecting && EasyInstallPossible,
        0 => Source switch { SourceKind.Disc => IsoPath is not null && !IsoProblem, SourceKind.Download => Entry is not null && (!Entry.FromMicrosoft || Editions.Count > 0), SourceKind.Template => Template is not null, SourceKind.Cloud => CloudImage is not null, _ => true },
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
        if (kind == SourceKind.Download && _allSystems.Count == 0) _ = LoadCatalogAsync();
        // Editions belong to the source on show: the disc's, or Microsoft's for a Windows download.
        if (kind == SourceKind.Download && Entry?.FromMicrosoft == true) ShowMicrosoftEditions();
        else if (kind == SourceKind.Download) Editions = [];
        if (kind == SourceKind.Disc && IsoPath is { } iso) _ = UseIsoAsync(iso);
        OnPropertyChanged(nameof(IsMicrosoftWindows));
        if (kind == SourceKind.Later) Detected(new GuestConfig { Os = "linux" }, "Choose the system you will install on the next page.");
        if (kind == SourceKind.Template && Template is null && Templates.Count == 1) Template = Templates[0];
        if (kind == SourceKind.Cloud)
        {
            CloudImage ??= CloudImageList[0];
            _ = Task.Run(() =>
            {
                var keys = CloudImages.LocalPublicKeys();
                Ui.Post(() => CloudKeys = keys);
            });
        }
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
            // A damaged or deliberately odd disc image must end in words, never a wizard stuck on "Reading the disc…".
            try
            {
                return Detect(path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                VmQontrol.Core.Log.Warn($"Reading {path} failed: {ex}");
                return new DiscDetection(true, null, $"This disc image could not be read ({ex.Message}). It may be damaged; download it again.", [], "");
            }
        }).ConfigureAwait(false);
        Ui.Post(() => FinishDetection(result));
    }

    private DiscDetection Detect(string path)
    {
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

            return new DiscDetection(false, new GuestConfig { Os = "linux" }, $"A disc labeled \"{volume.VolumeId}\". Its system is not known; choose it on the next page.", [], volume.VolumeId, linux);
        }
    }

    private void FinishDetection(DiscDetection result)
    {
        {
            Detecting = false;
            IsoProblem = result.Problem;
            IsoSummary = result.Text;
            Editions = result.Editions;
            Edition = result.Editions.FirstOrDefault(e => e.DisplayName.EndsWith(" Pro", StringComparison.Ordinal)) ?? result.Editions.FirstOrDefault();
            if (result.Guest is { } g) Detected(g, result.Text);
            LinuxInstallerFound = result.Linux;
            OnPropertyChanged(nameof(ExistingNote));
            OnPropertyChanged(nameof(Steps));
            if (result.Name.Length > 0 && (Name == "New machine" || Name.Length == 0)) Name = Edition?.DisplayName ?? result.Name;
            OnPropertyChanged(nameof(EasyInstallPossible));
            OnPropertyChanged(nameof(EasyInstallShown));
            NextCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnEasyInstallChanged(bool value)
    {
        OnPropertyChanged(nameof(PasswordRequired));
        OnPropertyChanged(nameof(PasswordNote));
        NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnEditionChanged(WindowsEdition? value)
    {
        OnPropertyChanged(nameof(ConsumerDisc));
        if (IsMicrosoftWindows) UpdateWindowsDownloadNote();
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

    // ── Catalog
    private async Task LoadCatalogAsync()
    {
        CatalogNote = "Loading the catalog…";
        IReadOnlyList<CatalogSystem> quickget;
        try
        {
            quickget = await Catalog.LoadAsync(_shell.Paths).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or global::System.Text.Json.JsonException)
        {
            // The list of other systems failed; Windows from Microsoft still works, and the note says what happened.
            VmQontrol.Core.Log.Warn($"The download catalog failed: {ex.Message}");
            quickget = [];
        }

        // Windows comes from Microsoft's own catalog (the Media Creation Tool's), not quickget's scripted web-page download.
        var icon = quickget.FirstOrDefault(s => s.Os == "windows")?.IconUrl ?? "";
        var windows = new CatalogSystem("windows", "Windows", icon,
            [new CatalogEntry("Windows", "windows", "11", "", icon, FromMicrosoft: true), new CatalogEntry("Windows", "windows", "10", "", icon, FromMicrosoft: true)]);
        var systems = quickget.Where(s => s.Os != "windows").OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).Prepend(windows).ToList();
        Ui.Post(() =>
        {
            _allSystems = systems;
            CatalogNote = quickget.Count == 0
                ? "Windows comes straight from Microsoft. Other systems need quickget (part of quickemu): " + Doctor.InstallCommand(Doctor.DetectThisDistro(), "quickemu")
                : $"{systems.Count} systems. Downloads are checked before the machine is made.";
            FilterCatalog();
            OnPropertyChanged(nameof(CatalogAvailable));
        });
    }

    partial void OnCatalogSearchChanged(string value) => FilterCatalog();

    private void FilterCatalog()
    {
        var term = CatalogSearch.Trim();
        Systems.Clear();
        foreach (var s in _allSystems.Where(s => term.Length == 0 || s.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) || s.Os.Contains(term, StringComparison.OrdinalIgnoreCase)))
            Systems.Add(s);
        // Keep the choice when the list is filtered again (a filter that hides it leaves it chosen).
        if (System is { } chosen && !Systems.Contains(chosen) && term.Length == 0) System = null;
    }

    partial void OnSystemChanged(CatalogSystem? value) => Entry = value?.Entries.FirstOrDefault();

    partial void OnEntryChanged(CatalogEntry? value)
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
        _microsoftFiles = [];
        Editions = [];
        WindowsDownloadNote = "";
        if (value.FromMicrosoft) _ = LoadMicrosoftEditionsAsync(value.Release);
        OnPropertyChanged(nameof(IsMicrosoftWindows));
        OnPropertyChanged(nameof(EasyInstallPossible));
        OnPropertyChanged(nameof(EasyInstallShown));
    }

    private async Task LoadMicrosoftEditionsAsync(string release)
    {
        Ui.Post(() => WindowsDownloadNote = $"Reading Microsoft's Windows {release} catalog…");
        IReadOnlyList<WindowsImageFile> files;
        try
        {
            files = await WindowsDownload.LoadAsync(_shell.Paths, release).ConfigureAwait(false);
        }
        catch (DownloadFailedException ex)
        {
            Ui.Post(() => WindowsDownloadNote = string.Join(" ", ex.Reasons));
            return;
        }

        Ui.Post(() =>
        {
            if (Entry is not { FromMicrosoft: true } entry || entry.Release != release) return; // the person moved on
            _microsoftFiles = files;
            ShowMicrosoftEditions();
        });
    }

    /// <summary>Microsoft's editions as the wizard's edition list (each becomes a one-edition disc, so its index is 1).</summary>
    private void ShowMicrosoftEditions()
    {
        var offered = WindowsDownload.Editions(_microsoftFiles, Locale);
        Editions = offered.Select(e => new WindowsEdition(1, e.Name, e.Name, 0, e.EditionId)).ToList();
        Edition = Editions.FirstOrDefault(e => e.EditionId == "Professional") ?? Editions.FirstOrDefault();
        UpdateWindowsDownloadNote();
        OnPropertyChanged(nameof(EasyInstallPossible));
        OnPropertyChanged(nameof(EasyInstallShown));
        OnPropertyChanged(nameof(ConsumerDisc));
        NextCommand.NotifyCanExecuteChanged();
    }

    private void UpdateWindowsDownloadNote()
    {
        if (MicrosoftEdition is not { } m)
        {
            WindowsDownloadNote = _microsoftFiles.Count == 0 ? WindowsDownloadNote : "Microsoft's catalog has no Windows for this computer's architecture.";
            return;
        }

        var language = CultureInfo.GetCultureInfo(m.File.Language).DisplayName;
        var tools = WindowsDownload.MissingTools();
        WindowsDownloadNote = $"From Microsoft: build {m.File.Build}, {language}, {Formats.Bytes(m.File.Size)}, checked against Microsoft's checksum, then made into an installer disc kept for your next Windows machine. Windows Update brings it up to date after installing."
                              + (tools.Count > 0 ? $" Needs {string.Join(" and ", tools)} first: {Doctor.InstallCommand(Doctor.DetectThisDistro(), "windows-download")}" : "");
    }

    partial void OnLocaleChanged(string value)
    {
        if (IsMicrosoftWindows) UpdateWindowsDownloadNote();
        OnPropertyChanged(nameof(LocaleChoice));
    }

    // ── Recommendations
    private void Recalculate()
    {
        if (_host is null) return;
        var os = Guest.OsInfoId is { } id ? _osinfo?.Find(id) : null;
        var r = Recommendations.For(Guest, os, _host, GuestLabel, _shell.Settings.Size);
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

        if (!await ResolveDriversAsync().ConfigureAwait(false)) return;

        if (_existing is { } existing)
        {
            await AddEasyInstallAsync(existing.Machine, existing.Directory).ConfigureAwait(false);
            return;
        }

        var request = BuildRequest();
        try
        {
            string dir;
            var progress = new Progress<DownloadProgress>(p => Ui.Post(() =>
            {
                ProgressKnown = p.ExpectedBytes is > 0;
                ProgressValue = p.ExpectedBytes is { } e and > 0 ? Math.Min(100, p.Bytes * 100.0 / e) : 0;
                ProgressText = $"{p.Status} {Formats.Bytes(p.Bytes)}{(p.ExpectedBytes is { } ex ? $" of {Formats.Bytes(ex)}" : "")}";
            }));
            if (IsMicrosoftWindows && MicrosoftEdition is { } windows)
            {
                // Microsoft's file becomes an installer disc; from there the machine is made exactly as from a disc the person picked.
                _download = new CancellationTokenSource();
                var token = _download.Token;
                var iso = await Task.Run(() => WindowsDownload.BuildDiscAsync(windows, LibraryRoot, progress, token)).ConfigureAwait(false);
                request = request with
                {
                    InstallerIso = iso,
                    WindowsEasyInstall = request.WindowsEasyInstall is { } w
                        ? w with { EditionIndex = 1, EditionName = windows.Name, UiLanguage = CultureInfo.GetCultureInfo(windows.File.Language).Name }
                        : null,
                };
                Ui.Post(() => ProgressText = "Creating the machine…");
                dir = (await Task.Run(() => MachineFactory.Create(request)).ConfigureAwait(false)).Directory;
            }
            else if (Source == SourceKind.Cloud && CloudImage is { } cloud)
            {
                _download = new CancellationTokenSource();
                var token = _download.Token;
                var choices = new CloudChoices
                {
                    Name = request.Name, UserName = UserName.Trim(), Password = Password, SshKeys = CloudKeys,
                    DiskGiB = request.DiskGiB, MemoryMiB = request.MemoryMiB ?? 2048, Cores = request.Cores ?? 2,
                };
                var root = LibraryRoot;
                dir = await Task.Run(async () =>
                {
                    var resolved = await CloudImages.ResolveAsync(cloud, token).ConfigureAwait(false);
                    var file = await CloudImages.GetAsync(_shell.Paths, resolved, progress, token).ConfigureAwait(false);
                    Ui.Post(() => ProgressText = "Making the machine from the image…");
                    return (await CloudImages.CreateAsync(root, cloud, file, choices, token).ConfigureAwait(false)).Directory;
                }).ConfigureAwait(false);
            }
            else if (Source == SourceKind.Download && Entry is { } entry)
            {
                _download = new CancellationTokenSource();
                var job = new DownloadJob(entry, LibraryRoot, request.Name);
                var (machine, folder) = await Task.Run(() => job.RunAsync(progress, _download.Token)).ConfigureAwait(false);
                // The catalog's machine, with the hardware chosen here.
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
            Ui.Post(() => _shell.Note("The download was canceled."));
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

    private NewMachineRequest BuildRequest() => new()
    {
        Name = Name.Trim(),
        LibraryRoot = LibraryRoot,
        Guest = Guest,
        InstallerIso = Source == SourceKind.Disc ? IsoPath : null,
        DriversIso = IsWindows && EasyInstallShown && !InstallDrivers ? null : DriversIso,
        DiskGiB = (long)DiskGb,
        MemoryMiB = (long)(MemoryGb * 1024),
        Cores = (int)Cores,
        Firmware = Uefi ? FirmwareKind.Uefi : FirmwareKind.Bios,
        SecureBoot = SecureBoot,
        Tpm = Tpm,
        SystemName = Guest.Os == "linux" ? null : GuestLabel,
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
                Lean = LeanWindows,
            }
            : null,
    };

    private async Task AddEasyInstallAsync(MachineConfig machine, string directory)
    {
        var request = BuildRequest();
        Ui.Post(() => ProgressText = "Writing the answer disc…");
        try
        {
            var updated = await Task.Run(() => MachineFactory.AddEasyInstall(machine, directory, request)).ConfigureAwait(false);
            VmQontrol.Core.Log.Info($"{machine.Name}: Easy Install set up ({updated.EasyInstall?.System}).");
            Ui.Post(() =>
            {
                _shell.FinishEasyInstallSetup(directory, updated, PowerOnAfter);
                Closed?.Invoke();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ui.Post(() =>
            {
                Busy = false;
                NextCommand.NotifyCanExecuteChanged();
                _shell.Show(new NoticeViewModel("Easy Install could not be set up", [ex.Message], isError: true));
            });
        }
    }

    private IReadOnlyList<DetailRow> BuildSummary()
    {
        if (_existing is { } existingMachine)
        {
            return
            [
                new("Machine", existingMachine.Machine.Name),
                new("System", IsWindows ? Edition?.DisplayName ?? GuestLabel : GuestLabel),
                new("Installer", Path.GetFileName(IsoPath) ?? ""),
                IsWindows
                    ? new("Easy Install", $"{Edition?.DisplayName ?? "Windows"} for {UserName}{(Password.Length == 0 ? " (no password)" : "")}{(LeanWindows ? ", Lean" : "")}")
                    : new("Easy Install", $"For {UserName}, host name {LinuxEasyInstall.HostName(Name)}, time zone {IanaTimeZone}"),
                new("Hardware", "As the machine has it now (change it in Settings)"),
            ];
        }

        if (IsFromTemplate && Template is { } t)
        {
            var m = t.Machine;
            return
            [
                new("Name", Name),
                new("Folder", FolderPreview),
                new("Template", t.Name),
                new("System", IsCloud && CloudImage is { } img ? img.DisplayName : GuestLabel),
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
                SourceKind.Cloud => CloudImage is { } ci ? $"None: {ci.DisplayName}'s cloud image, downloaded and checked" : "",
                _ => "None yet (attach one later in Settings)",
            }),
        };
        if (EasyInstallShown && IsWindows) rows.Add(new("Easy Install", $"{Edition?.DisplayName ?? "Windows"} for {UserName}{(Password.Length == 0 ? " (no password)" : "")}{(InstallDrivers ? ", with the VirtIO drivers and guest tools" : "")}"));
        if (IsCloud) rows.Add(new("Account", $"{LinuxEasyInstall.AccountName(UserName)}{(CloudKeys.Count > 0 ? ", signs in with your SSH key" : "")}{(Password.Length > 0 ? ", with a password" : "")}; the guest tools are added on first start"));
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
