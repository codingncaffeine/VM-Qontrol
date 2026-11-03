using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

public sealed class MachineStoreTests
{
    [Fact]
    public void A_machine_round_trips_through_vm_json()
    {
        using var profile = new TestProfile("store");
        var file = Path.Combine(profile.Root, MachineStore.FileName);
        var machine = new MachineConfig
        {
            Name = "Dev box",
            Guest = new GuestConfig { Os = "windows", Release = "11" },
            MemoryMiB = 8192,
            Tpm = true,
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 80 }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = "/isos/win11.iso", Connect = Connect.UntilInstalled }],
            Nics = [new NicConfig { Forwards = [new PortForward("tcp", 3389, 3389)] }],
            Usb = new UsbConfig { Passthrough = [new UsbDevice("046d", "c52b", "Logitech receiver")] },
        };
        MachineStore.Save(machine, file);
        var back = MachineStore.Load(file);
        Assert.Equal(MachineStore.ToJson(machine), MachineStore.ToJson(back));
        Assert.False(File.Exists(file + ".new"));
        Assert.Contains("\"installer\"", File.ReadAllText(file).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_format_is_refused_with_a_reason()
    {
        using var profile = new TestProfile("store");
        var file = Path.Combine(profile.Root, MachineStore.FileName);
        File.WriteAllText(file, """{"schemaVersion": 99, "name": "x"}""");
        var ex = Assert.Throws<InvalidDataException>(() => MachineStore.Load(file));
        Assert.Contains("newer VM Qontrol", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A file written by an older version (or by hand) lacks newer settings: each must keep its default, never become null.</summary>
    [Fact]
    public void A_minimal_vm_json_loads_with_every_missing_setting_at_its_default()
    {
        using var profile = new TestProfile("store");
        var file = Path.Combine(profile.Root, MachineStore.FileName);
        File.WriteAllText(file, """{"schemaVersion": 1, "name": "old"}""");
        var m = MachineStore.Load(file);
        Assert.NotNull(m.Nics);
        Assert.Single(m.Nics);
        Assert.NotNull(m.Disks);
        Assert.NotNull(m.Media);
        Assert.NotNull(m.Guest);
        Assert.Equal("linux", m.Guest.Os);
        Assert.NotNull(m.Firmware);
        Assert.NotNull(m.Cpu);
        Assert.NotNull(m.Display);
        Assert.NotNull(m.Audio);
        Assert.NotNull(m.Input);
        Assert.Equal("en-us", m.Input.Layout);
        Assert.NotNull(m.Usb);
        Assert.NotNull(m.Usb.Passthrough);
        Assert.NotNull(m.ExtraArgs);
        Assert.False(string.IsNullOrEmpty(m.Id));
    }

    [Fact]
    public void Settings_missing_newer_fields_keep_their_defaults()
    {
        using var profile = new TestProfile("settings");
        Directory.CreateDirectory(profile.Paths.Config);
        File.WriteAllText(profile.Paths.SettingsFile, """{"viewer": "spicy"}""");
        var settings = VmQontrol.Core.AppSettings.Load(profile.Paths);
        Assert.Equal("spicy", settings.Viewer);
        Assert.NotNull(settings.Favorites);
        Assert.NotNull(settings.Folders);
        Assert.Equal([profile.Paths.DefaultLibrary], settings.LibraryRoots);
    }
}

public sealed class MachineStoreIdTests
{
    [Fact]
    public void A_settings_file_without_an_id_gets_the_same_one_at_every_read()
    {
        using var profile = new TestProfile("noid");
        var file = Path.Combine(profile.Root, "vm.json");
        File.WriteAllText(file, """{ "name": "hand-made" }""");
        var first = VmQontrol.Core.Machines.MachineStore.Load(file).Id;
        Assert.Equal(first, VmQontrol.Core.Machines.MachineStore.Load(file).Id);
        Assert.Matches("^[0-9a-f]{32}$", first);
        File.WriteAllText(file, """{ "name": "hand-made", "id": "0123456789abcdef0123456789abcdef" }""");
        Assert.Equal("0123456789abcdef0123456789abcdef", VmQontrol.Core.Machines.MachineStore.Load(file).Id);
    }
}
