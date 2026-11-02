#!/usr/bin/env bash
# Builds VM Qontrol from source on Alpine (musl) in a throwaway container, the way an Alpine user would: Alpine's own
# .NET SDK package, a clean copy of the tracked files, warnings as errors, then the push tier of the tests and the CLI.
#   tools/alpine-build.sh [alpine tag, default latest]
set -uo pipefail
cd "$(dirname "$0")/.."
tag="${1:-latest}"
out="scratch/alpine"
rm -rf "$out/src"; mkdir -p "$out/src"
git archive HEAD | tar -x -C "$out/src"
# Uncommitted tracked changes too, so the check covers what is about to be committed.
git diff HEAD --binary | (cd "$out/src" && git apply --allow-empty 2>/dev/null || true)
podman run --rm -v "$PWD/$out/src:/src:Z" -w /src "docker.io/library/alpine:$tag" sh -euc '
    apk add --no-cache -q dotnet10-sdk bash icu-libs fontconfig ttf-dejavu > /dev/null
    echo "── $(cat /etc/alpine-release), $(dotnet --version), RID $(dotnet --info | sed -n "s/^ *RID: *//p" | head -1)"
    dotnet build VmQontrol.slnx -c Release -warnaserror --nologo -v q 2>&1 | grep -E "error|warn|Build succeeded" | sort -u
    dotnet test tests/VmQontrol.Tests -c Release --no-build --filter "Tier!=Window&Tier!=Integration" 2>&1 | grep -E "^(Passed|Failed)!|^\s+Failed "
    src/VmQontrol.Cli/bin/Release/net10.0/vmq version | head -1
' > "$out/build.log" 2>&1
rc=$?
cat "$out/build.log"
if [ $rc -eq 0 ] && grep -q "^Passed!" "$out/build.log" && grep -q "^vmq " "$out/build.log"; then echo "ALPINE BUILD PASSED"; else echo "ALPINE BUILD FAILED"; exit 1; fi
