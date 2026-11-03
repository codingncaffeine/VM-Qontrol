#!/usr/bin/env bash
# Inside xvfb-run + dbus-run-session (isolated): the watcher first, then the real app; report, then the app is ended.
here="$(cd "$(dirname "$0")" && pwd)"
python3 -I "$here/watcher.py" "$here/report.txt" &
w=$!
sleep 1
"$1" > "$here/app.out" 2>&1 &
a=$!
wait $w
# The app must end by itself after the menu's Quit; SIGTERM only if it has not within 10 s.
for _ in $(seq 1 100); do kill -0 $a 2>/dev/null || break; sleep 0.1; done
if kill -0 $a 2>/dev/null; then echo "STILL RUNNING after Quit" >> "$here/report.txt"; kill -TERM $a; fi
wait $a; echo "app exit $?" >> "$here/report.txt"
