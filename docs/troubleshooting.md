# Troubleshooting

**The mouse is stuck inside the machine's window.** Press **Ctrl+Alt**. The viewer holds the mouse while the system
inside cannot use the tablet pointer yet (firmware, boot pickers, installers); once the system has started and loaded
its mouse driver, the mouse moves freely in and out. While a viewer can hold it, VM Qontrol's status bar says so.

**A machine will not start and names another process.** Another machine (or program) has one of its disks open; the
message names it. Shut that one down, then power on again.

**A machine will not start: "This machine cannot start yet".** Its page lists why in words (a missing disc, no disk,
memory). Power on asks before starting a machine when this computer is short of memory.

**Windows asks for a key / does not install by itself.** Easy Install needs the edition's own key for some discs; the
wizard says when. Without Easy Install, answer Windows Setup in the viewer as on a real computer.

**A setting did not change anything.** Settings marked "Applies at next power on" take effect after the machine is
shut down and started again (a restart inside the system is not enough).

**Something else.** Each machine's page has a **Log** tab; the app's own log is in `~/.local/state/vmqontrol/logs/`.
`vmq doctor` checks this computer again.
