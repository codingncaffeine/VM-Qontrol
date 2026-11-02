using System.Globalization;
using System.Text;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Qemu;

/// <summary>
/// Turns a machine's settings into the QEMU command line for one power on. The rules are quickemu 4.9.9's, step for step
/// (configure_cpu, configure_ram, configure_bios, configure_os_quirks, configure_display, configure_audio,
/// configure_ports, then vm_boot's argument order), and the tests hold the output to quickemu's own command lines.
/// Where VM Qontrol differs on purpose it says so here and in docs/profiles.md: QMP replaces the human monitor, every
/// socket lives in a short runtime folder, and a headless launch has no screen and no sound.
/// </summary>
public static class QemuCommandBuilder
{
    /// <summary>quickemu's public macOS SMC key, stored rot13 in its source as in OSX-KVM.</summary>
    private const string AppleSmcRot13 = "bheuneqjbexolgurfrjbeqfthneqrqcyrnfrqbagfgrny(p)NccyrPbzchgreVap";

    private static readonly string[] MacFlagsForNonIntel =
    [
        "abm", "adx", "aes", "amd-ssbd", "apic", "arat", "bmi1", "bmi2", "clflush", "cmov", "cx8", "cx16", "de",
        "eist", "erms", "f16c", "fma", "fp87", "fsgsbase", "fxsr", "invpcid", "invtsc", "lahf_lm", "lm",
        "mca", "mce", "mmx", "movbe", "mpx", "msr", "mtrr", "nx", "pae", "pat", "pcid", "pge", "pse", "popcnt", "pse36",
        "rdrand", "rdtscp", "sep", "smep", "syscall", "tsc", "tsc_adjust", "vaes", "vbmi2", "vmx", "vpclmulqdq",
        "x2apic", "xgetbv1", "xsave", "xsaveopt",
    ];

    public static QemuLaunch Build(MachineConfig m, LaunchContext ctx)
    {
        var host = ctx.Host;
        var os = m.Guest.Os.ToLowerInvariant();
        var release = m.Guest.Release.ToLowerInvariant();
        var errors = new List<string>();
        var warnings = new List<string>();
        var args = new List<string>();
        var qemuShort = host.QemuVersion.Major * 10 + host.QemuVersion.Minor;
        var kvm = host.KvmUsable;
        if (!kvm) warnings.Add("KVM is not available, so the machine runs without hardware acceleration and will be very slow. The host check explains how to enable it.");
        string File(string path) => Path.IsPathRooted(path) ? path : Path.Combine(ctx.MachineDirectory, path);
        var isBsd = os.EndsWith("bsd", StringComparison.Ordinal);
        var isWindows = os is "windows" or "windows-server";
        var macModern = release is "monterey" or "ventura" or "sonoma" or "sequoia" or "tahoe";
        var macVirtio = macModern || release == "big-sur";

        // ── configure_cpu
        var tweaks = new List<string>();
        if (kvm) tweaks.AddRange(["-global", "kvm-pit.lost_tick_policy=discard"]);
        var machineType = "q35";
        var cpuModel = m.Cpu.Model ?? (kvm ? "host" : "qemu64");
        var smm = isWindows || os == "freedos" || (os == "linux" && m.Firmware.SecureBoot) ? "on" : "off";
        var cpu = new CpuFlagList(cpuModel);
        switch (os)
        {
            case "batocera" or "freedos" or "haiku" or "solaris":
                machineType = "pc";
                break;
            case "kolibrios" or "reactos":
                cpu = new CpuFlagList("qemu32");
                machineType = "pc";
                break;
            case "macos":
                cpu = host.IsIntel
                    ? new CpuFlagList("host,-pdpe1gb,+hypervisor,vmware-cpuid-freq=on")
                    : new CpuFlagList("Haswell-v2,vendor=GenuineIntel,-pdpe1gb,+avx,+sse,+sse2,+ssse3,vmware-cpuid-freq=on");
                if (kvm) MacCpuFlags(cpu, host, release, errors);
                tweaks.AddRange(["-global", "ICH9-LPC.disable_s3=1", "-global", "ICH9-LPC.acpi-pci-hotplug-with-bridge-support=off",
                    "-device", "isa-applesmc,osk=" + Rot13(AppleSmcRot13)]);
                if (qemuShort >= 70) machineType += ",hpet=off";
                else tweaks.Add("-no-hpet");
                break;
            case "windows" or "windows-server":
                cpu = new CpuFlagList($"{cpuModel},+hypervisor,+invtsc,l3-cache=on");
                if (kvm)
                {
                    if (qemuShort > 60)
                    {
                        cpu.Add("migratable=no");
                        cpu.Add("hv_passthrough");
                    }
                    else
                    {
                        foreach (var f in new[] { "migratable=no", "hv_frequencies", "kvm_pv_unhalt", "hv_reenlightenment", "hv_relaxed", "hv_spinlocks=8191", "hv_stimer", "hv_synic", "hv_time", "hv_vapic", "hv_vendor_id=1234567890ab", "hv_vpindex" })
                            cpu.Add(f);
                    }
                }

                tweaks.AddRange(["-global", "ICH9-LPC.disable_s3=1"]);
                if (qemuShort >= 70) machineType += ",hpet=off";
                else tweaks.Add("-no-hpet");
                break;
        }

        if (host.IsAmd && os != "macos") cpu.Add("topoext");

        var cores = m.Cpu.Cores ?? host.LogicalCpus switch { >= 32 => 16, >= 16 => 8, >= 8 => 4, >= 4 => 2, _ => 1 };
        if (os == "windows" && release == "11" && cores < 2)
        {
            warnings.Add("Windows 11 needs at least 2 processor cores; using 2.");
            cores = 2;
        }

        if (os == "macos")
        {
            var power = 1;
            while (power * 2 <= cores) power *= 2;
            cores = power;
        }

        var threads = cores >= 2 && host.SmtOn ? 2 : 1;
        var smp = $"cores={cores / threads},threads={threads},sockets={host.Sockets}";

        // ── configure_ram
        var memory = m.MemoryMiB ?? 1024L * host.MemoryGB switch { >= 128 => 32, >= 64 => 16, >= 16 => 8, >= 8 => 4, _ => 2 };
        var minimumGiB = isWindows ? 4 : os == "macos" ? 8 : 0;
        if (minimumGiB > 0 && memory / 1024 < minimumGiB)
        {
            var what = isWindows ? "Windows" : "macOS";
            if (m.MemoryMiB is null) errors.Add($"This computer cannot spare the {minimumGiB} GB of memory {what} needs.");
            else warnings.Add($"{what} needs at least {minimumGiB} GB of memory; this machine has less and may not install or run well.");
        }

        // ── configure_bios
        var uefi = os == "macos" || m.Firmware.Kind == FirmwareKind.Uefi;
        string? efiCode = null;
        var efiVars = File(m.Firmware.VarsFile);
        (string, string)? varsToCreate = null;
        if (uefi)
        {
            if (m.Firmware.CodeFile is { } own) efiCode = File(own);
            else if (FirmwareLocator.Find(host.FirmwareFiles, m.Firmware.SecureBoot, ctx.Share) is { } pair)
            {
                efiCode = pair.Code;
                if (!ctx.FileExists(efiVars)) varsToCreate = (pair.VarsTemplate, efiVars);
            }
            else
            {
                errors.Add(m.Firmware.SecureBoot
                    ? "Secure Boot was asked for, but this computer has no Secure Boot firmware for virtual machines (OVMF). Install your distribution's OVMF/edk2 package, or turn Secure Boot off."
                    : "This machine starts with UEFI firmware, but this computer has no OVMF firmware installed. Install your distribution's OVMF/edk2 package.");
            }
        }
        else
        {
            machineType = "pc";
        }

        // ── configure_os_quirks
        var netDevice = os switch
        {
            "batocera" or "haiku" or "kolibrios" => "rtl8139",
            "windows-server" => "e1000",
            "freedos" => "pcnet",
            "reactos" => "e1000",
            "macos" => macVirtio ? "virtio-net-pci" : "vmxnet3",
            _ when isBsd || os.StartsWith("linux", StringComparison.Ordinal) || os == "windows" || os.EndsWith("solaris", StringComparison.Ordinal) => "virtio-net-pci",
            _ => "rtl8139",
        };
        var mouse = m.Input.Mouse ?? (os is "freebsd" or "ghostbsd" ? "usb" : "tablet");
        var keyboard = m.Input.Keyboard ?? (os == "reactos" ? "ps2" : "usb");
        var sound = m.Audio.Device ?? os switch
        {
            "freedos" => "sb16",
            _ when os.EndsWith("solaris", StringComparison.Ordinal) => "ac97",
            "macos" when macModern => "virtio-sound-pci",
            "macos" when release == "big-sur" => "ich9-intel-hda",
            _ => "intel-hda",
        };
        var usbController = m.Usb.Controller ?? (os.EndsWith("solaris", StringComparison.Ordinal) || (os == "macos" && macVirtio) ? "xhci" : "ehci");
        var passthroughController = os == "macos" ? (macVirtio ? "nec-usb-xhci" : "usb-ehci") : "qemu-xhci";
        if (os == "macos" && macVirtio) tweaks.AddRange(["-global", "nec-usb-xhci.msi=off"]);
        var balloon = m.Devices.Balloon ?? (os != "macos" || macVirtio);
        var macDisk = macVirtio || release == "catalina" ? "virtio-blk-pci" : "ide-hd,bus=ahci.2";

        // ── configure_display
        var backend = ctx.Headless ? DisplayBackend.None : m.Display.Backend;
        var spiceLike = backend is DisplayBackend.Spice or DisplayBackend.SpiceApp or DisplayBackend.None;
        var gl = !ctx.Headless && backend != DisplayBackend.None && (m.Display.Gl ?? host.RenderNode is not null);
        var device = m.Display.Device ?? os switch
        {
            _ when isBsd => "VGA",
            "linux_old" or "solaris" => "vmware-svga",
            "linux" => spiceLike ? "virtio-gpu" : "virtio-vga",
            "macos" => "vmware-svga",
            "windows" or "windows-server" => backend is DisplayBackend.Spice or DisplayBackend.None ? "qxl-vga" : "virtio-vga",
            _ => "qxl-vga",
        };
        if (gl && device is "virtio-vga" or "virtio-gpu" or "virtio-gpu-pci")
        {
            var glDevice = device switch { "virtio-gpu-pci" => "virtio-gpu-gl-pci", "virtio-gpu" => "virtio-gpu-gl", _ => "virtio-vga-gl" };
            if (host.QemuDevices.Contains(glDevice)) device = glDevice;
        }

        var displayRender = backend switch
        {
            DisplayBackend.Gtk => $"gtk,grab-on-hover=on,zoom-to-fit=off,gl={OnOff(gl)}",
            DisplayBackend.Sdl => $"sdl,gl={OnOff(gl)}",
            DisplayBackend.SpiceApp => $"spice-app,gl={OnOff(gl)}",
            DisplayBackend.Spice when gl => $"egl-headless,rendernode={host.RenderNode ?? "/dev/dri/renderD128"}",
            _ => "none",
        };
        var video = new StringBuilder(device);
        if (device is "virtio-vga" or "virtio-vga-gl" or "virtio-gpu" or "virtio-gpu-gl" or "virtio-gpu-pci" or "virtio-gpu-gl-pci" or "qxl" or "qxl-vga" or "bochs-display")
            video.Append(CultureInfo.InvariantCulture, $",xres={(m.Display.Width is not null && m.Display.Height is not null ? m.Display.Width : 1280)},yres={(m.Display.Width is not null && m.Display.Height is not null ? m.Display.Height : 800)}");
        video.Append(device switch
        {
            "bochs-display" => ",vgamem=67108864",
            "qxl" or "qxl-vga" => ",ram_size=65536,vram_size=65536,vgamem_mb=64",
            "ati-vga" or "cirrus-vga" or "VGA" or "vmware-svga" => ",vgamem_mb=256",
            _ => "",
        });

        // ── configure_audio (a headless launch has no sound at all: tests are silent by construction)
        var audiodev = ctx.Headless ? "none,id=audio0" : spiceLike ? "spice,id=audio0" : $"{ctx.HostAudio},id=audio0";
        var soundArgs = sound switch
        {
            "ich9-intel-hda" or "intel-hda" => new[] { "-device", sound, "-device", "hda-micro,audiodev=audio0" },
            "none" => [],
            _ => ["-device", $"{sound},audiodev=audio0"],
        };

        // ── configure_ports
        var nic = m.Nics.FirstOrDefault() ?? new NicConfig { Mode = NetMode.None };
        var net = new StringBuilder($"user,hostname={Slug(m.Name)}");
        if (nic.SshForward) net.Append(CultureInfo.InvariantCulture, $",hostfwd=tcp::{nic.SshHostPort ?? ctx.SshHostPort}-:22");
        foreach (var f in nic.Forwards) net.Append(CultureInfo.InvariantCulture, $",hostfwd={f.Protocol}::{f.HostPort}-:{f.GuestPort}");
        var spice = backend == DisplayBackend.SpiceApp ? $"disable-ticketing=on,gl={OnOff(gl)}" : $"disable-ticketing=on,unix=on,addr={ctx.Runtime.Spice}";

        // ── vm_boot
        args.AddRange(["-name", $"{EscapeOption(m.Name)},process={Slug(m.Name)},debug-threads=on"]);
        if (kvm) args.AddRange(["-machine", $"{machineType},smm={smm},vmport=off,accel=kvm"]);
        else args.AddRange(["-accel", $"tcg,tb-size={(host.MemoryGB >= 16 ? 512 : 256)},thread=multi", "-machine", $"{machineType},smm={smm},vmport=off"]);
        args.AddRange(tweaks);
        args.AddRange(["-cpu", cpu.ToString(), "-smp", smp, "-m", MemoryArg(memory)]);
        // Shared folders (virtiofs) need the machine's memory shared with virtiofsd.
        var shares = new List<VirtiofsdLaunch>();
        if (m.Shares.Count > 0)
        {
            if (ctx.Virtiofsd is null) errors.Add("Shared folders need virtiofsd, which is not installed. The host check names its package.");
            args.AddRange(["-object", $"memory-backend-memfd,id=mem,size={MemoryArg(memory)},share=on", "-numa", "node,memdev=mem"]);
        }
        if (balloon) args.AddRange(["-device", "virtio-balloon"]);
        args.AddRange(["-pidfile", ctx.Runtime.Pid]);
        args.AddRange(os is "windows" or "windows-server" or "reactos" or "freedos"
            ? ["-rtc", "base=localtime,clock=host,driftfix=slew"]
            : ["-rtc", "base=utc,clock=host"]);
        if (spiceLike) args.AddRange(["-vga", "none"]);
        args.AddRange(["-device", video.ToString(), "-display", displayRender]);
        if (spiceLike && !ctx.Headless)
        {
            args.AddRange(["-spice", spice]);
            args.AddRange(["-device", "virtio-serial-pci",
                "-chardev", $"socket,id=agent0,path={ctx.Runtime.Agent},server=on,wait=off",
                "-device", "virtserialport,chardev=agent0,name=org.qemu.guest_agent.0",
                "-chardev", "spicevmc,id=vdagent0,name=vdagent",
                "-device", "virtserialport,chardev=vdagent0,name=com.redhat.spice.0",
                "-chardev", "spiceport,id=webdav0,name=org.spice-space.webdav.0",
                "-device", "virtserialport,chardev=webdav0,name=org.spice-space.webdav.0"]);
        }
        else if (ctx.Headless)
        {
            // The guest agent still answers in a headless launch (IP address, clean shutdown).
            args.AddRange(["-device", "virtio-serial-pci",
                "-chardev", $"socket,id=agent0,path={ctx.Runtime.Agent},server=on,wait=off",
                "-device", "virtserialport,chardev=agent0,name=org.qemu.guest_agent.0"]);
        }

        if (m.Devices.Rng ?? true) args.AddRange(["-device", "virtio-rng-pci,rng=rng0", "-object", "rng-random,id=rng0,filename=/dev/urandom"]);
        if (!ctx.Headless)
        {
            args.AddRange(["-device", $"{passthroughController},id=spicepass"]);
            for (var i = 1; i <= 3; i++)
                args.AddRange(["-chardev", $"spicevmc,id=usbredirchardev{i},name=usbredir", "-device", $"usb-redir,chardev=usbredirchardev{i},id=usbredirdev{i}"]);
            args.AddRange(["-device", "pci-ohci,id=smartpass", "-device", "usb-ccid"]);
            if (host.QemuDevices.Contains("ccid-card-passthru")) args.AddRange(["-chardev", "spicevmc,id=ccid,name=smartcard", "-device", "ccid-card-passthru,chardev=ccid"]);
        }

        switch (usbController)
        {
            case "ehci": args.AddRange(["-device", "usb-ehci,id=input"]); break;
            case "xhci": args.AddRange(["-device", "qemu-xhci,id=input"]); break;
        }

        switch (keyboard)
        {
            case "usb": args.AddRange(["-device", "usb-kbd,bus=input.0"]); break;
            case "virtio": args.AddRange(["-device", "virtio-keyboard"]); break;
        }

        args.AddRange(["-k", m.Input.Layout]);
        switch (mouse)
        {
            case "usb": args.AddRange(["-device", "usb-mouse,bus=input.0"]); break;
            case "tablet": args.AddRange(["-device", "usb-tablet,bus=input.0"]); break;
            case "virtio": args.AddRange(["-device", "virtio-mouse"]); break;
        }

        args.AddRange(["-audiodev", audiodev]);
        args.AddRange(soundArgs);

        var nicModel = nic.Model ?? netDevice;
        switch (nic.Mode)
        {
            case NetMode.None:
                args.AddRange(["-nic", "none"]);
                break;
            case NetMode.Restricted:
                args.AddRange(["-device", $"{nicModel},netdev=nic", "-netdev", $"{net},restrict=y,id=nic"]);
                break;
            case NetMode.Segment:
            {
                // Machines on one segment share a multicast group on this computer only; each needs its own MAC.
                var segment = nic.Segment ?? "default";
                var port = 20000 + (int)(Fnv(segment) % 20000);
                var segmentMac = nic.Mac ?? MacFor(m.Id);
                args.AddRange(["-device", $"{nicModel},netdev=nic,mac={segmentMac}", "-netdev", $"socket,id=nic,mcast=230.0.0.1:{port},localaddr=127.0.0.1"]);
                break;
            }

            case NetMode.Bridge:
            {
                // On the real network each machine needs its own MAC, or two machines (a clone and its original) clash on
                // QEMU's one default; a quickemu machine keeps quickemu's own command line.
                var bridgeMac = nic.Mac ?? (m.ImportedFrom is null ? MacFor(m.Id) : null);
                args.AddRange(["-nic", $"bridge,br={nic.Bridge},model={nicModel}{(bridgeMac is { } mac ? ",mac=" + mac : "")}"]);
                break;
            }
            default:
                args.AddRange(["-device", $"{nicModel},netdev=nic", "-netdev", $"{net},id=nic"]);
                break;
        }

        if (uefi && efiCode is not null)
        {
            args.AddRange(["-global", "driver=cfi.pflash01,property=secure,value=on",
                "-drive", $"if=pflash,format={ctx.FirmwareFormat(efiCode)},unit=0,file={efiCode},readonly=on",
                "-drive", $"if=pflash,format={(varsToCreate is { } v ? ctx.FirmwareFormat(v.Item1) : ctx.FirmwareFormat(efiVars))},unit=1,file={efiVars}"]);
        }

        // Media: an installer marked "until installed" is in the drive only while the system disk is unused.
        bool Attached(MediaConfig media) => media.Connect switch
        {
            Connect.Never => false,
            Connect.UntilInstalled => !ctx.SystemDiskUsed || os == "kolibrios",
            _ => true,
        };
        var installer = m.Media.FirstOrDefault(x => x.Role == MediaRole.Installer && Attached(x));
        var installerPath = installer is null ? null : File(installer.File);
        if (installerPath is not null && os == "freedos") args.AddRange(["-boot", "order=dc"]);
        else if (installerPath is not null && os == "kolibrios") { args.AddRange(["-drive", $"media=cdrom,index=2,file={installerPath}"]); installerPath = null; }
        else if (installerPath is not null && os == "reactos") { args.AddRange(["-boot", "order=d", "-drive", $"if=ide,index=2,media=cdrom,file={installerPath}"]); installerPath = null; }
        else if (installerPath is not null && os == "windows"
                 && m.Media.FirstOrDefault(x => x.Role == MediaRole.Unattended) is { } answers && ctx.FileExists(File(answers.File)))
            args.AddRange(["-drive", $"media=cdrom,index=2,file={File(answers.File)}"]);
        else if (os != "windows" && m.Media.FirstOrDefault(x => x.Role == MediaRole.Unattended && Attached(x)) is { } linuxAnswers)
            // A Linux answer-file disc (Ubuntu's cidata seed, a kickstart volume) rides beside the installer while it installs.
            args.AddRange(["-drive", $"media=cdrom,index=2,file={File(linuxAnswers.File)}"]);

        foreach (var floppy in m.Media.Where(x => x.Role == MediaRole.Floppy && Attached(x)))
            args.AddRange(["-drive", $"if=floppy,format=raw,file={File(floppy.File)}"]);
        if (installerPath is not null) args.AddRange(["-drive", $"media=cdrom,index=0,file={installerPath}"]);
        if (m.Media.FirstOrDefault(x => x.Role == MediaRole.Drivers && Attached(x)) is { } drivers)
            args.AddRange(["-drive", $"media=cdrom,index=1,file={File(drivers.File)}"]);

        const string optimisations = "discard=unmap,detect-zeroes=unmap,cache=writeback,aio=threads";
        if (m.Disks.Count == 0) errors.Add("This machine has no disk.");
        else
        {
            var disk = m.Disks[0];
            var diskPath = File(disk.File);
            var device0 = disk.Bus switch { DiskBus.Virtio => "virtio-blk-pci", DiskBus.Sata or DiskBus.Ide => "ide-hd", _ => null };
            if (os == "macos")
            {
                var loader = m.Media.FirstOrDefault(x => x.Role == MediaRole.Bootloader);
                if (loader is null) errors.Add("This macOS machine has no OpenCore boot loader.");
                args.AddRange(["-device", "ahci,id=ahci", "-device", "ide-hd,bus=ahci.0,drive=BootLoader,bootindex=0",
                    "-drive", $"id=BootLoader,if=none,format=qcow2,file={(loader is null ? "" : File(loader.File))}"]);
                if (m.Media.FirstOrDefault(x => x.Role == MediaRole.Recovery && Attached(x)) is { } recovery)
                    args.AddRange(["-device", "ide-hd,bus=ahci.1,drive=RecoveryImage", "-drive", $"id=RecoveryImage,if=none,format=raw,file={File(recovery.File)}"]);
                args.AddRange(["-device", $"{device0 ?? macDisk},drive=SystemDisk", "-drive", $"id=SystemDisk,if=none,format=qcow2,file={diskPath},{optimisations}"]);
            }
            else if (os == "kolibrios")
                args.AddRange(["-device", "ahci,id=ahci", "-device", "ide-hd,bus=ahci.0,drive=SystemDisk", "-drive", $"id=SystemDisk,if=none,format=qcow2,file={diskPath},{optimisations}"]);
            else if (os == "reactos" && device0 is null)
                args.AddRange(["-drive", $"if=ide,index=0,media=disk,file={diskPath}"]);
            else if (os == "windows-server" && device0 is null)
                args.AddRange(["-device", "ide-hd,drive=SystemDisk", "-drive", $"id=SystemDisk,if=none,format=qcow2,file={diskPath},{optimisations}"]);
            else
                args.AddRange(["-device", $"{device0 ?? "virtio-blk-pci"},drive=SystemDisk", "-drive", $"id=SystemDisk,if=none,format={disk.Format},file={diskPath},{optimisations}"]);

            for (var i = 1; i < m.Disks.Count; i++)
            {
                var extra = m.Disks[i];
                var dev = extra.Bus is DiskBus.Sata or DiskBus.Ide ? "ide-hd" : "virtio-blk-pci";
                args.AddRange(["-device", $"{dev},drive=Disk{i}", "-drive", $"id=Disk{i},if=none,format={extra.Format},file={File(extra.File)},{optimisations}"]);
            }
        }

        if (m.Usb.Passthrough.Count > 0)
        {
            args.AddRange(["-device", $"{passthroughController},id=hostpass"]);
            foreach (var u in m.Usb.Passthrough)
                args.AddRange(["-device", $"usb-host,bus=hostpass.0,vendorid=0x{u.VendorId},productid=0x{u.ProductId},id={Devices.UsbHost.RuleId(u.VendorId, u.ProductId)}"]);
        }

        for (var i = 0; i < m.Shares.Count && ctx.Virtiofsd is { } virtiofsd; i++)
        {
            var share = m.Shares[i];
            var socket = ctx.Runtime.Share(i);
            var fsArgs = new List<string> { $"--socket-path={socket}", $"--shared-dir={share.Path}", "--cache=auto", "--sandbox=namespace" };
            if (share.ReadOnly) fsArgs.Add("--readonly");
            shares.Add(new VirtiofsdLaunch([virtiofsd, .. fsArgs], socket));
            args.AddRange(["-chardev", $"socket,id=fs{i},path={socket}", "-device", $"vhost-user-fs-pci,chardev=fs{i},tag={share.Tag}"]);
        }

        SwtpmLaunch? swtpm = null;
        if (m.Tpm)
        {
            // An imported quickemu machine keeps its TPM state beside its disk, where quickemu wrote it.
            var state = m.ImportedFrom?.Kind == "quickemu" ? ctx.MachineDirectory : Path.Combine(ctx.MachineDirectory, "tpm");
            swtpm = new SwtpmLaunch(["socket", "--ctrl", $"type=unixio,path={ctx.Runtime.Tpm}", "--terminate", "--tpmstate", $"dir={state}", "--tpm2"], ctx.Runtime.Tpm, state);
            args.AddRange(["-chardev", $"socket,id=chrtpm,path={ctx.Runtime.Tpm}", "-tpmdev", "emulator,id=tpm0,chardev=chrtpm", "-device", "tpm-tis,tpmdev=tpm0"]);
        }

        var boot = new List<string>();
        if (m.Devices.BootOrder is { Length: > 0 } order && !uefi) boot.Add($"order={order}");
        if (m.Devices.BootMenu || ctx.FirmwareMenu) boot.Add("menu=on,splash-time=5000");
        if (boot.Count > 0) args.AddRange(["-boot", string.Join(',', boot)]);
        if (m.Devices.Watchdog is { } watchdogAction) args.AddRange(["-device", "i6300esb", "-action", $"watchdog={watchdogAction}"]);
        if (m.Devices.CrashReporting) args.AddRange(["-device", "pvpanic"]);

        if (m.KernelBoot is { } kb && kb.Connect switch { Connect.Never => false, Connect.UntilInstalled => !ctx.SystemDiskUsed, _ => true })
        {
            args.AddRange(["-kernel", File(kb.Kernel)]);
            if (kb.Initrd is { } initrd) args.AddRange(["-initrd", File(initrd)]);
            if (kb.Append.Length > 0) args.AddRange(["-append", kb.Append]);
        }

        // VM Qontrol talks to the machine over QMP only; the human monitor is off.
        args.AddRange(["-monitor", "none", "-qmp", $"unix:{ctx.Runtime.Qmp},server=on,wait=off", "-qmp", $"unix:{ctx.Runtime.Control},server=on,wait=off"]);
        var serial = m.Serial ?? os is not ("macos" or "windows" or "windows-server");
        args.AddRange(serial ? ["-serial", $"unix:{ctx.Runtime.Serial},server=on,wait=off"] : ["-serial", "none"]);
        args.AddRange(m.ExtraArgs);

        return new QemuLaunch
        {
            Args = args,
            Swtpm = swtpm,
            Shares = shares,
            VarsToCreate = varsToCreate,
            Errors = errors,
            Warnings = warnings,
            Cores = cores,
            MemoryMiB = memory,
        };
    }

    private static void MacCpuFlags(CpuFlagList cpu, HostCapabilities host, string release, List<string> errors)
    {
        var notIntel = !host.IsIntel;
        switch (release)
        {
            case "ventura" or "sonoma" or "sequoia" or "tahoe":
                if (host.HasFlag("sse4_2") && host.HasFlag("avx2"))
                {
                    if (notIntel) { cpu.Add("+avx2"); cpu.Add("+sse4.2"); }
                }
                else errors.Add($"macOS {release} needs a processor with SSE 4.2 and AVX2. Choose macOS Monterey or Big Sur instead.");
                break;
            case "catalina" or "big-sur" or "monterey":
                if (host.HasFlag("sse4_2")) { if (notIntel) cpu.Add("+sse4.2"); }
                else errors.Add($"macOS {release} needs a processor with SSE 4.2.");
                break;
            default:
                if (host.HasFlag("sse4_1")) { if (notIntel) cpu.Add("+sse4.1"); }
                else errors.Add($"macOS {release} needs a processor with SSE 4.1.");
                break;
        }

        if (!notIntel) return;
        foreach (var flag in MacFlagsForNonIntel)
        {
            if (host.HasFlag(flag)) cpu.Add("+" + flag.Replace('_', '-'));
            else if (host.IsAmd && flag == "pcid") cpu.Add("-pcid");
        }

        if (host.IsAmd && host.HasFlag("invtsc"))
        {
            cpu.Add("+tsc");
            cpu.Add("+tsc-deadline");
        }
    }

    private static string OnOff(bool b) => b ? "on" : "off";

    private static uint Fnv(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        return hash;
    }

    /// <summary>A stable MAC for a machine (from its id), in QEMU's 52:54:00 block, so machines on one segment never clash.</summary>
    public static string MacFor(string machineId)
    {
        var h = Fnv(machineId);
        return $"52:54:00:{(h >> 16) & 0xFF:x2}:{(h >> 8) & 0xFF:x2}:{h & 0xFF:x2}";
    }

    /// <summary>"16G" when the size is whole gigabytes (as quickemu writes it), otherwise megabytes.</summary>
    public static string MemoryArg(long mib) => mib % 1024 == 0 ? $"{mib / 1024}G" : $"{mib}M";

    /// <summary>A name made safe for process names and host names: lowercase letters, digits and dashes.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "vm" : slug;
    }

    /// <summary>QEMU option values escape a comma by doubling it.</summary>
    private static string EscapeOption(string value) => value.Replace(",", ",,", StringComparison.Ordinal);

    private static string Rot13(string s) => new(s.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    }).ToArray());
}
