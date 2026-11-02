#!/usr/bin/env bash
# Proves Windows Easy Install end to end on a real disc: an unattended install, headless and silent, in a throwaway
# profile. The answer file's last first-logon step shuts Windows down, so the machine powering itself off means Setup,
# partitioning, the edition, the account and the first logon all worked. A screenshot a minute goes to snapshots/.
#   tools/verify-windows-install.sh WINDOWS.iso [EDITION-INDEX] [--drivers virtio-win.iso]
# Takes 10-20 minutes; run it in the background.
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
source tools/env.sh
iso="$(realpath "$1")"; edition="${2:-1}"; shift; shift || true
out="$root/scratch/windows-install"; shots="$root/snapshots/windows-install"
rm -rf "$out" "$shots"; mkdir -p "$out" "$shots"
export VMQONTROL_HOME="$out/home" PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq
vmq="$root/src/VmQontrol.Cli/bin/Release/net10.0/vmq"
start=$(date +%s)
"$vmq" create win-test --iso "$iso" --easy-install --user tester --password 'Qontrol-1' --edition "$edition" \
    --memory-mib 4096 --cores 4 --disk-gib 40 --first-logon 'shutdown /s /t 15' "$@" || exit 1
"$vmq" start win-test --headless || exit 1
n=0
while "$vmq" status win-test | grep -q ": running\|: paused"; do
    sleep 60
    n=$((n + 1))
    "$vmq" screenshot win-test "$shots/$(printf '%02d' $n)min.png" > /dev/null 2>&1
    if [ $n -ge 45 ]; then echo "VERIFY FAILED: still running after 45 minutes"; "$vmq" poweroff win-test; exit 1; fi
done
elapsed=$(( $(date +%s) - start ))
used=$(du -sh "$out/home/VMs/win-test/disk0.qcow2" | cut -f1)
# The control: an installed Windows fills several gigabytes; a machine that stopped early (or was stopped) does not.
bytes=$(du -sb "$out/home/VMs/win-test/disk0.qcow2" | cut -f1)
[ "$bytes" -gt $((5 * 1024 * 1024 * 1024)) ] || { echo "VERIFY FAILED: the machine stopped with only $used on its disk (no Windows installed)"; exit 1; }
echo "VERIFY PASSED: Windows installed and shut itself down after its first logon in $((elapsed / 60)) min $((elapsed % 60)) s; disk uses $used"
