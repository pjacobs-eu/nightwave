#!/usr/bin/env bash
# Builds a signed apt repository from Debian packages, as plain files for a static web host.
# The signing key has to be in gpg's keyring.
#
#   packaging/apt-repo.sh <directory of .deb files> <output directory>

set -euo pipefail

packages="$1"
site="$2"
suite=stable
component=main
architectures="amd64 arm64"

mkdir -p "$site/pool/$component/n/nightwave"
cp "$packages"/*.deb "$site/pool/$component/n/nightwave/"

cd "$site"

for arch in $architectures; do
    index="dists/$suite/$component/binary-$arch"
    mkdir -p "$index"
    apt-ftparchive --arch "$arch" packages pool > "$index/Packages"
    gzip -9 -k -f "$index/Packages"
done

apt-ftparchive \
    -o "APT::FTPArchive::Release::Origin=Nightwave" \
    -o "APT::FTPArchive::Release::Label=Nightwave" \
    -o "APT::FTPArchive::Release::Suite=$suite" \
    -o "APT::FTPArchive::Release::Codename=$suite" \
    -o "APT::FTPArchive::Release::Components=$component" \
    -o "APT::FTPArchive::Release::Architectures=$architectures" \
    release "dists/$suite" > "dists/$suite/Release.new"
mv "dists/$suite/Release.new" "dists/$suite/Release"

gpg --batch --yes --clearsign -o "dists/$suite/InRelease" "dists/$suite/Release"
gpg --batch --yes --armor --detach-sign -o "dists/$suite/Release.gpg" "dists/$suite/Release"
gpg --batch --yes --export -o nightwave.gpg

touch .nojekyll
cat > index.html <<'HTML'
<!doctype html>
<meta charset="utf-8">
<title>Nightwave packages</title>
<p>This is the apt repository of <a href="https://github.com/pjacobs-eu/nightwave#debian-and-ubuntu">Nightwave</a>.</p>
HTML
