#!/usr/bin/env bash
# Downloads the guest the Guest-tier tests boot: Alpine's cloud-init image (180 MB), checked against Alpine's published
# SHA-512, into scratch/images/ (never committed). The tests boot it through a throwaway overlay, so it stays pristine.
#   tools/fetch-test-image.sh
set -euo pipefail
cd "$(dirname "$0")/.."
name=nocloud_alpine-3.22.4-x86_64-bios-cloudinit-r0.qcow2
url=https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/cloud/$name
mkdir -p scratch/images
cd scratch/images
if [ -f "$name" ] && echo "$(cat "$name.sha512")  $name" | sha512sum -c --quiet 2>/dev/null; then echo "test image: $PWD/$name (cached)"; exit 0; fi
curl -fsSL -o "$name.sha512" "$url.sha512"
curl -fSL --progress-bar -o "$name.part" "$url"
echo "$(cat "$name.sha512")  $name.part" | sha512sum -c --quiet
mv "$name.part" "$name"
chmod a-w "$name"
echo "test image: $PWD/$name"
