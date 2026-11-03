# Getting started

## 1. Check this computer

The welcome page lists what VM Qontrol needs and what is optional, with the command that installs each missing piece
on your distribution. `vmq doctor` prints the same list in a terminal. Your account needs access to `/dev/kvm`.

## 2. Make a machine

**Create a virtual machine** opens the wizard:

- **Your own disc**: pick an `.iso`; the system on it is recognised (Windows editions and builds, the common Linux
  distributions).
- **Download**: Windows 10 and 11 come from Microsoft's own catalogue; macOS and other systems come from the quickget
  catalogue (needs `quickemu` installed).
- **Install later**: an empty machine; add a disc in Settings when you are ready.

For Windows and the major Linux distributions, **Easy Install** answers the installer for you: type a user name and
password and the machine installs itself. A card on the machine's page shows its progress and says when it is done.

## 3. Use it

- **Open screen** shows the machine in a viewer window (remote-viewer or spicy).
- **Settings** changes the hardware. Every setting says what it does, what is recommended, and whether it applies now
  or at the next power on.
- **Snapshots**, **Clone…**, **Move to…** and **Export** are on the machine's page and in its ⋯ menu.
- **Ctrl+K** opens a palette of every command.

## macOS

A macOS machine starts in OpenCore's boot picker. The first time, it boots Apple's recovery image: choose **Reinstall
macOS**, then pick the empty disk (erase it with Disk Utility first if it is not listed). The install restarts several
times; the disk shows as **macOS Installer** until the last stage is done, then by the name you gave it. Leave it be
between restarts: the right disk is chosen by itself.

macOS has no graphics driver for any virtual graphics card, so its interface is drawn by the processors and feels
slower than other systems. In macOS, System Settings → Accessibility → Display → **Reduce transparency** and **Reduce
motion** help most; the first hour after setup is slowest while macOS indexes the disk.
