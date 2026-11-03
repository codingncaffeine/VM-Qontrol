using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// The user's report (2026-11-03): copied on Linux, paste greyed out in Windows 10 — the guest had no guest tools, so no
/// SPICE agent, and nothing said so. Now, while a viewer shows a machine whose guest tools do not answer, the status
/// bar says copy and paste needs them, and its click opens the machine's guest tools steps. The viewer here is a SPICE
/// main-channel link made by the test (no display, no sound channel); control: before it connects, no hint.
/// </summary>
public sealed class ClipboardHintTests
{
    /// <summary>A SPICE client's link to the main channel, ticketing disabled (an empty password, as the protocol asks).</summary>
    private static async Task<Socket> LinkMainChannelAsync(string spiceSocket, CancellationToken ct)
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await s.ConnectAsync(new UnixDomainSocketEndPoint(spiceSocket), ct);
        var mess = new byte[18]; // connection id 0, channel main (1) id 0, no capabilities, capabilities at 18
        mess[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(mess.AsSpan(14), 18);
        var header = new byte[16];
        "REDQ"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)mess.Length);
        await s.SendAsync(header.Concat(mess).ToArray(), ct);
        async Task<byte[]> ReadAsync(int n)
        {
            var b = new byte[n];
            for (var got = 0; got < n;) got += await s.ReceiveAsync(b.AsMemory(got), ct) is > 0 and var r ? r : throw new IOException("SPICE closed the link");
            return b;
        }

        var replyHeader = await ReadAsync(16);
        var reply = await ReadAsync((int)BinaryPrimitives.ReadUInt32LittleEndian(replyHeader.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(reply)); // SPICE_LINK_ERR_OK
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(reply.AsSpan(4, 162), out _);
        await s.SendAsync(rsa.Encrypt([0], RSAEncryptionPadding.OaepSHA1), ct);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(await ReadAsync(4)));
        return s;
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task Paste_without_guest_tools_says_why_while_a_viewer_shows_the_machine()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("clipboard-hint");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Win");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "Win", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Guest = new GuestConfig { Os = "windows", Release = "10" }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1, Bus = DiskBus.Sata }] };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        MachineStore.Save(m, Path.Combine(dir, "vm.json"));
        // With a screen (SPICE on its socket): the sound goes only to a client's playback channel, and none is opened.
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions(), ct);
        var grace = MachineViewModel.ClipboardGrace;
        MachineViewModel.ClipboardGrace = TimeSpan.FromSeconds(2);
        Socket? viewer = null;
        try
        {
            var seen = await Ui.Run(() =>
            {
                var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
                var window = new MainWindow { DataContext = shell, Width = 1320, Height = 860 };
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Show();
                _ = shell.StartAsync();
                Ui.PumpUntil(() => shell.Machines.Count == 1 && shell.Machines[0].IsRunning, "the running machine", 20);
                Ui.PumpUntil(() => shell.Machines[0].GuestToolsAnswering == false, "the agent check", 20);
                Ui.Pump(3.5);
                var before = shell.ClipboardHint;
                viewer = Task.Run(() => LinkMainChannelAsync(running.Paths.Spice, CancellationToken.None)).GetAwaiter().GetResult();
                Ui.PumpUntil(() => shell.ClipboardHint is not null, "the copy and paste hint", 10);
                var bar = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ClipboardHintBar");
                var shown = bar.IsEffectivelyVisible;
                var text = shell.ClipboardHint;
                Ui.Capture(window, "phase17", "clipboard-hint-dark");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.3);
                Ui.Capture(window, "phase17", "clipboard-hint-light");
                Ui.Click(window, bar);
                Ui.Pump(0.4);
                var selected = shell.Selected?.Name;
                var steps = shell.Selected?.ShowGuestToolsSteps;
                var scrolled = window.GetVisualDescendants().OfType<MachineView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y;
                Ui.Capture(window, "phase17", "clipboard-hint-steps");
                viewer.Dispose();
                Ui.PumpUntil(() => shell.ClipboardHint is null, "the hint to go with the viewer", 10);
                window.Close();
                return (before, shown, text, selected, steps, scrolled);
            });

            Assert.Null(seen.before);
            Assert.True(seen.shown);
            Assert.Equal("needs the guest tools in Win. Its Summary page puts them in.", seen.text);
            Assert.Equal("Win", seen.selected);
            Assert.True(seen.steps);
            Assert.True(seen.scrolled > 100, $"the page did not scroll to the guest tools card (offset {seen.scrolled})");
        }
        finally
        {
            MachineViewModel.ClipboardGrace = grace;
            viewer?.Dispose();
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
