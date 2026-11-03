using Avalonia.Controls;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class ImportView : UserControl
{
    public ImportView()
    {
        InitializeComponent();
        this.FindControl<Button>("PickMachineFile")!.Click += async (_, _) =>
            await PickAsync("Choose the machine to import", "Machines", ["*.vmx", "*.vbox", "*.ovf", "*.ova", "*.xml", "*.tar"]);
        this.FindControl<Button>("PickDisk")!.Click += async (_, _) =>
            await PickAsync("Choose the disk to import", "Disks", ["*.vmdk", "*.vdi", "*.vhd", "*.vhdx", "*.qcow2", "*.img", "*.raw"]);
    }

    private async Task PickAsync(string title, string kind, string[] patterns)
    {
        if (DataContext is not ImportViewModel vm) return;
        try
        {
            if (await Pickers.FileAsync(this, title, kind, patterns) is { } path) await vm.ReadAsync(path);
        }
        catch (PickerFailedException)
        {
            // Logged by the picker; the page stays as it was.
        }
    }
}
