#!/usr/bin/env bash
# The tray icon, proven on a real app run and never on the desktop: Xvfb + an ISOLATED bus with a stand-in tray host
# (watcher.py) that records the StatusNotifierItem the app registers, reads its title and dbusmenu layout as a panel
# does, and clicks "Quit VM Qontrol"; the app must then end by itself. Report in scratch/tray-proof/report.txt.
#   tools/tray-proof/prove.sh            (after a Release build of src/VmQontrol.App)
set -uo pipefail
cd "$(dirname "$0")/../.."
app="$PWD/src/VmQontrol.App/bin/Release/net10.0/vmqontrol"
[ -x "$app" ] || { echo "tray-proof: build Release first"; exit 2; }
out="$PWD/scratch/tray-proof"
rm -rf "$out"; mkdir -p "$out/home/config" "$out/home/VMs/Web"
printf '{ "trayIcon": true }' > "$out/home/config/settings.json"
printf '{ "name": "Web", "disks": [ { "file": "disk0.qcow2", "sizeGiB": 1 } ] }' > "$out/home/VMs/Web/vm.json"
: > "$out/home/VMs/Web/disk0.qcow2"
cp tools/tray-proof/watcher.py tools/tray-proof/run.sh "$out/"
export VMQONTROL_HOME="$out/home" PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq VMQONTROL_WAYLAND=0
timeout -k 5 60 env -u WAYLAND_DISPLAY -u WAYLAND_SOCKET GDK_BACKEND=x11 xvfb-run -a \
    dbus-run-session --config-file="$PWD/packaging/isolated-bus.conf" "$out/run.sh" "$app" < /dev/null
cat "$out/report.txt"
grep -q "^Quit VM Qontrol" "$out/report.txt" && grep -q "^app exit 0" "$out/report.txt" && ! grep -q "STILL RUNNING" "$out/report.txt" \
    && echo "TRAY PROOF PASSED" || { echo "TRAY PROOF FAILED"; exit 1; }
