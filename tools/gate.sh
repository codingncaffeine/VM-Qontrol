#!/usr/bin/env bash
# The gate before a push: the build with warnings as errors, then the suite N times (default 2) on two CPUs, offline
# and with no sound server. Run it in the background and keep working; push once it says GATE PASSED.
# Every push runs every test but the Window, Integration and Guest tiers; --full adds them, as a release needs
# (the Guest tier boots a real Alpine guest: run tools/fetch-test-image.sh once first).
#   tools/gate.sh [--full] [N]
set -uo pipefail
cd "$(dirname "$0")/.."
source tools/env.sh
tier=(--filter "Tier!=Window&Tier!=Integration&Tier!=Guest")
if [ "${1:-}" = --full ]; then tier=(); shift; fi
N="${1:-2}"
# Silent by construction: no sound server reachable from any test.
export PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq
command -v taskset > /dev/null || { echo "gate: needs taskset"; exit 2; }
exec 9> "${XDG_RUNTIME_DIR:-$TMPDIR}/vmqontrol-gate.lock"
flock -n 9 || { echo "gate: another gate is running"; exit 2; }
mkdir -p scratch/gate

echo "── window tests placed in a tier"
tools/test-tiers.sh || { echo "GATE FAILED: test tiers"; exit 1; }

echo "── build, warnings as errors"
if ! dotnet build VmQontrol.slnx -c Release -warnaserror --nologo -v q > scratch/gate/build.log 2>&1 9>&-; then
    grep -E "error" scratch/gate/build.log | sort -u | head -20
    echo "GATE FAILED: the build"
    exit 1
fi

rc=0
for i in $(seq 1 "$N"); do
    log="scratch/gate/run-$i.log"
    taskset -c 0,1 dotnet test tests/VmQontrol.Tests -c Release --no-build "${tier[@]}" > "$log" 2>&1 9>&-
    line=$(grep -E "^(Passed|Failed)!" "$log" | tail -1 | cut -c1-100)
    echo "run $i of $N: ${line:-no result, see $log}"
    if ! grep -qE "^Passed!" "$log"; then
        rc=1
        grep -E "^\s+Failed " "$log" | sed 's/^/    /'
    fi
done

if (( rc == 0 )); then echo "GATE PASSED: $N runs on 2 CPUs${tier[*]:+ (push tier)}"; else echo "GATE FAILED"; fi
exit $rc
