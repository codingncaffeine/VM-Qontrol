#!/usr/bin/env bash
# Windows apps as windows, end to end and headless (D43): the real vmq, FreeRDP 3, Xvfb and gnome-keyring in the
# vmq-rdp container (podman build -t vmq-rdp tools/rdp-proof), against a RUNNING Windows Pro machine of a test profile.
#   tools/rdp-proof/prove.sh PROFILE_HOME RUNTIME_DIR MACHINE FIRST_APP [SECOND_APP]
# RUNTIME_DIR is the folder the machine's runtime lives in (mounted as /run/user/UID); PROFILE_HOME is VMQONTROL_HOME.
# Silent by construction: the container has no /dev/snd and no audio server socket.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
home="$1" run="$2" machine="$3" first="$4" second="${5:-}"
out="$root/scratch/rdp-proof/$(date +%H%M%S)"; mkdir -p "$out"
podman run --rm --pid=host --userns=keep-id --user "$(id -u):$(id -g)" -e HOME=/tmp -v "$root:$root" -v "$run:/run/user/$(id -u)" -v "$out:/out" -v /etc/machine-id:/etc/machine-id:ro \
  -e OUT=/out -e XDG_RUNTIME_DIR="/run/user/$(id -u)" -e VMQONTROL_HOME="$home" -e DOTNET_ROOT=/usr/share/dotnet \
  -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e VMQ="$root/src/VmQontrol.Cli/bin/Release/net10.0/vmq" \
  -e SETTLE="${SETTLE:-8}" -e MACHINE="$machine" -e FIRST="$first" -e SECOND="$second" vmq-rdp sh "$root/tools/rdp-proof/inside.sh"
echo "pictures and window list: $out"; cat "$out/windows.txt"
