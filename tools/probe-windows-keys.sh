#!/usr/bin/env bash
# Proves which setup keys a Windows disc accepts: one throwaway machine per edition (headless, silent, 2 GB), started
# with Easy Install and the candidate key; after three minutes a screenshot shows whether Setup went past its key page.
#   tools/probe-windows-keys.sh WINDOWS.iso "INDEX:KEY" ...      (runs four at a time)
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
source tools/env.sh
iso="$(realpath "$1")"; shift
out="$root/scratch/key-probe"; shots="$root/snapshots/key-probe"
rm -rf "$out" "$shots"; mkdir -p "$out" "$shots"
export VMQONTROL_HOME="$out/home" PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq
vmq="$root/src/VmQontrol.Cli/bin/Release/net10.0/vmq"
probe() {
    local index="${1%%:*}" key="${1#*:}" name="edition-${1%%:*}"
    "$vmq" create "$name" --iso "$iso" --easy-install --user tester --password x --edition "$index" --key "$key" \
        --memory-mib 2048 --cores 2 --disk-gib 32 > /dev/null || { echo "$name: create failed"; return; }
    "$vmq" start "$name" --headless > /dev/null || { echo "$name: start failed"; return; }
    sleep 170
    "$vmq" screenshot "$name" "$shots/$name.png" > /dev/null
    "$vmq" poweroff "$name" > /dev/null
    echo "$name: $key -> $shots/$name.png"
}
running=0
for spec in "$@"; do
    probe "$spec" &
    sleep 3
    running=$((running + 1))
    if [ $running -ge 4 ]; then wait -n; running=$((running - 1)); fi
done
wait
echo "PROBE DONE"
