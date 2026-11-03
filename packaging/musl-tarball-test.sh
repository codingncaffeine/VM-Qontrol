#!/usr/bin/env bash
# The linux-musl-x64 tarball on a clean Alpine: extracted, every shipped library resolves, both programs start, and
# "vmq doctor" names what Alpine lacks with apk commands.
set -uo pipefail
cd "$(dirname "$0")/.."
tarball=$(ls packaging/out/VMQontrol-*-linux-musl-x64.tar.gz | head -1)
[ -f "$tarball" ] || { echo "Build it first: packaging/build-release.sh"; exit 2; }
podman run --rm -v "$PWD/$tarball:/tmp/vmq.tar.gz:ro,Z" docker.io/library/alpine:latest sh -c '
    apk add --no-cache -q icu-libs libstdc++ libgcc fontconfig > /dev/null
    mkdir /opt/vmq && tar -xzf /tmp/vmq.tar.gz -C /opt/vmq
    echo "on Alpine $(cat /etc/alpine-release)"
    problems=0
    for f in /opt/vmq/vmq /opt/vmq/vmqontrol /opt/vmq/*.so*; do
        # .NET loads its LTTng trace provider only when LTTng is installed: not a dependency.
        case $(basename $f) in libcoreclrtraceptprovider.so) continue ;; esac
        bad=$(ldd "$f" 2>&1 | grep -E "not found|Error" || true)
        [ -z "$bad" ] || { echo "FAIL $(basename $f): $bad"; problems=1; }
    done
    [ $problems = 1 ] || echo "ok   every shipped library resolves"
    /opt/vmq/vmq --version | head -1
    HOME=/tmp /opt/vmq/vmq doctor | sed "s/^/     /"
    exit $problems
'
rc=$?
[ $rc = 0 ] && echo "MUSL TARBALL PASSED" || echo "MUSL TARBALL FAILED"
exit $rc
