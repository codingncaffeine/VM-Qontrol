#!/usr/bin/env bash
# Pushes main after the content and identity scans. Run tools/gate.sh first; CI on GitHub then builds and runs the
# push tier again on a clean Ubuntu runner.
#   tools/push.sh
set -uo pipefail
cd "$(dirname "$0")/.."
mkdir -p scratch/gate
tools/precommit-scan.sh --all > scratch/gate/scan.log 2>&1 || { tail -20 scratch/gate/scan.log; echo "PUSH REFUSED: the content scan"; exit 1; }
tools/identity-audit.sh > scratch/gate/identity.log 2>&1 || { tail -20 scratch/gate/identity.log; echo "PUSH REFUSED: the identity audit"; exit 1; }
git fetch -q origin || { echo "PUSH FAILED: fetch"; exit 1; }
if ! git merge-base --is-ancestor origin/main HEAD; then
    echo "PUSH REFUSED: origin/main has commits this branch lacks (edited on GitHub?); rebase onto it first"
    exit 1
fi
git push -q origin HEAD:main || { echo "PUSH FAILED: main"; exit 1; }
echo "PUSHED $(git rev-parse --short HEAD) to main"
