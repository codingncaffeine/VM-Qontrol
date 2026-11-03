using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// The viewer (remote-viewer, not ours) captures the mouse while the guest drives a relative mouse; VM Qontrol says how to
/// let go for exactly that long. The signal: a viewer connected and SPICE in "server" mouse mode.
/// </summary>
public sealed class MouseCaptureTests
{
    [Fact]
    public void Capture_needs_a_connected_viewer_and_server_mode()
    {
        var channel = """[{"channel-type": 1, "channel-id": 0}]""";
        JsonNode Spice(string mode, string channels) => JsonNode.Parse($$"""{"enabled": true, "mouse-mode": "{{mode}}", "channels": {{channels}}}""")!;
        Assert.True(MouseCapture.CanCapture(Spice("server", channel)));
        Assert.False(MouseCapture.CanCapture(Spice("client", channel)));
        Assert.False(MouseCapture.CanCapture(Spice("server", "[]")));
        Assert.False(MouseCapture.CanCapture(JsonNode.Parse("""{"enabled": false}""")));
        Assert.False(MouseCapture.CanCapture(null));
    }

    private static async Task<JsonNode?> Qmp(string socket, string command)
    {
        using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await s.ConnectAsync(new UnixDomainSocketEndPoint(socket));
        await using var stream = new NetworkStream(s);
        using var reader = new StreamReader(stream);
        await reader.ReadLineAsync(); // greeting
        var bytes = Encoding.UTF8.GetBytes("{\"execute\":\"qmp_capabilities\"}\n{\"execute\":\"" + command + "\"}\n");
        await stream.WriteAsync(bytes);
        await reader.ReadLineAsync();
        while (await reader.ReadLineAsync() is { } line)
            if (JsonNode.Parse(line) is JsonObject o && o.ContainsKey("return")) return o["return"];
        return null;
    }

    /// <summary>A real QEMU in its firmware (silent: no sound device at all) and a real remote-viewer on a virtual screen and its own bus.</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_viewer_on_a_machine_in_its_firmware_can_capture_and_closing_it_ends_that()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!File.Exists("/usr/bin/remote-viewer") || !File.Exists("/usr/bin/xvfb-run"), "remote-viewer and xvfb-run are needed.");
        var code = new[] { "/usr/share/edk2/x64/OVMF_CODE.4m.fd", "/usr/share/OVMF/OVMF_CODE_4M.fd", "/usr/share/edk2-ovmf/x64/OVMF_CODE.4m.fd" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(code is null, "No OVMF firmware.");
        var dir = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", "vmq-mouse-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Process? qemu = null, viewer = null;
        try
        {
            var start = new ProcessStartInfo("qemu-system-x86_64") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in (string[])["-machine", "q35,accel=kvm", "-m", "256", "-drive", $"if=pflash,format=raw,readonly=on,file={code}",
                         "-device", "qemu-xhci,id=input", "-device", "usb-tablet,bus=input.0", "-display", "none",
                         "-spice", $"disable-ticketing=on,unix=on,addr={dir}/spice.sock", "-qmp", $"unix:{dir}/qmp.sock,server=on,wait=off"])
                start.ArgumentList.Add(a);
            qemu = Process.Start(start)!;
            for (var i = 0; i < 50 && !File.Exists($"{dir}/qmp.sock"); i++) await Task.Delay(100, ct);
            // The control: no viewer yet, so nothing can be captured.
            Assert.False(MouseCapture.CanCapture(await Qmp($"{dir}/qmp.sock", "query-spice")), "captured with no viewer connected");

            var bus = Path.Combine(TestProfile.ScratchRoot, "..", "packaging", "isolated-bus.conf");
            var v = new ProcessStartInfo("xvfb-run") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in (string[])["-a", "dbus-run-session", "--config-file=" + Path.GetFullPath(bus), "remote-viewer", $"spice+unix://{dir}/spice.sock"])
                v.ArgumentList.Add(a);
            v.Environment["PULSE_SERVER"] = "unix:/nonexistent/vmq";
            v.Environment["PIPEWIRE_REMOTE"] = "/nonexistent/vmq";
            v.Environment.Remove("WAYLAND_DISPLAY");
            viewer = Process.Start(v)!;
            var captured = false;
            for (var i = 0; i < 60 && !captured; i++)
            {
                await Task.Delay(250, ct);
                captured = MouseCapture.CanCapture(await Qmp($"{dir}/qmp.sock", "query-spice"));
            }

            Assert.True(captured, "a viewer on a machine in its firmware did not read as able to capture the mouse");
            // Nothing on this bus serves notifications: the note degrades to the machine page, without throwing.
            viewer.Kill(entireProcessTree: true);
            await viewer.WaitForExitAsync(ct);
            var released = false;
            for (var i = 0; i < 40 && !released; i++)
            {
                await Task.Delay(250, ct);
                released = !MouseCapture.CanCapture(await Qmp($"{dir}/qmp.sock", "query-spice"));
            }

            Assert.True(released, "closing the viewer did not end the capture state");
        }
        finally
        {
            if (viewer is { HasExited: false }) viewer.Kill(entireProcessTree: true);
            if (qemu is { HasExited: false }) qemu.Kill();
            if (qemu is not null) await qemu.WaitForExitAsync(CancellationToken.None);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Process StartViewer(string spiceSocket)
    {
        var bus = Path.GetFullPath(Path.Combine(TestProfile.ScratchRoot, "..", "packaging", "isolated-bus.conf"));
        var v = new ProcessStartInfo("xvfb-run") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["-a", "dbus-run-session", "--config-file=" + bus, "remote-viewer", $"spice+unix://{spiceSocket}"])
            v.ArgumentList.Add(a);
        v.Environment["PULSE_SERVER"] = "unix:/nonexistent/vmq";
        v.Environment["PIPEWIRE_REMOTE"] = "/nonexistent/vmq";
        v.Environment.Remove("WAYLAND_DISPLAY");
        return Process.Start(v)!;
    }

    /// <summary>
    /// The app's own path: a machine powered on in the background (no sound card at all, waiting in its firmware), a
    /// real remote-viewer connects on a virtual screen, the status bar says how to get the mouse back, and the hint goes
    /// when the viewer closes.
    /// </summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_status_bar_says_how_to_free_the_mouse_while_a_viewer_can_hold_it()
    {
        Assert.SkipWhen(!File.Exists("/usr/bin/remote-viewer") || !File.Exists("/usr/bin/xvfb-run"), "remote-viewer and xvfb-run are needed.");
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("mouse-hint");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Firmware only");
        Directory.CreateDirectory(dir);
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        MachineStore.Save(new MachineConfig
        {
            Name = "Firmware only",
            MemoryMiB = 256,
            Cpu = new CpuConfig { Cores = 1 },
            Audio = new AudioConfig { Device = "none" },
            Display = new DisplayConfig { Gl = false },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
        }, Path.Combine(dir, "vm.json"));
        var settings = AppSettings.Load(profile.Paths);
        settings.WarnLowMemory = false;
        settings.Save(profile.Paths);

        Process? viewer = null;
        MachineViewModel? machine = null;
        try
        {
            var seen = await Ui.Run(() =>
            {
                var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
                var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Show();
                _ = shell.StartAsync();
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                machine = shell.Machines[0];
                shell.Selected = machine;
                machine.PowerOnInBackgroundCommand.Execute(null);
                Ui.PumpUntil(() => machine.Running is not null, "the machine to run", 30);
                Ui.Pump(1.5);
                var before = shell.MouseHint; // the control: no viewer, no hint
                viewer = StartViewer(machine.Running!.Paths.Spice);
                Ui.PumpUntil(() => shell.MouseHint is not null, "the mouse hint", 20);
                Ui.Pump(0.3);
                var shown = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "MouseHintText");
                var text = (shell.MouseHint, shown.IsEffectivelyVisible);
                Ui.Capture(window, "s9", "mouse-hint-dark");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.3);
                Ui.Capture(window, "s9", "mouse-hint-light");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                viewer.Kill(entireProcessTree: true);
                Ui.PumpUntil(() => shell.MouseHint is null, "the hint to go once the viewer closed", 15);
                window.Close();
                return (before, text);
            });

            Assert.Null(seen.before);
            Assert.Equal("releases the mouse from Firmware only. Once its system is installed, the mouse moves freely by itself.", seen.text.MouseHint);
            Assert.True(seen.text.IsEffectivelyVisible);
        }
        finally
        {
            if (viewer is { HasExited: false }) viewer.Kill(entireProcessTree: true);
            if (machine?.Running is { } running) await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            else if (MachineRunner.Find(MachineStore.Load(Path.Combine(dir, "vm.json")), profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }
    }
}
