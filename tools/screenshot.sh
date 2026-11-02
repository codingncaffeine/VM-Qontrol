#!/usr/bin/env bash
# The app's window under Xvfb (never the real desktop), captured to snapshots/<batch>/<name>.png after it settles.
#   tools/screenshot.sh BATCH NAME [dark|light] [seconds]
# The profile is isolated: VMQONTROL_HOME, if set by the caller, is kept; otherwise a throwaway one is used.
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
source tools/env.sh
batch="$1"; name="$2"; theme="${3:-dark}"; wait="${4:-4}"
app="$root/src/VmQontrol.App/bin/Release/net10.0/vmqontrol"
[ -x "$app" ] || { echo "screenshot: build Release first"; exit 2; }
mkdir -p "snapshots/$batch"
export VMQONTROL_HOME="${VMQONTROL_HOME:-$root/scratch/screenshot-home}"
export VMQONTROL_THEME="$theme" VMQONTROL_WAYLAND=0
export PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq
unset WAYLAND_DISPLAY WAYLAND_SOCKET
out="$root/snapshots/$batch/$name.png"
xvfb-run -a -s "-screen 0 1440x900x24" dbus-run-session --config-file="$root/packaging/isolated-bus.conf" bash -c "
    '$app' > '$root/scratch/tmp/screenshot-app.out' 2>&1 & pid=\$!
    sleep $wait
    import -window root '$out'
    kill -TERM \$pid; wait \$pid 2>/dev/null"
[ -s "$out" ] && echo "screenshot: $out" || { echo "screenshot: failed"; cat "$root/scratch/tmp/screenshot-app.out"; exit 1; }
