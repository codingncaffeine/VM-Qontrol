#!/usr/bin/env bash
# Installs packaging/out/vm-qontrol_*.deb in clean Debian 13 and Ubuntu 24.04 containers and runs deb-install-check.sh.
#   packaging/deb-install-test.sh [image ...]
set -uo pipefail
cd "$(dirname "$0")/.."
deb=$(ls packaging/out/vm-qontrol_*_amd64.deb | head -1)
[ -f "$deb" ] || { echo "Build it first: packaging/build-release.sh"; exit 2; }
images=("$@"); [ ${#images[@]} -gt 0 ] || images=(docker.io/library/debian:13 docker.io/library/ubuntu:24.04)
rc=0
for image in "${images[@]}"; do
    echo "════ $image"
    podman run --rm -v "$PWD/$deb:/tmp/vm-qontrol.deb:ro,Z" -v "$PWD/packaging/deb-install-check.sh:/tmp/check.sh:ro,Z" \
        -e DEBIAN_FRONTEND=noninteractive "$image" bash /tmp/check.sh || rc=1
done
[ $rc = 0 ] && echo "DEB INSTALL PASSED" || echo "DEB INSTALL FAILED"
exit $rc
