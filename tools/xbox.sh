#!/usr/bin/env bash
# Installs, opens and screenshots openbox on a console through its Device Portal.
#
#   XBOX_HOST=https://192.168.1.20:11443 XBOX_USER=... XBOX_PASS=... tools/xbox.sh <command>
#
#   install <dir>      install the .msix in <dir> with the .appx files in <dir>/Dependencies
#                      (the openbox-x64 artifact, unzipped)
#   launch             open openbox
#   stop               close openbox
#   screenshot <file>  save what's on the TV as a PNG
#   log                print openbox's log
#   command "<line>"   run a dev command in a Debug build (Diagnostics/DevCommands.cs)
set -euo pipefail

: "${XBOX_HOST:?set XBOX_HOST, e.g. https://192.168.1.20:11443}" "${XBOX_USER:?}" "${XBOX_PASS:?}"
PACKAGE_NAME=851Labs.openbox
jar=$(mktemp)
trap 'rm -f "$jar"' EXIT

portal() {
    curl -sk --fail-with-body -u "$XBOX_USER:$XBOX_PASS" -b "$jar" -c "$jar" "$@"
}

# Device Portal wants its CSRF-Token cookie echoed back as a header on POSTs.
csrf() {
    portal -o /dev/null "$XBOX_HOST/api/os/info"
    awk '$6 == "CSRF-Token" { token = $7 } END { print token }' "$jar"
}

# Base64, made safe for a query string.
b64() {
    printf '%s' "$1" | base64 | python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.stdin.read().strip(), safe=""))'
}

package_full_name() {
    portal "$XBOX_HOST/api/app/packagemanager/packages" |
        python3 -c 'import json, sys
names = [p["PackageFullName"] for p in json.load(sys.stdin)["InstalledPackages"]
         if p["PackageFullName"].startswith(sys.argv[1] + "_")]
# Right after an update the old version can still be listed: the newest is the one.
key = lambda n: [int(x) for x in n.split("_")[1].split(".")]
print(max(names, key=key) if names else "")' "$PACKAGE_NAME"
}

case "${1:-}" in
install)
    dir=${2:?usage: install <dir>}
    msix=$(ls "$dir"/*.msix | head -1)
    files=(-F "app=@$msix")
    for dep in "$dir"/Dependencies/*.appx; do files+=(-F "$(basename "$dep")=@$dep"); done
    token=$(csrf)
    echo "Installing $(basename "$msix")"
    portal -H "X-CSRF-Token: $token" "${files[@]}" \
        "$XBOX_HOST/api/app/packagemanager/package?package=$(basename "$msix")" >/dev/null
    # 204 while it's still going, then 200 with the result.
    for _ in $(seq 1 120); do
        state=$(curl -sk -u "$XBOX_USER:$XBOX_PASS" -b "$jar" -w '\n%{http_code}' "$XBOX_HOST/api/app/packagemanager/state")
        code=${state##*$'\n'}
        if [ "$code" != 204 ]; then
            echo "${state%$'\n'*}"
            [ "$code" = 200 ] && exit 0 || exit 1
        fi
        sleep 2
    done
    echo "Timed out waiting for the install" >&2
    exit 1
    ;;
launch)
    full_name=$(package_full_name)
    [ -n "$full_name" ] || { echo "openbox isn't installed" >&2; exit 1; }
    family=${full_name%%_*}_${full_name##*_}
    token=$(csrf)
    portal -X POST -H "X-CSRF-Token: $token" -d '' \
        "$XBOX_HOST/api/taskmanager/app?appid=$(b64 "$family!App")&package=$(b64 "$full_name")"
    echo "Opened $full_name"
    ;;
stop)
    full_name=$(package_full_name)
    token=$(csrf)
    portal -X DELETE -H "X-CSRF-Token: $token" "$XBOX_HOST/api/taskmanager/app?package=$(b64 "$full_name")" >/dev/null
    echo "Closed $full_name"
    ;;
log)
    portal "$XBOX_HOST/api/filesystem/apps/file?knownfolderid=LocalAppData&packagefullname=$(package_full_name)&path=%5CLocalState&filename=openbox.log"
    ;;
command)
    tmp=$(mktemp -d)
    printf '%s' "${2:?usage: command \"<line>\"}" > "$tmp/dev-command.txt"
    token=$(csrf)
    portal -H "X-CSRF-Token: $token" -F "file=@$tmp/dev-command.txt" \
        "$XBOX_HOST/api/filesystem/apps/file?knownfolderid=LocalAppData&packagefullname=$(package_full_name)&path=%5CLocalState" >/dev/null
    rm -rf "$tmp"
    echo "Sent: $2"
    ;;
screenshot)
    portal -o "${2:?usage: screenshot <file>}" "$XBOX_HOST/ext/screenshot?hdr=false"
    ;;
*)
    sed -n '2,13p' "$0"
    exit 1
    ;;
esac
