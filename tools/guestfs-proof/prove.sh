#!/usr/bin/env bash
# Open a disk (D37) end to end without libguestfs on this computer: the real vmq inside an Arch container that has it,
# with /dev/kvm and /dev/fuse passed in and an isolated profile. Needs podman and tools/fetch-test-image.sh's Alpine image.
#   tools/guestfs-proof/prove.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
root="$PWD"
podman build -q -t vmq-guestfs tools/guestfs-proof > /dev/null
home="$root/scratch/guestfs-proof/home"
rm -rf "$home"; mkdir -p "$home"
podman run --rm --device /dev/kvm --device /dev/fuse --cap-add SYS_ADMIN -v /usr/share/dotnet:/usr/share/dotnet:ro \
    -v "$root":"$root" -e ROOT="$root" -e HOME_DIR="$home" vmq-guestfs bash "$root/tools/guestfs-proof/walk.sh"
rm -rf "$root/scratch/guestfs-proof"
