using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;

namespace VmQontrol.App.ViewModels;

/// <summary>A pinning choice: none, one of this computer's cache groups (the recommended one first), or a list typed by hand.</summary>
public sealed record PinChoice(string Title, string Detail, IReadOnlyList<int>? Cpus, IReadOnlyList<int>? Emulator, bool Custom = false)
{
    public override string ToString() => Title;
}

/// <summary>A PCI device in the passthrough list: ticked to hand it over, with what it does for this computer.</summary>
public sealed partial class PciRow(PciDevice device, bool chosen, IReadOnlyList<PciDevice> group) : ObservableObject
{
    public PciDevice Device { get; } = device;

    public string Name => Device.Name;

    public string Detail => $"{Device.Address} · {Device.Kind} · {(Device.Driver is { } d ? $"driver {d}" : "no driver")} · group {Device.Group ?? "none"}"
                            + (group.Count > 1 ? $" (with {string.Join(", ", group.Where(g => g.Address != Device.Address).Select(g => g.Address))})" : "");

    public string? Warning => Device.Group is null ? "No IOMMU group: this computer's IOMMU is off, so nothing can be handed over."
        : Device.HostUse is { } use ? $"This computer needs it: {use}."
        : Device.GroupUse is { } group ? $"Cannot go alone: {group}."
        : null;

    public bool HasWarning => Warning is not null;

    private readonly bool _savedChosen = chosen;

    /// <summary>A device this computer needs is not offered (one already chosen stays untickable-free, to be taken out).</summary>
    public bool CanChoose => Device.Group is not null && (Device.Blocked is null || _savedChosen);

    public string State => Device.BoundToVfio ? "Ready (vfio-pci)" : Device.HostUse is not null ? "In use here" : Device.GroupUse is not null ? "Group in use" : "On this computer";

    public bool Ready => Device.BoundToVfio;

    [ObservableProperty]
    private bool _chosen = chosen;
}

/// <summary>
/// Settings → Performance: pinning by cache group, I/O threads, huge pages with the pool's state and a Reserve button,
/// Looking Glass's shared memory sized from the resolution, and PCI devices by IOMMU group. Every row says what this
/// computer must have and whether it has it.
/// </summary>
public sealed partial class PerformanceSettingsViewModel : ObservableObject
{
    private readonly PerformanceConfig _original;
    private readonly Func<int> _guestCores;
    private readonly Func<long> _memoryMiB;
    private readonly Func<(int W, int H)> _resolution;

    public PerformanceSettingsViewModel(PerformanceConfig original, Func<int> guestCores, Func<long> memoryMiB, Func<(int W, int H)> resolution)
    {
        _original = original;
        _guestCores = guestCores;
        _memoryMiB = memoryMiB;
        _resolution = resolution;
        _ioThreads = original.IoThreads;
        _hugePages = HugeChoices.FirstOrDefault(c => c.Value == original.HugePages) ?? HugeChoices[0];
        _lookingGlass = original.LookingGlassMiB is > 0;
        _ = Task.Run(Load);
    }

    // ── Pinning

    public ObservableCollection<PinChoice> PinChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinDetail), nameof(IsCustomPin))]
    private PinChoice? _pin;

    public string PinDetail => Pin?.Detail ?? "";

    public bool IsCustomPin => Pin?.Custom == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomError), nameof(HasCustomError))]
    private string _customCpus = "";

    [ObservableProperty]
    private string _customEmulator = "";

    public string? CustomError => !IsCustomPin || CpuTopology.ParseList(CustomCpus).Count > 0 ? null : "Processor numbers, such as 2-5 or 2,3,18,19.";

    public bool HasCustomError => CustomError is not null;

    // ── I/O threads

    [ObservableProperty] private bool _ioThreads;

    // ── Huge pages

    public IReadOnlyList<Option> HugeChoices { get; } = [new(null, "Off: ordinary memory"), new("2M", "2 MB pages"), new("1G", "1 GB pages")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PoolText), nameof(NeedsPages), nameof(ReserveText))]
    private Option _hugePages;

    private IReadOnlyList<HugePageSize> _pool = [];

    private HugePageSize? Size => _pool.FirstOrDefault(s => s.Label == HugePages.Value);

    public string PoolText => HugePages.Value is null ? "The machine's memory comes from ordinary memory. Huge pages make memory faster for games and big databases, but stay reserved while unused."
        : Size is not { } s ? "This computer has no pool of this size."
        : $"Pool of {s.Label} pages: {s.Total} reserved, {s.Free} free. This machine needs {s.PagesFor(_memoryMiB())}" + (s.Free >= s.PagesFor(_memoryMiB()) ? ": enough." : ", so it would not start yet.");

    public bool NeedsPages => Size is { } s && s.Free < s.PagesFor(_memoryMiB());

    public string ReserveText => Size is { } s ? $"Reserve {s.Total + s.PagesFor(_memoryMiB()) - s.Free} pages…" : "Reserve…";

    [ObservableProperty] private string _poolNote = "";

    [RelayCommand]
    private async Task ReserveAsync()
    {
        if (Size is not { } s) return;
        var want = s.Total + s.PagesFor(_memoryMiB()) - s.Free;
        Ui.Post(() => PoolNote = "Asking for the password to reserve the pages…");
        var (now, problem) = await HugePagePool.ReserveAsync(s, want).ConfigureAwait(false);
        _pool = HugePagePool.Sizes();
        Ui.Post(() =>
        {
            PoolNote = problem is null ? $"{now} pages are reserved until this computer restarts." : $"Not all reserved: {problem}.";
            OnPropertyChanged(nameof(PoolText));
            OnPropertyChanged(nameof(NeedsPages));
            OnPropertyChanged(nameof(ReserveText));
        });
    }

    // ── Looking Glass

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LookingGlassText))]
    private bool _lookingGlass;

    /// <summary>Looking Glass's formula: width × height × 4 bytes × 2 frames + 10 MiB, rounded up to a power of two.</summary>
    public int LookingGlassMiB
    {
        get
        {
            var (w, h) = _resolution();
            var need = (long)w * h * 4 * 2 / (1024 * 1024) + 10;
            var size = 16;
            while (size < need) size *= 2;
            return size;
        }
    }

    public string LookingGlassText
    {
        get
        {
            var (w, h) = _resolution();
            var client = FindOnPath("looking-glass-client") is not null ? "The Looking Glass client is installed." : "The Looking Glass client is not installed on this computer yet (looking-glass-client).";
            return $"{LookingGlassMiB} MiB at {QemuCommandBuilder.LookingGlassFile}, enough for {w} × {h}. The guest runs the Looking Glass host program and a passed-through graphics card. {client}";
        }
    }

    // ── PCI

    public ObservableCollection<PciRow> PciDevices { get; } = [];

    public bool NoIommu { get; private set; }

    [ObservableProperty]
    private bool _showAllPci;

    public string PciSummary { get; private set; } = "Reading this computer's devices…";

    private IReadOnlyList<PciDevice> _allPci = [];

    partial void OnShowAllPciChanged(bool value) => FillPci();

    private void Load()
    {
        var cpus = CpuTopology.List();
        var groups = CpuTopology.Groups(cpus);
        var count = _guestCores();
        var recommended = CpuTopology.Recommend(cpus, count);
        _pool = HugePagePool.Sizes();
        _allPci = PciHost.List();
        Ui.Post(() =>
        {
            PinChoices.Clear();
            PinChoices.Add(new PinChoice("Not pinned", "Recommended for everyday use: this computer moves the machine's processors wherever there is room, as it does for any program.", null, null));
            if (recommended is not null)
            {
                var free = cpus.Where(c => !recommended.Contains(c.Id) && groups.First(g => g.Cpus.Contains(recommended[0])).Cpus.Contains(c.Id)).Select(c => c.Id).ToList();
                PinChoices.Add(new PinChoice($"Recommended: processors {CpuTopology.FormatList(recommended)}",
                    $"{count} guest processors on whole cores of the cache group with the most L3, so a game's working set stays in one cache. QEMU's own threads go to {(free.Count > 0 ? CpuTopology.FormatList(free) : "the same processors")}.",
                    recommended, free.Count > 0 ? free : null));
            }

            var largest = groups.Count > 0 ? groups.Max(g => g.L3Bytes) : 0;
            foreach (var g in groups)
                PinChoices.Add(new PinChoice($"{g.Name}: processors {CpuTopology.FormatList(g.Cpus)}", g.Describe(g.L3Bytes == largest && groups.Count > 1) + ". Guest processors take these in turn.", g.Cpus, null));
            PinChoices.Add(new PinChoice("Choose processors by number…", "Type this computer's processor numbers; guest processor 1 runs on the first, 2 on the second, and so on.", null, null, Custom: true));

            if (_original.PinnedCpus.Count > 0)
            {
                Pin = PinChoices.FirstOrDefault(c => c.Cpus is { } list && list.SequenceEqual(_original.PinnedCpus)) ?? PinChoices[^1];
                if (Pin.Custom)
                {
                    CustomCpus = CpuTopology.FormatList(_original.PinnedCpus);
                    CustomEmulator = CpuTopology.FormatList(_original.EmulatorCpus);
                }
            }
            else
            {
                Pin = PinChoices[0];
            }

            NoIommu = _allPci.All(d => d.Group is null);
            OnPropertyChanged(nameof(NoIommu));
            OnPropertyChanged(nameof(PoolText));
            OnPropertyChanged(nameof(NeedsPages));
            OnPropertyChanged(nameof(ReserveText));
            FillPci();
        });
    }

    private void FillPci()
    {
        PciDevices.Clear();
        var chosen = _original.PciDevices.ToHashSet(StringComparer.Ordinal);
        // By default the devices worth handing over: graphics, audio, USB and network controllers, and anything already chosen.
        foreach (var d in _allPci.Where(d => !d.IsBridge && (ShowAllPci || chosen.Contains(d.Address) || d.Kind is "Graphics" or "Audio" or "USB controller" or "Network" or "Wireless" or "NVMe disk"))
                     .OrderBy(d => d.Blocked is null ? 0 : 1).ThenBy(d => d.Kind, StringComparer.Ordinal).ThenBy(d => d.Address, StringComparer.Ordinal))
            PciDevices.Add(new PciRow(d, chosen.Contains(d.Address), PciHost.GroupOf(d, _allPci)));
        PciSummary = NoIommu
            ? "This computer's IOMMU is off, so no device can be handed over. Turn on IOMMU (AMD-Vi or VT-d) in the firmware settings, and add amd_iommu=on or intel_iommu=on to the kernel's options."
            : $"{_allPci.Count(d => d.Group is not null && d.Blocked is null && !d.IsBridge)} devices could be handed over. A device leaves this computer while the machine runs, with everything in its group.";
        OnPropertyChanged(nameof(PciSummary));
    }

    public PerformanceConfig ToConfig()
    {
        List<int> pinned = Pin is null ? [.. _original.PinnedCpus] : Pin.Custom ? CpuTopology.ParseList(CustomCpus) : [.. Pin.Cpus ?? []];
        List<int> emulator = Pin is null ? [.. _original.EmulatorCpus] : Pin.Custom ? CpuTopology.ParseList(CustomEmulator) : [.. Pin.Emulator ?? []];
        // Devices ticked; a group's other devices go with them (QEMU refuses a group handed over in part).
        var chosen = PciDevices.Count == 0 ? _original.PciDevices
            : PciDevices.Where(r => r.Chosen).SelectMany(r => PciHost.GroupOf(r.Device, _allPci)).Select(d => d.Address).Distinct().Order(StringComparer.Ordinal).ToList();
        return _original with
        {
            IoThreads = IoThreads,
            PinnedCpus = pinned,
            EmulatorCpus = pinned.Count > 0 ? emulator : [],
            HugePages = HugePages.Value,
            LookingGlassMiB = LookingGlass ? LookingGlassMiB : null,
            PciDevices = [.. chosen],
        };
    }

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':').Select(d => Path.Combine(d, program)).FirstOrDefault(File.Exists);
}
