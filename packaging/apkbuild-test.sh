#!/usr/bin/env bash
# Builds the APKBUILD with Alpine's own abuild in a clean container, from the current tree (git archive standing in
# for the release tarball), and installs the package it makes. Exit 0 = Alpine can package and install VM Qontrol.
set -uo pipefail
cd "$(dirname "$0")/.."
ver=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
work=scratch/apkbuild; rm -rf "$work"; mkdir -p "$work"
# The tracked and new (not ignored) files as they are now, as the release tarball will hold them.
git ls-files -co --exclude-standard -z | tar --null -T - --transform "s,^,VM-Qontrol-$ver/," -czf "$work/vm-qontrol-$ver.tar.gz"
cp packaging/alpine/APKBUILD "$work/"
podman run --rm -v "$PWD/$work:/work:Z" docker.io/library/alpine:latest sh -euc '
    apk update -q > /dev/null && apk add -q alpine-sdk sudo > /dev/null
    adduser -D builder && addgroup builder abuild && echo "builder ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/builder
    mkdir -p /home/builder/pkg && cp /work/APKBUILD /work/*.tar.gz /home/builder/pkg/ && chown -R builder /home/builder
    touch /tmp/abuild.log && chmod 666 /tmp/abuild.log
    su builder -c "cd ~/pkg && abuild-keygen -a -n -q > /dev/null 2>&1 && sudo cp ~/.abuild/*.pub /etc/apk/keys/ && export SRCDEST=\$HOME/pkg && abuild checksum > /tmp/abuild.log 2>&1 && abuild -r >> /tmp/abuild.log 2>&1" \
        || { tail -30 /tmp/abuild.log; exit 1; }
    apk add --no-cache -q --allow-untrusted /home/builder/packages/*/x86_64/vm-qontrol-*.apk > /dev/null
    echo "installed: $(apk info -e vm-qontrol) → $(vmq --version | head -1)"
'
rc=$?
[ $rc = 0 ] && echo "APKBUILD PASSED" || echo "APKBUILD FAILED"
exit $rc
