#!/bin/sh
# Runs inside the vmq-rdp container (see prove.sh): an X server with a window manager, a private session bus with a
# real unlocked keyring, then the real vmq opening Windows programs. Writes pictures and window lists to $OUT.
set -u
Xvfb :9 -screen 0 1600x900x24 >"$OUT/xvfb.log" 2>&1 &
sleep 1; export DISPLAY=:9
openbox >"$OUT/openbox.log" 2>&1 &
sleep 1; xsetroot -solid '#2b3a42'
exec dbus-run-session -- sh -c '
  printf "proof-keyring" | gnome-keyring-daemon --unlock --components=secrets >/dev/null 2>&1
  sleep 1
  run() { echo "\$ vmq $*"; "$VMQ" "$@"; echo "exit $?"; }
  if [ -n "${MENU:-}" ]; then
    # The app menu path: the window program exactly as a menu entry runs it (Exec= of the .desktop file).
    echo "$ $MENU"
    sh -c "$MENU" > "$OUT/menu.log" 2>&1 & menu=$!
    sleep 4; import -window root "$OUT/menu-early.png"
    for i in $(seq 1 40); do kill -0 $menu 2>/dev/null || break; sleep 1; done
    kill -0 $menu 2>/dev/null && echo "menu window still open after 44 s" || echo "menu window closed by itself"
  else
  run apps "$MACHINE" open "$FIRST"
  fi
  sleep "${SETTLE:-8}"
  echo "keyring: $(secret-tool lookup application vm-qontrol kind windows-apps account tester >/dev/null 2>&1 && echo has-the-password || echo empty)"
  pgrep -c freerdp | sed "s/^/clients: /"
  if [ -n "${SECOND:-}" ]; then run apps "$MACHINE" open "$SECOND"; sleep "${SETTLE:-8}"; fi
  pgrep -c freerdp | sed "s/^/clients: /"
  xwininfo -root -tree | grep "\"RAIL\"" | grep -v "has no name" > "$OUT/windows.txt"
  for id in $(xwininfo -root -tree | grep "\"RAIL\"" | awk "{print \$1}"); do
    st=$(xwininfo -id "$id" | awk -F": " "/Map State/ {print \$2}")
    [ "$st" = IsViewable ] && echo "$id $(xwininfo -id "$id" | awk -F": " "/Width|Height/ {printf \"%s \", \$2}") $(xprop -id "$id" WM_NAME _NET_WM_WINDOW_TYPE 2>/dev/null | tr "\n" " ")"
  done > "$OUT/mapped.txt"
  import -window root "$OUT/screen.png"
'
