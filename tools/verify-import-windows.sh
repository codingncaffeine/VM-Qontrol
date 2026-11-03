#!/usr/bin/env bash
# Phase 11's acceptance for Windows: an installed Windows behind a VMware Workstation .vmx (UEFI, Secure Boot, TPM,
# NVMe or SATA) imports and boots to a running Windows. Headless and silent, in a throwaway profile. Proof: after the
# boot, the ACPI power button shuts it down within two minutes (firmware or a stuck boot ignores the button).
#   tools/verify-import-windows.sh INSTALLED-WINDOWS-DISK [sata|nvme]
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
source tools/env.sh
disk="$(realpath "$1")"; bus="${2:-sata}"
out="$root/scratch/import-windows"; shots="$root/snapshots/import-windows"
rm -rf "$out" "$shots"; mkdir -p "$out/src" "$shots"
export VMQONTROL_HOME="$out/home" PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq
vmq="${VMQ:-$root/src/VmQontrol.Cli/bin/Release/net10.0/vmq}"
qemu-img convert -O vmdk -o subformat=monolithicSparse "$disk" "$out/src/Windows 11.vmdk" || exit 1
cat > "$out/src/Windows 11.vmx" <<VMX
.encoding = "UTF-8"
displayName = "Windows 11"
guestOS = "windows11-64"
firmware = "efi"
uefi.secureBoot.enabled = "TRUE"
vtpm.present = "TRUE"
memsize = "4096"
numvcpus = "4"
${bus}0.present = "TRUE"
${bus}0:0.present = "TRUE"
${bus}0:0.fileName = "Windows 11.vmdk"
ethernet0.present = "TRUE"
ethernet0.virtualDev = "e1000e"
ethernet0.connectionType = "nat"
VMX
"$vmq" import "$out/src/Windows 11.vmx" || exit 1
"$vmq" start "Windows 11" --headless || exit 1
for i in 1 2 3; do sleep 60; "$vmq" screenshot "Windows 11" "$shots/boot-${i}min.png" > /dev/null 2>&1; done
"$vmq" status "Windows 11" | grep -q ": running" || { echo "VERIFY FAILED: the imported machine stopped by itself"; exit 1; }
"$vmq" shutdown "Windows 11"
for i in $(seq 1 24); do
    sleep 5
    if ! "$vmq" status "Windows 11" | grep -q ": running\|: paused"; then
        echo "VERIFY PASSED: the Workstation Windows machine imported ($bus), booted, and shut down from the power button in $((i * 5)) s"
        exit 0
    fi
done
"$vmq" screenshot "Windows 11" "$shots/no-shutdown.png" > /dev/null 2>&1
"$vmq" poweroff "Windows 11" > /dev/null 2>&1
echo "VERIFY FAILED: Windows did not answer the power button (it may not have booted); see $shots"
exit 1
