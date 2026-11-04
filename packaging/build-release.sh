#!/usr/bin/env bash
# Builds the Linux release artifacts into packaging/out:
#   VMQontrol-<ver>-linux-x64.tar.gz        glibc distributions (Arch, Debian, Ubuntu, Fedora...): extract, run ./vmqontrol
#   VMQontrol-<ver>-linux-musl-x64.tar.gz   musl distributions (Alpine)
#   vm-qontrol_<ver>_amd64.deb              Debian/Ubuntu system install (/usr/lib/vm-qontrol; vmqontrol and vmq in /usr/bin)
#   SHA256SUMS                              the three files' SHA-256 (attach it to the GitHub release; the wiki's
#                                           Download-Sources-and-Verification page tells people how to check it)
# Self-contained: .NET ships inside; QEMU and its helpers are the distribution's (Depends/Recommends). Publishing goes
# through its own artifacts folder, so bin/ (which a running script or the person's app may use) is never touched.
# Check the .deb with packaging/deb-install-test.sh and the musl tarball with packaging/musl-tarball-test.sh.
set -euo pipefail
cd "$(dirname "$0")/.."
VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=${VMQ_OUT:-packaging/out}
ID=io.github.codingncaffeine.VMQontrol
ART=scratch/release-art

grep -q "<release version=\"$VER\"" "packaging/$ID.metainfo.xml" || { echo "!! packaging/$ID.metainfo.xml has no <release> for $VER" >&2; exit 3; }
# Every recipe carries the same version, or a package is built from (or points at) another release.
for recipe in packaging/alpine/APKBUILD packaging/aur/PKGBUILD; do
    grep -q "^pkgver=$VER\$" "$recipe" || { echo "!! $recipe is not at pkgver=$VER" >&2; exit 3; }
done
rm -rf "$OUT" "$ART" && mkdir -p "$OUT"

for rid in linux-x64 linux-musl-x64; do
    PUB=$OUT/publish-$rid
    echo "── publish v$VER ($rid, self-contained)"
    for project in VmQontrol.App VmQontrol.Cli; do
        dotnet publish "src/$project/$project.csproj" -c Release -r "$rid" --self-contained true \
            -p:VersionPrefix="$VER" --artifacts-path "$ART/$rid" -o "$PUB" -v q --nologo
    done
    # The views must be compiled into the app; a publish that fell back to loading XAML at run time is not shipped.
    grep -qa CompiledAvaloniaXaml "$PUB/vmqontrol.dll" || { echo "!! vmqontrol.dll carries no compiled XAML: refusing to package." >&2; exit 3; }
    cp LICENSE "$PUB/LICENSE"
    cp packaging/README.txt "$PUB/README.txt"
    mkdir -p "$PUB/share"
    cp "packaging/$ID.desktop" "packaging/$ID.metainfo.xml" "$PUB/share/"
    cp -r packaging/icons "$PUB/share/"
    name=VMQontrol-$VER-$rid
    echo "── tarball $name.tar.gz"
    tar -C "$PUB" -czf "$OUT/$name.tar.gz" .
done

echo "── deb"
PUB=$OUT/publish-linux-x64
DEB=$OUT/debroot
LIB=$DEB/usr/lib/vm-qontrol
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$LIB" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/metainfo" "$DEB/usr/share/doc/vm-qontrol" "$DEB/usr/share/icons"
# The whole publish output, never a list of names: a narrowed copy installs cleanly and fails at launch.
cp -a "$PUB/." "$LIB/"
rm -rf "$LIB/README.txt" "$LIB/LICENSE" "$LIB/share"
cp packaging/README.txt "$DEB/usr/share/doc/vm-qontrol/README.txt"
cp LICENSE "$DEB/usr/share/doc/vm-qontrol/copyright"
for bin in vmqontrol vmq; do
    printf '#!/bin/sh\nexec /usr/lib/vm-qontrol/%s "$@"\n' "$bin" > "$DEB/usr/bin/$bin"
    chmod 755 "$DEB/usr/bin/$bin"
done
cp -r packaging/icons/hicolor "$DEB/usr/share/icons/"
cp "packaging/$ID.desktop" "$DEB/usr/share/applications/"
cp "packaging/$ID.metainfo.xml" "$DEB/usr/share/metainfo/"
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<CTRL
Package: vm-qontrol
Version: $VER
Section: admin
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6 (>= 2.34), libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70, libfontconfig1, libfreetype6, libx11-6, libx11-xcb1, libxcb1, libxi6, libxcursor1, libxext6, libxrandr2, libxrender1, libice6, libsm6, libegl1, libgl1, libwayland-client0, libxkbcommon0, zlib1g, qemu-system-x86, qemu-utils, ovmf, gpgv
Recommends: swtpm, swtpm-tools, virt-viewer, osinfo-db, virtiofsd, passt, spice-client-glib-usb-acl-helper, freerdp3-x11
Suggests: wimtools, xorriso, libarchive-tools, quickemu, ffmpeg
Maintainer: VM Qontrol <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/VM-Qontrol
Description: Create, run and fine-tune QEMU/KVM virtual machines
 VM Qontrol makes and runs virtual machines with QEMU and KVM. Windows and the
 big Linux distributions install themselves, every setting says what it does
 and what is recommended, and machines from VMware, VirtualBox and
 virt-manager can be brought over.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/vm-qontrol_${VER}_amd64.deb" > /dev/null
rm -rf "$DEB"

# Checksums of what people download, in sha256sum's own format (`sha256sum -c --ignore-missing SHA256SUMS`).
(cd "$OUT" && sha256sum -- *.tar.gz *.deb > SHA256SUMS)

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
