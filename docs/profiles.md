# Guest profiles and the QEMU command line

VM Qontrol builds each machine's QEMU command with quickemu 4.9.9's rules, so a machine behaves as it would under
quickemu: the same CPU model and flags per guest (Hyper-V enlightenments for Windows, the macOS CPU and SMC setup),
the same chipset, display adapter, sound card, network adapter, USB controllers and firmware.

The test suite holds this to quickemu itself. `tools/record-oracle.sh` runs the real quickemu on 20 reference
configurations (every guest family and the main options) with a stand-in QEMU that only records its arguments, and
`QuickemuParityTests` requires the same command, argument for argument, from VM Qontrol for the same host.

## Where VM Qontrol differs on purpose

| What | quickemu | VM Qontrol | Why |
|------|----------|------------|-----|
| Machine control | Human monitor on a socket | `-monitor none` and QMP on a socket | QMP is QEMU's machine interface: power, media, USB, snapshots and events without parsing text. |
| Sockets and process id | Beside the disk | The user's runtime folder (`$XDG_RUNTIME_DIR/vmqontrol/<id>`) | A socket path may not exceed 107 bytes, and machine folders on long paths would break. |
| Serial port | Socket beside the disk, or off for Windows and macOS | Socket in the runtime folder, same on/off rule | Same reason; it feeds the console tab. |
| Display backend | GTK window by default | SPICE and an external viewer by default | One viewer for every machine, with clipboard, USB redirection and resizing. |
| 3D acceleration | On unless `glxinfo` reports an old GPU | On when the host has a GPU render node | Without a render node QEMU cannot start with GL at all. |
| Headless launch | Not offered | No screen, no SPICE, `-audiodev none` | Background work and every test run, which must never make a sound. |
| Imported machines | — | Read in place, never written | A quickemu machine keeps working with quickemu. |
| Windows 11 Secure Boot | Off (`secureboot="off"`) | On for new Windows 11 machines when the host has a Secure Boot firmware build | Setup on current Windows 11 discs (build 26300) stops with "The PC must support Secure Boot"; the LabConfig bypass no longer gets past it. Imported machines keep their setting. |
| USB devices at power on | `usb-host` with no id | The same device with `id=usbauto-VENDOR-PRODUCT` | The id lets the USB panel see the rule in the running machine and take it out live. |
| Controller for handed-over USB devices | Only when `usb_devices` is set | Always on VM Qontrol's own machines (`qemu-xhci,id=hostpass`) | Devices connected while running go on it: a USB 2 (EHCI) bus refuses the full-speed mice, keyboards and receivers most people pass, and QEMU then resets them over and over. Imported quickemu machines keep quickemu's line. |
| Firmware flash "secure" | `-global driver=cfi.pflash01,property=secure,value=on` on every UEFI machine | Only when SMM is on (Windows, FreeDOS, Linux with Secure Boot) | A secure flash takes writes only from SMM. With SMM off (macOS, Linux and BSD without Secure Boot) it refused every NVRAM write: the startup disk and the macOS installer's own state were lost at each restart, so an install fell back to Recovery, and Linux boot entries vanished. Measured: the variables file never changed with it, and changed at once without it. |

## Known quickemu behaviour kept for parity

- An odd number of cores on an SMT host loses one (5 cores → 2 cores × 2 threads). Kept so an imported machine
  starts with the same topology; new VM Qontrol machines will offer even counts.
- The installer disc is attached only while the system disk is unused (smaller than 1.5 MB), as quickemu decides.
- Firmware discovery takes the last matching pair in quickemu's list. Alpine's `/usr/share/OVMF/OVMF_CODE.fd` is
  included; Alpine has no Secure Boot build.
