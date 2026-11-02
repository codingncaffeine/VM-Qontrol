# Sourced by every tool: temporary files stay in the project's scratch folder, never /tmp.
_vmq_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export TMPDIR="$_vmq_root/scratch/tmp"
mkdir -p "$TMPDIR"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
