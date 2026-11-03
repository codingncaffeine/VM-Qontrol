#!/usr/bin/env bash
# Proves "Lean Windows" on a real disc: two unattended installs side by side from the same disc, one with --lean and
# one without (the control). At first logon each Windows sends the host its provisioned apps, the signed-in account's
# apps, a few of the policies and the lean script's log over a socket on the host loopback (the machine's gateway
# address reaches it), then shuts down. The lean report must lack the removed apps and keep the kept ones; the plain
# report must still HAVE the removed apps, or the check could not see the difference.
#   tools/verify-windows-lean.sh WINDOWS.iso [EDITION-INDEX]
# Takes 15-25 minutes; run it in the background. Headless and silent, throwaway profiles under scratch/.
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
source tools/env.sh
iso="$(realpath "$1")"; edition="${2:-1}"
base="$root/scratch/windows-lean"; shots="$root/snapshots/windows-lean"
rm -rf "$base" "$shots"; mkdir -p "$base" "$shots"
vmq="$root/src/VmQontrol.Cli/bin/Release/net10.0/vmq"
export PULSE_SERVER=unix:/nonexistent/vmq PIPEWIRE_REMOTE=/nonexistent/vmq

report_command() { # $1 = port
    local ps="\$ErrorActionPreference='Continue'; \$r=@(); "
    ps+="\$r+='PROVISIONED='+((Get-AppxProvisionedPackage -Online | ForEach-Object DisplayName) -join ','); "
    ps+="\$r+='USER='+((Get-AppxPackage | ForEach-Object Name) -join ','); "
    ps+="\$r+='CONSUMER='+(Get-ItemProperty HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent -EA 0).DisableWindowsConsumerFeatures; "
    ps+="\$r+='SILENT='+(Get-ItemProperty HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager -EA 0).SilentInstalledAppsEnabled; "
    ps+="\$r+='ONEDRIVERUN='+(Get-ItemProperty HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run -EA 0).OneDriveSetup; "
    ps+="\$r+='LOG='+((Get-Content C:\\Windows\\Setup\\Scripts\\vmq-lean.log -EA 0) -join ' / '); "
    ps+="\$g=(Get-NetRoute -DestinationPrefix 0.0.0.0/0 | Select -First 1).NextHop; "
    ps+="\$c=New-Object Net.Sockets.TcpClient(\$g,$1); \$s=\$c.GetStream(); \$b=[Text.Encoding]::UTF8.GetBytes(\$r -join [char]10); \$s.Write(\$b,0,\$b.Length); \$c.Close(); "
    ps+="shutdown /s /t 15"
    echo "powershell -NoProfile -ExecutionPolicy Bypass -Command \"$ps\""
}

run() { # $1 = name, $2 = port, rest = extra create flags
    local name=$1 port=$2; shift 2
    export VMQONTROL_HOME="$base/$name"
    socat -u TCP-LISTEN:"$port",bind=127.0.0.1,reuseaddr OPEN:"$base/$name.report",creat > /dev/null 2>&1 &
    local listener=$!
    "$vmq" create win-test --iso "$iso" --easy-install --user tester --password 'Qontrol-1' --edition "$edition" \
        --memory-mib 4096 --cores 4 --disk-gib 40 --first-logon "$(report_command "$port")" "$@" > "$base/$name.log" 2>&1 || { echo "$name: create failed"; cat "$base/$name.log"; return 1; }
    "$vmq" start win-test --headless >> "$base/$name.log" 2>&1 || { echo "$name: start failed"; return 1; }
    local n=0
    while "$vmq" status win-test | grep -q ": running\|: paused"; do
        sleep 60; n=$((n + 1))
        "$vmq" screenshot win-test "$shots/$name-$(printf '%02d' $n)min.png" > /dev/null 2>&1
        if [ $n -ge 50 ]; then echo "$name: still running after 50 minutes"; "$vmq" poweroff win-test; break; fi
    done
    kill "$listener" 2>/dev/null
    echo "$name: stopped after $n min"
}

run lean 47331 --lean &
run plain 47332 &
wait

fail=0
check() { # $1 = report, $2 = must|mustnot, $3 = text
    if [ "$2" = must ]; then grep -q -- "$3" "$base/$1.report" || { echo "VERIFY FAILED: $1 report lacks $3"; fail=1; }
    else grep -q -- "$3" "$base/$1.report" && { echo "VERIFY FAILED: $1 report still has $3"; fail=1; }; fi
}
for r in lean plain; do [ -s "$base/$r.report" ] || { echo "VERIFY FAILED: no report from the $r machine"; exit 1; }; done
# The control: a plain Windows has the apps Lean removes.
for app in Microsoft.BingNews Microsoft.GamingApp Clipchamp.Clipchamp; do check plain must "$app"; done
for app in Microsoft.BingNews Microsoft.BingWeather Microsoft.GamingApp Clipchamp.Clipchamp Microsoft.YourPhone Microsoft.MicrosoftSolitaireCollection; do check lean mustnot "$app"; done
for app in Microsoft.WindowsStore Microsoft.WindowsCalculator Microsoft.WindowsNotepad Microsoft.WindowsTerminal; do check lean must "$app"; done
check lean must "CONSUMER=1"
check lean must "SILENT=0"
check lean must "Lean Windows: done"
check plain mustnot "Lean Windows: done"
echo "lean provisioned: $(grep -c , "$base/lean.report" >/dev/null; grep '^PROVISIONED=' "$base/lean.report" | tr ',' '\n' | wc -l) apps; plain: $(grep '^PROVISIONED=' "$base/plain.report" | tr ',' '\n' | wc -l)"
[ $fail = 0 ] && echo "VERIFY PASSED: Lean Windows removed the extras and kept the essentials; the plain install (control) kept everything"
exit $fail
