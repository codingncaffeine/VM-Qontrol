#!/usr/bin/env bash
# Records quickemu's command line for every reference .conf in the test fixtures, plus the host facts quickemu read
# (CPU vendor, logical CPUs, SMT, sockets, memory, CPU flags, QEMU version), so the builder tests replay them anywhere.
#   tools/record-oracle.sh        (rerun after a quickemu upgrade; review the diff before committing)
set -uo pipefail
cd "$(dirname "$0")/.."
F=tests/VmQontrol.Tests/Fixtures/quickemu
for conf in "$F"/*.conf; do
    tools/quickemu-oracle.sh "$conf" "${conf%.conf}.args" > /dev/null || { echo "record: $(basename "$conf") failed"; tail -5 "scratch/oracle/$(basename "$conf" .conf)/quickemu.out"; }
done
smt=$(cat /sys/devices/system/cpu/smt/control 2>/dev/null || echo off)
{
    echo "vendor=$(lscpu | sed -n 's/^Vendor ID: *//p' | head -1)"
    echo "logical=$(nproc)"
    echo "smt=$smt"
    echo "sockets=$(lscpu | sed -n 's/^Socket(s): *//p' | head -1)"
    echo "memory_gb=$(free --giga | tr ' ' '\n' | grep -m 1 '[0-9]')"
    echo "qemu=$(/usr/bin/qemu-system-x86_64 --version | head -1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+')"
    echo "quickemu=$(quickemu --version)"
    echo "flags=$(lscpu | sed -n 's/^Flags: *//p')"
    # Names and aliases: quickemu's test (-device help | grep '"name"') finds either.
    echo "devices=$(/usr/bin/qemu-system-x86_64 -device help 2>&1 | grep -oE '(^name|alias) "[^"]+"' | cut -d'"' -f2 | sort -u | tr '\n' ' ')"
    echo "rendernode=$([ -e /dev/dri/renderD128 ] && echo /dev/dri/renderD128)"
    echo "firmware=$(ls /usr/share/edk2/x64/*.fd /usr/share/OVMF/*.fd /usr/share/qemu/edk2-x86_64*.fd 2>/dev/null | tr '\n' ' ')"
} > "$F/host.txt"
echo "recorded $(ls "$F"/*.args | wc -l) command lines; host facts in $F/host.txt"
