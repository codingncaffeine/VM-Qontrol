VM Qontrol
==========

Create, run and fine-tune QEMU/KVM virtual machines.

Run it
  ./vmqontrol          the app
  ./vmq help           the command line (vmq doctor checks this computer)

What it needs from your distribution
  VM Qontrol brings .NET with it; QEMU and its helpers come from your distribution.
  "vmq doctor" (or the app's welcome page) lists what is missing with the command that installs it.

  Arch:           qemu-desktop edk2-ovmf swtpm virt-viewer
  Debian/Ubuntu:  qemu-system-x86 qemu-utils ovmf swtpm-tools virt-viewer
  Fedora:         qemu-kvm edk2-ovmf swtpm virt-viewer
  Alpine:         qemu-system-x86_64 qemu-img ovmf swtpm virt-viewer

Licence: GPL-3.0-or-later (LICENSE).
