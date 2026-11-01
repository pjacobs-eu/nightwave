#!/usr/bin/env bash
# Builds the Debian package from a published, self-contained build.
#
#   packaging/deb.sh <published directory> <version> <amd64|arm64> <output directory>

set -euo pipefail

published="$1"
version="$2"
arch="$3"
output="$4"

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT

install -d "$root/DEBIAN" "$root/opt/nightwave" "$root/usr/lib/systemd/system"
cp -a "$published/." "$root/opt/nightwave/"
install -m 644 "$repo/packaging/nightwaved.service" "$root/usr/lib/systemd/system/"

cat > "$root/DEBIAN/control" <<CONTROL
Package: nightwave
Version: $version
Architecture: $arch
Maintainer: Philip Jacobs <contact@pjacobs.eu>
Depends: adduser, libc6, libgcc-s1, libstdc++6, libssl3t64 | libssl3, libicu78 | libicu76 | libicu74 | libicu72 | libicu70
Section: video
Priority: optional
Homepage: https://github.com/pjacobs-eu/nightwave
Description: Television server for SAT>IP, DVB cards, HDHomeRun and M3U
 Takes channels from tuners and hands them out as bouquets in M3U and XMLTV,
 with the streams as the source delivers them.
CONTROL

cat > "$root/DEBIAN/postinst" <<'SCRIPT'
#!/bin/sh
set -e

if [ "$1" = configure ]; then
    getent passwd nightwave > /dev/null || adduser --system --group --quiet --no-create-home --home /var/lib/nightwave nightwave

    if [ -d /run/systemd/system ]; then
        systemctl daemon-reload
        systemctl enable nightwaved > /dev/null 2>&1
        systemctl restart nightwaved
    fi
fi
SCRIPT

cat > "$root/DEBIAN/prerm" <<'SCRIPT'
#!/bin/sh
set -e

if [ "$1" = remove ] && [ -d /run/systemd/system ]; then
    systemctl disable --now nightwaved > /dev/null 2>&1 || true
fi
SCRIPT

cat > "$root/DEBIAN/postrm" <<'SCRIPT'
#!/bin/sh
set -e

if [ -d /run/systemd/system ]; then
    systemctl daemon-reload || true
fi
SCRIPT

chmod 755 "$root/DEBIAN/postinst" "$root/DEBIAN/prerm" "$root/DEBIAN/postrm"

mkdir -p "$output"
dpkg-deb --root-owner-group --build "$root" "$output/nightwave_${version}_${arch}.deb"
