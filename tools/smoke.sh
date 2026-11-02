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
fail() { echo "SMOKE FAILED: $*"; [ -n "${apid:-}" ] && kill -TERM "$apid" 2>/dev/null; exit 1; }

"$cli" version | head -1 | grep -q '^vmq ' || fail "vmq version"
echo "smoke: vmq answers"

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
