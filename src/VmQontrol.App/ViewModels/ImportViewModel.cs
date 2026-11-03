using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Import;
using VmQontrol.Core.Machines;

namespace VmQontrol.App.ViewModels;

/// <summary>A machine libvirt knows (virt-manager, GNOME Boxes), offered for import.</summary>
public sealed record LibvirtDomain(string Name, string Uri)
{
    public string Where => Uri.EndsWith("/system", StringComparison.Ordinal) ? "System machines (virt-manager's default)" : "Your own machines (GNOME Boxes)";
}

/// <summary>
/// Import a machine (phase 11, D30): choose where it comes from, read it, show what VM Qontrol will make of it and every
/// guess, then copy it in. Reading and copying run on workers.
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _copy;

    public ImportViewModel(ShellViewModel shell)
    {
        _shell = shell;
        LibraryRoot = shell.Settings.LibraryRoots[0];
    }

    public event Action? Closed;

    public string LibraryRoot { get; }

    public IReadOnlyList<GuestChoice> GuestChoices { get; } = new NewMachineViewModel.GuestChoiceList();

    /// <summary>What was read; <c>null</c> while choosing the source.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Choosing), nameof(Reviewing), nameof(Rows), nameof(HasNotes), nameof(HasProblems), nameof(IsBareDisk), nameof(KindTitle), nameof(DetectedSystem))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private ForeignMachine? _machine;

    public bool Choosing => Machine is null;

    public bool Reviewing => Machine is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rows))]
    private GuestChoice? _system;

    /// <summary>A bare disk's firmware, which the disk does not say (UEFI for anything recent).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rows))]
    private bool _uefi = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _busy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private double _percent;

    public ObservableCollection<LibvirtDomain> Domains { get; } = [];

    [ObservableProperty]
    private string _domainsNote = "";

    public bool IsBareDisk => Machine?.Kind == "disk";

    /// <summary>What the machine said it runs, shown in the system list until another is chosen.</summary>
    public string DetectedSystem => Machine is not { } f ? "" : $"{GuestNames.Describe(f.Guest)}{(f.SourceOs.Length > 0 && !f.SourceOs.StartsWith("http", StringComparison.Ordinal) ? $" (it said \"{f.SourceOs}\")" : "")}: keep it, or choose another";

    public bool HasNotes => Machine?.Notes.Count > 0;

    public bool HasProblems => Machine?.Problems.Count > 0;

    public string KindTitle => Machine?.Kind switch
    {
        "VMware" => "From VMware",
        "VirtualBox" => "From VirtualBox",
        "OVF" => "From an OVF appliance",
        "libvirt" => "From virt-manager",
        "disk" => "From a disk",
        _ => "",
    };

    /// <summary>The machine as it will be made, row by row, in words.</summary>
    public IReadOnlyList<DetailRow> Rows
    {
        get
        {
            if (Machine is not { } f) return [];
            var rows = new List<DetailRow>
            {
                new("Memory", f.MemoryMiB is { } m ? (m % 1024 == 0 ? $"{m / 1024} GB" : $"{m} MB") : "Recommended for this computer"),
                new("Processors", f.Cores is { } c ? $"{c}" : "Recommended for this computer"),
                new("Firmware", (IsBareDisk ? Uefi : f.Firmware == FirmwareKind.Uefi) ? f.SecureBoot ? "UEFI with Secure Boot" : "UEFI" : "BIOS"),
            };
            if (f.Tpm) rows.Add(new("TPM", "A new one (version 2.0)"));
            for (var i = 0; i < f.Disks.Count; i++)
                rows.Add(new(f.Disks.Count == 1 ? "Disk" : $"Disk {i + 1}", $"{Path.GetFileName(f.Disks[i].Path)}, copied as qcow2, on {BusWords(f.Disks[i].Bus)}"));
            if (f.Discs.Count > 0) rows.Add(new("CD/DVD", Path.GetFileName(f.Discs[0]) + " (left where it is)"));
            if (f.Nics.FirstOrDefault() is { } nic) rows.Add(new("Network", $"NAT, {NicWords(nic.Model)}{(nic.Mac is { } mac ? $", address {mac}" : "")}"));
            rows.Add(new("Copied to", Path.Combine(LibraryRoot, Name.Trim().Length > 0 ? Name.Trim() : f.Name)));
            rows.Add(new("Original", f.Source + " (only read, never changed)"));
            return rows;
        }
    }

    private static string BusWords(DiskBus bus) => bus switch
    {
        DiskBus.Nvme => "NVMe",
        DiskBus.Pvscsi => "VMware paravirtual SCSI",
        DiskBus.Virtio => "VirtIO",
        DiskBus.Sata or DiskBus.Ide => "SATA",
        _ => "the fastest the system supports",
    };

    private static string NicWords(string? model) => model switch
    {
        "vmxnet3" => "VMware vmxnet3 adapter",
        "e1000e" => "Intel e1000e adapter",
        "e1000" => "Intel e1000 adapter",
        "virtio-net-pci" => "VirtIO adapter",
        "pcnet" => "AMD PCnet adapter",
        "rtl8139" => "Realtek adapter",
        _ => "the recommended adapter",
    };

    /// <summary>Reads a machine file or a disk the person picked; a VM Qontrol export (.tar) comes straight in.</summary>
    public async Task ReadAsync(string file)
    {
        if (file.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            await UnpackAsync(file).ConfigureAwait(false);
            return;
        }

        Busy = true;
        Status = "Reading the machine…";
        try
        {
            var root = LibraryRoot;
            var f = await Task.Run(() => MachineImporter.Read(file, root)).ConfigureAwait(false);
            Ui.Post(() => Show(f));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or global::System.Xml.XmlException)
        {
            Log.Warn($"Import: {file} could not be read ({ex.Message}).");
            Ui.Post(() => _shell.Show(new NoticeViewModel("This could not be imported", [ex.Message, "VM Qontrol imports VMware (.vmx), VirtualBox (.vbox), OVF appliances (.ovf, .ova), libvirt descriptions (.xml) and disks (.vmdk, .vdi, .vhdx, .qcow2, .img)."], isError: true)));
        }
        finally
        {
            Ui.Post(() =>
            {
                Busy = false;
                Status = "";
            });
        }
    }

    private async Task UnpackAsync(string tar)
    {
        Ui.Post(() =>
        {
            Busy = true;
            Status = "Unpacking and checking every file…";
        });
        try
        {
            var root = LibraryRoot;
            var dir = await Task.Run(() => MachineExport.UnpackAsync(tar, root, CancellationToken.None)).ConfigureAwait(false);
            Ui.Post(() =>
            {
                Closed?.Invoke();
                _ = _shell.FinishNewMachineAsync(dir, powerOn: false);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or global::System.Text.Json.JsonException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel("The machine was not brought in", [ex.Message, "Nothing of it was kept."], isError: true)));
        }
        finally
        {
            Ui.Post(() =>
            {
                Busy = false;
                Status = "";
            });
        }
    }

    private void Show(ForeignMachine f)
    {
        Name = f.Name;
        System = null;
        Uefi = f.Firmware == FirmwareKind.Uefi;
        Machine = f;
    }

    /// <summary>The machines libvirt knows, on both of its connections.</summary>
    [RelayCommand]
    private async Task LoadDomainsAsync()
    {
        DomainsNote = "Asking libvirt…";
        var found = new List<LibvirtDomain>();
        foreach (var uri in new[] { "qemu:///session", "qemu:///system" })
            foreach (var name in await LibvirtReader.DomainsAsync(uri, CancellationToken.None).ConfigureAwait(false))
                found.Add(new LibvirtDomain(name, uri));
        Ui.Post(() =>
        {
            Domains.Clear();
            foreach (var d in found) Domains.Add(d);
            DomainsNote = found.Count > 0 ? "" : "libvirt has no machines here (or virt-manager is not installed). A machine's description saved with \"virsh dumpxml\" can be chosen as a file instead.";
        });
    }

    [RelayCommand]
    private async Task ReadDomainAsync(LibvirtDomain domain)
    {
        Busy = true;
        Status = $"Reading {domain.Name}…";
        var xml = await LibvirtReader.DumpXmlAsync(domain.Uri, domain.Name, CancellationToken.None).ConfigureAwait(false);
        Ui.Post(() =>
        {
            Busy = false;
            Status = "";
            if (xml is null)
            {
                _shell.Show(new NoticeViewModel($"{domain.Name} could not be read", ["libvirt did not give its description."], isError: true));
                return;
            }

            try
            {
                Show(LibvirtReader.Read(xml, $"{domain.Uri} {domain.Name}"));
            }
            catch (Exception ex) when (ex is InvalidDataException or global::System.Xml.XmlException)
            {
                _shell.Show(new NoticeViewModel($"{domain.Name} could not be read", [ex.Message], isError: true));
            }
        });
    }

    private bool CanImport() => Machine is { Problems.Count: 0 } && !Busy && Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (Machine is not { } f) return;
        var chosen = f with
        {
            Name = Name.Trim(),
            Guest = System is { } s ? new GuestConfig { Os = s.Os, Release = s.Release, OsInfoId = f.Guest.OsInfoId } : f.Guest,
            Firmware = IsBareDisk ? (Uefi ? FirmwareKind.Uefi : FirmwareKind.Bios) : f.Firmware,
        };
        Busy = true;
        Status = "Copying…";
        _copy = new CancellationTokenSource();
        var token = _copy.Token;
        var root = LibraryRoot;
        var progress = new Progress<ImportProgress>(p => Ui.Post(() =>
        {
            Percent = (p.Disk - 1 + p.Percent / 100) * 100 / Math.Max(1, p.Disks);
            Status = $"{p.Status} {p.Percent:0}%";
        }));
        try
        {
            var (_, dir) = await Task.Run(() => MachineImporter.ImportAsync(chosen, root, progress, token)).ConfigureAwait(false);
            Ui.Post(() =>
            {
                Closed?.Invoke();
                _ = _shell.FinishNewMachineAsync(dir, powerOn: false);
            });
        }
        catch (OperationCanceledException)
        {
            Ui.Post(() => _shell.Note("The import was stopped; nothing of it was kept."));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel($"{chosen.Name} was not imported", [ex.Message, "Nothing of it was kept, and the original is as it was."], isError: true)));
        }
        finally
        {
            Ui.Post(() =>
            {
                Busy = false;
                Status = "";
                Percent = 0;
            });
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Machine?.TemporaryFolder is { } tmp) _ = Task.Run(() => { try { Directory.Delete(tmp, recursive: true); } catch (IOException) { } });
        Machine = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Busy && _copy is not null)
        {
            _copy.Cancel();
            return;
        }

        Back();
        Closed?.Invoke();
    }
}
