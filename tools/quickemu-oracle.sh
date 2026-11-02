#!/usr/bin/env bash
# Runs the real quickemu on a throwaway copy of a .conf with stand-ins for QEMU, swtpm and the viewers, and prints the
# QEMU command line quickemu would have started, one argument per line. The reference our command builder is held to.
#   tools/quickemu-oracle.sh CONF OUT [extra quickemu options]
# The copy lives under scratch/oracle/<name>/; the original .conf and its folder are only read.
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
source tools/env.sh
conf="$(realpath "$1")"; out="$(realpath -m "$2")"; shift 2
name="$(basename "$conf" .conf)"
work="$root/scratch/oracle/$name"
rm -rf "$work"; mkdir -p "$work"
cp "$conf" "$work/$name.conf"
mkdir -p "$work/$name"
# Placeholder files the .conf names (ISO, firmware, bootloader), so quickemu takes its normal path. Empty: nothing boots.
grep -oE '^(iso|img|fixed_iso|floppy)="[^"]+"' "$conf" | cut -d'"' -f2 | while read -r f; do mkdir -p "$work/$(dirname "$f")"; : > "$work/$f"; done
if grep -q '^guest_os="macos"' "$conf"; then : > "$work/$name/OVMF_CODE.fd"; : > "$work/$name/OVMF_VARS-1920x1080.fd"; : > "$work/$name/OpenCore.qcow2"; fi
export ORACLE_ARGS="$work/args.txt"
# quickemu runs its QEMU path unquoted, so the stand-ins must live in a folder whose path has no spaces.
bin="${XDG_RUNTIME_DIR:-/tmp}/vmqontrol-oracle-bin"
rm -rf "$bin"; mkdir -p "$bin"; cp tools/oracle/* "$bin/"
export PATH="$bin:$PATH"
(cd "$work" && quickemu --vm "$name.conf" --display spice --viewer none --public-dir none "$@") > "$work/quickemu.out" 2>&1
sleep 1.2
[ -s "$ORACLE_ARGS" ] || { echo "oracle: quickemu started nothing"; cat "$work/quickemu.out"; exit 1; }
sed "s|$work|<ROOT>|g" "$ORACLE_ARGS" > "$out"
echo "oracle: $(wc -l < "$out") arguments → $out"
