#!/usr/bin/env bash
# Runs INSIDE a clean Debian/Ubuntu container (deb-install-test.sh mounts the .deb): installs it with apt, checks every
# shipped native binary resolves its libraries, that both programs start, and what "vmq doctor" says is missing here
# (a container has no KVM; the rest it names comes from the package's Recommends). Exit 0 = installed and starts.
set -uo pipefail
apt-get update -qq >/dev/null
if ! apt-get install -y -q --no-install-recommends /tmp/vm-qontrol.deb xvfb xauth dbus > /tmp/apt.log 2>&1; then
    tail -25 /tmp/apt.log
    exit 1
fi
. /etc/os-release
echo "installed $(dpkg-query -W -f='${Package} ${Version}' vm-qontrol) on $PRETTY_NAME (Depends only, no Recommends)"

problems=0
for f in /usr/bin/vmqontrol /usr/bin/vmq; do [ -x "$f" ] || { echo "FAIL $f missing"; problems=1; }; done
[ -f /usr/share/applications/io.github.codingncaffeine.VMQontrol.desktop ] || { echo "FAIL no desktop entry"; problems=1; }
[ -f /usr/share/metainfo/io.github.codingncaffeine.VMQontrol.metainfo.xml ] || { echo "FAIL no metainfo"; problems=1; }
[ -f /usr/share/icons/hicolor/512x512/apps/io.github.codingncaffeine.VMQontrol.png ] || { echo "FAIL no icon"; problems=1; }
for f in /usr/lib/vm-qontrol/vmqontrol /usr/lib/vm-qontrol/vmq /usr/lib/vm-qontrol/*.so*; do
    case $(basename "$f") in libcoreclrtraceptprovider.so) continue ;; esac
    bad=$(ldd "$f" 2>&1 | grep -E "not found|version .* not found" || true)
    [ -z "$bad" ] || { echo "FAIL $(basename "$f") does not load here:"; echo "$bad" | sed 's/^/        /'; problems=1; }
done
[ $problems = 1 ] || echo "ok   every shipped library resolves"
v=$(vmq --version 2>&1 | head -1); case $v in vmq\ 0.*) echo "ok   vmq --version: $v" ;; *) echo "FAIL vmq --version said: $v"; problems=1 ;; esac
v=$(vmqontrol --version 2>&1 | head -1); case $v in VM\ Qontrol\ 0.*) echo "ok   vmqontrol --version: $v" ;; *) echo "FAIL vmqontrol --version said: $v"; problems=1 ;; esac
echo "── vmq doctor in this container:"
HOME=/tmp/home vmq doctor | sed 's/^/     /'
# The app's window opens on a virtual screen and stays up (it would exit at once if a library were missing).
mkdir -p /tmp/home
HOME=/tmp/home VMQONTROL_HOME=/tmp/home/vmq xvfb-run -a dbus-run-session -- sh -c 'vmqontrol > /tmp/app.log 2>&1 & pid=$!; sleep 8; if kill -0 $pid 2>/dev/null; then kill $pid; echo "ok   the window opened and stayed up 8 s"; else echo "FAIL the app exited:"; tail -20 /tmp/app.log; exit 1; fi' || problems=1
exit $problems
