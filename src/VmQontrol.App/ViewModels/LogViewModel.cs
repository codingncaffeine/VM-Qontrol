using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>What VM Qontrol, QEMU and the TPM emulator said about one machine, newest at the bottom.</summary>
public sealed partial class LogViewModel(QontrolPaths paths, MachineConfig machine) : ObservableObject
{
    [ObservableProperty] private string _text = "";

    [RelayCommand]
    public async Task LoadAsync()
    {
        var text = await Task.Run(() =>
        {
            var parts = new List<string>();
            if (Log.FilePath is { } app && File.Exists(app))
            {
                using var stream = new FileStream(app, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = reader.ReadToEnd().Split('\n').Where(l => l.Contains(machine.Name, StringComparison.Ordinal)).TakeLast(200).ToList();
                if (lines.Count > 0) parts.Add("── VM Qontrol\n" + string.Join('\n', lines));
            }

            var dir = MachineRunner.LogDirectory(paths, machine);
            foreach (var (file, title) in new[] { ("qemu.log", "QEMU"), ("swtpm.log", "TPM emulator") })
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path)) parts.Add($"── {title} ({path})\n" + string.Join('\n', File.ReadAllLines(path).TakeLast(200)));
            }

            return parts.Count == 0 ? "Nothing logged for this machine yet." : string.Join("\n\n", parts);
        }).ConfigureAwait(false);
        Ui.Post(() => Text = text);
    }
}
