using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>An address a segment's server gave out, with the machine that has that MAC when it is in the library.</summary>
public sealed record LeaseRow(string Address, string Mac, string Who);

/// <summary>One private segment in the editor: its subnet, whether it hands out addresses, its machines and their addresses.</summary>
public sealed partial class SegmentRow : ObservableObject
{
    public SegmentRow(string name, SegmentConfig? config, IReadOnlyList<string> machines, IReadOnlyList<LeaseRow> leases, bool serving)
    {
        Name = name;
        _subnet = config?.Subnet ?? "";
        _dhcp = config?.Dhcp ?? true;
        Machines = machines;
        Leases = leases;
        Serving = serving;
        DefaultSubnet = SegmentSubnet.ForName(name).ToString();
    }

    public string Name { get; }

    public IReadOnlyList<string> Machines { get; }

    public string MachinesText => Machines.Count == 0 ? "No machine uses it" : string.Join(", ", Machines);

    public IReadOnlyList<LeaseRow> Leases { get; }

    public bool HasLeases => Leases.Count > 0;

    public bool Serving { get; }

    public string StateText => !Dhcp ? "Addresses set by hand" : Serving ? "Handing out addresses" : "Starts with its first machine";

    /// <summary>The subnet the name gives, shown as the placeholder and used while the field is empty.</summary>
    public string DefaultSubnet { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubnetError), nameof(HasSubnetError), nameof(ServerText))]
    private string _subnet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _dhcp;

    public string? SubnetError => Subnet.Trim().Length == 0 || SegmentSubnet.Parse(Subnet) is not null
        ? null : "A private range from /16 to /29: 10.x.x.0/24, 172.16–31.x.0/24 or 192.168.x.0/24.";

    public bool HasSubnetError => SubnetError is not null;

    public string ServerText
    {
        get
        {
            var s = SegmentSubnet.Parse(Subnet.Trim().Length > 0 ? Subnet : DefaultSubnet) ?? SegmentSubnet.ForName(Name);
            return $"Machines get {s.Host(10)} onward; VM Qontrol's server answers at {s.Server}. No router and no name server: the segment reaches nothing else.";
        }
    }

    public bool CanRemove => Machines.Count == 0;

    public SegmentConfig ToConfig() => new() { Name = Name, Subnet = Subnet.Trim().Length > 0 ? SegmentSubnet.Parse(Subnet)?.ToString() : null, Dhcp = Dhcp };
}

/// <summary>A bridge of this computer and whether machines may join it.</summary>
public sealed record BridgeRow(string Name, bool Allowed, bool Up)
{
    public string StateText => Allowed ? Up ? "Machines can join" : "Allowed, but down" : "Not allowed in bridge.conf";

    public bool Ok => Allowed && Up;
}

/// <summary>
/// The virtual network editor, as Workstation's: every private segment with its subnet and address server, the NAT
/// network inside machines, and this computer's bridges. Lists and leases are read on a worker; saving writes the app's
/// settings, which take effect at a machine's next power on (and for a segment, when its server next starts).
/// </summary>
public sealed partial class NetworksViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public NetworksViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _natSubnet = shell.Settings.NatSubnet ?? "";
        _ = RefreshAsync();
    }

    public ObservableCollection<SegmentRow> Segments { get; } = [];

    public ObservableCollection<BridgeRow> Bridges { get; } = [];

    public bool NoBridges => Bridges.Count == 0;

    public bool NoSegments => Segments.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NatError), nameof(HasNatError), nameof(NatText))]
    private string _natSubnet;

    public string? NatError => NatSubnet.Trim().Length == 0 || SegmentSubnet.Parse(NatSubnet) is not null ? null : "A private range from /16 to /29, such as 10.0.5.0/24.";

    public bool HasNatError => NatError is not null;

    public string NatText
    {
        get
        {
            var s = SegmentSubnet.Parse(NatSubnet.Trim().Length > 0 ? NatSubnet : "10.0.2.0/24")!;
            return $"Inside each machine: this computer at {s.Host(2)}, its name server at {s.Host(3)}, the machine itself from {s.Host(15)}.";
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSegmentCommand))]
    private string _newSegment = "";

    [ObservableProperty] private string _note = "";

    public event Action? Closed;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var machines = _shell.Machines.Select(m => (m.Name, m.Machine)).ToList();
        var settings = _shell.Settings;
        var paths = _shell.Paths;
        var (segments, bridges) = await Task.Run(() =>
        {
            var used = machines.SelectMany(x => x.Machine.Nics.Where(n => n.Mode == NetMode.Segment).Select(n => (Segment: n.Segment ?? "default", x.Name, Mac: n.Mac ?? QemuCommandBuilder.MacFor(x.Machine.Id)))).ToList();
            var names = used.Select(u => u.Segment).Concat(settings.Segments.Select(s => s.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var rows = names.Select(name =>
            {
                var leases = SegmentDhcp.Load(SegmentServer.LeaseFile(paths, name))
                    .Select(l => new LeaseRow(l.Address, l.Mac, used.FirstOrDefault(u => u.Segment == name && string.Equals(u.Mac, l.Mac, StringComparison.OrdinalIgnoreCase)).Name ?? l.HostName ?? "a machine not in the library"))
                    .OrderBy(l => System.Net.IPAddress.TryParse(l.Address, out var a) ? SegmentSubnet.ToUInt(a) : 0).ToList();
                return (name, config: settings.Segments.FirstOrDefault(s => s.Name == name), machines: used.Where(u => u.Segment == name).Select(u => u.Name).Distinct().ToList(), leases, serving: SegmentServer.IsRunning(paths, name));
            }).ToList();
            return (rows, HostBridges.List());
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            Segments.Clear();
            foreach (var s in segments) Segments.Add(new SegmentRow(s.name, s.config, s.machines, s.leases, s.serving));
            Bridges.Clear();
            foreach (var b in bridges) Bridges.Add(new BridgeRow(b.Name, b.Allowed, b.Up));
            OnPropertyChanged(nameof(NoBridges));
            OnPropertyChanged(nameof(NoSegments));
        });
    }

    private bool CanAddSegment() => NewSegment.Trim().Length > 0 && Segments.All(s => s.Name != NewSegment.Trim());

    [RelayCommand(CanExecute = nameof(CanAddSegment))]
    private void AddSegment()
    {
        var name = NewSegment.Trim();
        Segments.Add(new SegmentRow(name, null, [], [], false));
        NewSegment = "";
        OnPropertyChanged(nameof(NoSegments));
        Note = $"{name} is ready: choose it in a machine's Settings, Network, Private segment.";
    }

    [RelayCommand]
    private void RemoveSegment(SegmentRow row)
    {
        if (!row.CanRemove) return;
        Segments.Remove(row);
        OnPropertyChanged(nameof(NoSegments));
    }

    [RelayCommand]
    private void Save()
    {
        if (HasNatError || Segments.Any(s => s.HasSubnetError))
        {
            Note = "Fix the subnets marked in red first.";
            return;
        }

        // Only segments that differ from the defaults, or that were added here, are kept in the settings.
        var segments = Segments.Select(s => s.ToConfig()).Where(c => c.Subnet is not null || !c.Dhcp || Segments.First(r => r.Name == c.Name).Machines.Count == 0).ToList();
        var nat = NatSubnet.Trim().Length > 0 ? SegmentSubnet.Parse(NatSubnet)!.ToString() : null;
        _shell.UpdateSettings(s => s with { Segments = segments, NatSubnet = nat == "10.0.2.0/24" ? null : nat });
        Note = "Saved. Machines use it from their next power on; a segment's server takes a new subnet when it next starts.";
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
