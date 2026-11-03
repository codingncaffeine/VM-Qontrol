set -u
export LIBGUESTFS_BACKEND=direct DOTNET_ROOT=/usr/share/dotnet VMQONTROL_HOME="$HOME_DIR"
vmq() { "$ROOT/src/VmQontrol.Cli/bin/Release/net10.0/vmq" "$@"; }
at() { sed -n 's/.* at \(\/.*\)$/\1/p' | head -1; }
vmq create walk --memory-mib 512 --cores 1 --bios --os linux --disk-gib 1 | tail -1
d="$VMQONTROL_HOME/VMs/walk"
qemu-img create -q -f qcow2 -b "$ROOT/scratch/images/nocloud_alpine-3.22.4-x86_64-bios-cloudinit-r0.qcow2" -F qcow2 "$d/disk0.qcow2"
echo "== 1 read-only"; out=$(vmq open-disk walk 2>&1); echo "$out"; mp=$(echo "$out" | at); [ -n "$mp" ] || { echo NO-MOUNT; exit 1; }
echo "release: $(cat "$mp/etc/alpine-release")"; touch "$mp/root/x" 2>&1 | tail -1
echo "== 2 start while open"; vmq start walk --headless 2>&1 | tail -1
echo "== 3 write"; vmq close-disk walk; vmq open-disk walk --write | head -1; echo hello-from-host > "$mp/root/from-host.txt"; vmq close-disk walk
vmq open-disk walk | head -1; echo "kept: $(cat "$mp/root/from-host.txt")"; vmq close-disk walk; echo "mount dir left: $(ls -d "$mp" 2>&1 | tail -1)"
echo "== 4 data disk (no system)"; cd /tmp; guestfish -N data.img=fs:ext4:64M exit; guestfish -a data.img -m /dev/sda1 write /marker.txt data-disk-ok
cp data.img "$d/data.img"; sed -i 's/"disks": \[/"disks": [ { "file": "data.img", "format": "raw" },/' "$d/vm.json"
grep -c data.img "$d/vm.json"; out=$(vmq open-disk walk --disk 1 2>&1); echo "$out"; mp=$(echo "$out" | at); [ -n "$mp" ] || { echo NO-MOUNT; exit 1; }; echo "marker: $(cat "$mp/marker.txt")"; vmq close-disk walk
