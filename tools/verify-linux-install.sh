#!/usr/bin/env bash
# Proves Linux Easy Install end to end on a real disc, headless and silent, in a throwaway profile:
#   1. the unattended install runs and powers the machine off when done;
#   2. the machine powers on again from its new disk (the installer and its kernel are no longer used);
#   3. the guest agent the install added answers with the machine's address.
#   tools/verify-linux-install.sh DISC.iso NAME      (VMQ=path/to/vmq to use another build)
# Takes 10-25 minutes; run it in the background.
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
source tools/env.sh
iso="$(realpath "$1")"; name="$2"
out="$root/scratch/linux-install-$name"; shots="$root/snapshots/linux-install-$name"
rm -rf "$out" "$shots"; mkdir -p "$out" "$shots"
export VMQONTROL_HOME="$out/home" PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq
vmq="${VMQ:-$root/src/VmQontrol.Cli/bin/Release/net10.0/vmq}"
start=$(date +%s)
"$vmq" create "$name" --iso "$iso" --easy-install --user tester --password 'Qontrol-1' --power-off-when-done \
    --memory-mib 4096 --cores 4 --disk-gib 25 || exit 1
"$vmq" start "$name" --headless || exit 1
n=0
while "$vmq" status "$name" | grep -q ": running\|: paused"; do
    sleep 60
    n=$((n + 1))
    "$vmq" screenshot "$name" "$shots/install-$(printf '%02d' $n)min.png" > /dev/null 2>&1
    if [ $n -ge 45 ]; then echo "VERIFY FAILED: the install still runs after 45 minutes"; "$vmq" poweroff "$name"; exit 1; fi
done
installed=$(( $(date +%s) - start ))
bytes=$(du -sb "$out/home/VMs/$name/disk0.qcow2" | cut -f1)
[ "$bytes" -gt $((1024 * 1024 * 1024)) ] || { echo "VERIFY FAILED: the install stopped with only $bytes bytes on the disk"; exit 1; }
# The install signal (InstallSignal): the installer's last step wrote the marker to ttyS3.
grep -q VMQONTROL-INSTALLED "$out/home/VMs/$name/install-signal.log" 2>/dev/null \
    || { echo "VERIFY FAILED: $name installed but never wrote the install signal"; exit 1; }
echo "installed in $((installed / 60)) min; booting the new system"
"$vmq" start "$name" --headless || exit 1
for i in $(seq 1 60); do
    sleep 5
    if "$vmq" status "$name" | grep -q "Guest addresses"; then
        "$vmq" status "$name"
        "$vmq" screenshot "$name" "$shots/booted.png" > /dev/null 2>&1
        "$vmq" shutdown "$name"
        sleep 20
        "$vmq" poweroff "$name" > /dev/null 2>&1
        echo "VERIFY PASSED: $name installed unattended in $((installed / 60)) min, booted from its disk, and its guest agent answered"
        exit 0
    fi
done
"$vmq" screenshot "$name" "$shots/no-agent.png" > /dev/null 2>&1
"$vmq" poweroff "$name"
echo "VERIFY FAILED: the installed system did not answer through its guest agent within 5 minutes"
exit 1
