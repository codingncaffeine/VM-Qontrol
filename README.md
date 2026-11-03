<p align="center"><img src="assets/branding/banner.jpg" alt="VM Qontrol" /></p>

# VM Qontrol

A modern, intuitive manager for QEMU/KVM virtual machines on Linux. Create a machine in a few guided steps, download
an operating system from a catalogue or use your own installer, and change every setting in a dialog that explains
what it does and what the recommended value is. No daemon, no root, and one portable folder per machine.

> **Status: early development.** Nothing is released yet. The first release will run machines in an external viewer
> window; a console inside the app comes later.

## Planned for the first release

- A library of machines with live thumbnails, performance graphs and a serial console.
- A New Virtual Machine wizard: your own ISO, or a download: Windows 10 and 11 straight from Microsoft's own catalogue
  (the one its Media Creation Tool uses, checked against Microsoft's checksums and made into an installer disc), other
  systems from the quickget catalogue. The operating system is detected from the media, with unattended installation
  for Windows and the major Linux distributions. "Lean Windows" leaves out the bundled promotions, games and ads while
  installing Microsoft's own, untouched Windows.
- A settings dialog covering the whole machine: memory, processors, disks, CD/DVD, network, USB, sound, display,
  TPM, firmware and boot options. Each setting shows when it takes effect.
- Snapshots taken while a machine runs, full and linked clones, templates, suspend and resume.
- USB passthrough, host drives (a whole disk, a partition or a CD/DVD drive, read-only unless you allow writing),
  host serial ports, shared folders, clipboard sharing, NAT with port forwarding, bridged networking and private
  networks between machines with their own addresses, in a virtual network editor.
- PCI passthrough with CPU pinning and hugepages for gaming and GPU workloads.
- Import from quickemu, VMware, VirtualBox, OVA and libvirt.
- A host check that names anything missing and the command that installs it on your distribution.

## Requirements (Linux)

| Need | Arch | Debian / Ubuntu | Fedora |
|------|------|-----------------|--------|
| QEMU | `qemu-desktop` | `qemu-system-x86 qemu-utils` | `qemu-system-x86 qemu-img` |
| UEFI firmware | `edk2-ovmf` | `ovmf` | `edk2-ovmf` |
| TPM (Windows 11) | `swtpm` | `swtpm swtpm-tools` | `swtpm swtpm-tools` |
| Viewer | `spice-gtk` or `virt-viewer` | `spice-client-gtk` or `virt-viewer` | `spice-gtk-tools` or `virt-viewer` |
| OS detection | `osinfo-db` | `osinfo-db` | `osinfo-db` |
| Shared folders (optional) | `virtiofsd` | `virtiofsd` | `virtiofsd` |
| Faster user networking (optional) | `passt` | `passt` | `passt` |
| Windows downloads (optional) | `wimlib libisoburn` | `wimtools xorriso libarchive-tools` | `wimlib-utils xorriso bsdtar` |
| OS download catalogue (optional) | `quickemu` | `quickemu` | `quickemu` |

**Alpine and other musl distributions** are supported for building from source:
`apk add dotnet10-sdk qemu-system-x86_64 qemu-img ovmf swtpm virt-viewer osinfo-db` (plus `virtiofsd passt` if
wanted, and `wimlib xorriso libarchive-tools` for Windows downloads). Alpine's UEFI firmware has no Secure Boot build, and quickget is not packaged there, so the download
catalogue is unavailable unless you install quickemu yourself.

Your user needs access to `/dev/kvm`, through the `kvm` group or your distribution's default rule.

## Building

```bash
dotnet build VmQontrol.slnx -c Release
src/VmQontrol.App/bin/Release/net10.0/vmqontrol
```

.NET 10 SDK required (on Alpine, `dotnet10-sdk`). `VMQONTROL_HOME=DIR` keeps every setting, log and machine under DIR, which is useful for trying
a build without touching your real machines.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
