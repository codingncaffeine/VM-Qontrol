#!/usr/bin/env bash
# The built binaries, run the way a user runs them, in a throwaway profile and nowhere near the desktop:
# vmq answers, and the window opens under Xvfb on an isolated D-Bus, stays up, and closes on SIGTERM.
#   tools/smoke.sh            (after a Release build)
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
source tools/env.sh
bin="$root/src"
cli="$bin/VmQontrol.Cli/bin/Release/net10.0/vmq"
app="$bin/VmQontrol.App/bin/Release/net10.0/vmqontrol"
for f in "$cli" "$app"; do [ -x "$f" ] || { echo "smoke: $f is missing; build Release first"; exit 2; }; done

out="$root/scratch/smoke"
rm -rf "$out"
mkdir -p "$out/home"
export VMQONTROL_HOME="$out/home"
export PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq
unset WAYLAND_DISPLAY WAYLAND_SOCKET
export VMQONTROL_WAYLAND=0
runtime_before=$(ls -d "${XDG_RUNTIME_DIR:-/tmp}"/vmqontrol-* 2>/dev/null)
# The isolated profile's runtime folder is outside scratch (socket paths must stay short); remove it if left empty.
tidy() { for d in "${XDG_RUNTIME_DIR:-/tmp}"/vmqontrol-*/; do d=${d%/}; [ -d "$d" ] && ! grep -qxF "$d" <<< "$runtime_before" && rmdir "$d" 2>/dev/null; done; true; }
trap tidy EXIT
fail() { echo "SMOKE FAILED: $*"; [ -n "${apid:-}" ] && kill -TERM "$apid" 2>/dev/null; exit 1; }

"$cli" version | head -1 | grep -q '^vmq ' || fail "vmq version"
echo "smoke: vmq answers"

# A whole machine life through vmq: headless and silent, 256 MB, BIOS, a blank disk (it boots to the firmware screen).
"$cli" create smoke-vm --bios --memory-mib 256 --cores 1 > /dev/null || fail "vmq create"
qemu-img create -q -f qcow2 "$VMQONTROL_HOME/VMs/smoke-vm/disk0.qcow2" 1G || fail "qemu-img create"
"$cli" start smoke-vm --headless | grep -q "is running" || fail "vmq start"
"$cli" status smoke-vm | grep -q ": running" || fail "vmq status did not say running"
"$cli" screenshot smoke-vm "$out/screen.png" > /dev/null || fail "vmq screenshot"
head -c 8 "$out/screen.png" | od -An -tx1 | grep -q "89 50 4e 47" || fail "the screenshot is not a PNG"
"$cli" pause smoke-vm && "$cli" status smoke-vm | grep -q ": paused" || fail "vmq pause"
"$cli" resume smoke-vm && "$cli" status smoke-vm | grep -q ": running" || fail "vmq resume"
"$cli" poweroff smoke-vm || fail "vmq poweroff"
"$cli" status smoke-vm | grep -q ": off" || fail "the machine is still running after poweroff"
# Only this run's own runtime folder counts: another profile's (the user's running machines) is none of its business.
for d in "${XDG_RUNTIME_DIR:-/tmp}"/vmqontrol-*/; do
    d=${d%/}; grep -qxF "$d" <<< "$runtime_before" && continue
    ls "$d"/*/lock.json > /dev/null 2>&1 && fail "poweroff left a runtime folder behind ($d)"
done
echo "smoke: a machine started, paused, resumed, took a screenshot and powered off through vmq"

xvfb-run -a dbus-run-session --config-file="$root/packaging/isolated-bus.conf" "$app" > "$out/app.out" 2>&1 &
wrapper=$!
apid=""
for _ in $(seq 1 150); do
    apid=$(pgrep -x -n vmqontrol || true)
    grep -q "Windowing:" "$out/app.out" 2>/dev/null && break
    sleep 0.1
done
grep -q "Windowing: X11" "$out/app.out" || fail "the window did not open on Xvfb: $(grep Windowing "$out/app.out")"
sleep 3
[ -n "$apid" ] && kill -0 "$apid" 2>/dev/null || fail "the window did not stay up"
grep -q "under VMQONTROL_HOME" "$out/app.out" || fail "the app did not take the isolated profile"
kill -TERM "$apid"
for _ in $(seq 1 50); do kill -0 "$apid" 2>/dev/null || break; sleep 0.1; done
kill -0 "$apid" 2>/dev/null && fail "the window ignored SIGTERM"
wait "$wrapper" 2>/dev/null
echo "smoke: the window opened on Xvfb and closed on SIGTERM"
echo "SMOKE PASSED"
grep -E "WRN|ERR" "$out/app.out" | sed 's/^/  log: /'
exit 0
