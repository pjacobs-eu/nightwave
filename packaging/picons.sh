#!/usr/bin/env bash
# Builds the channel logos Nightwave ships: the picons project's service reference
# picons (https://github.com/picons/picons, GPL-3.0) for the orbital positions it scans,
# once for dark backgrounds and once for light ones.
#
#   packaging/picons.sh [output.zip]
#
# The default output is packaging/picons.zip, which the build
# copies beside the program. It is built, not kept: the logos are not committed.
#
# What goes in:
#   index.txt          one line per service, SID_TSID_ONID_NAMESPACE=<file>, as the picons'
#                      own srp.index writes them (hexadecimal, upper case)
#   light/<file>.png   logos drawn for a dark background
#   dark/<file>.png    logos drawn for a light background
# Services that share a logo share its file, so a logo is in the archive once per style.
#
# The logos are the broadcasters' marks; the picons project collects them under GPL-3.0. They
# stay a separate data file - never linked or embedded - and their license and source are named on
# the settings page and in NOTICE.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-$repo/packaging/picons.zip}"

# The positions there are start transponders for (Scanning/InitialTransponders.cs), as
# Enigma2 namespaces: the orbital position in tenths of a degree, east positive, shifted left 16.
namespaces=(C00000 820000 EB0000 11A0000)

size="220x132"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "Finding the latest picons release..."
# Authenticated where a token is at hand: the release workflow shares its address with every
# other runner, and the API allows 60 anonymous requests an hour per address.
release_json="$(curl -fsSL ${GITHUB_TOKEN:+-H "Authorization: Bearer $GITHUB_TOKEN"} "https://api.github.com/repos/picons/picons/releases?per_page=30")"

fetch() {
    local style="$1"
    local url
    url="$(printf '%s' "$release_json" | python3 -c '
import json, re, sys
style, size = sys.argv[1], sys.argv[2]
pattern = re.compile(r"^srp-full\." + re.escape(size) + r"-[0-9x]+\." + style + r"\.on\.transparent_.*\.hardlink\.tar\.xz$")
for release in json.load(sys.stdin):
    for asset in release["assets"]:
        if pattern.match(asset["name"]):
            print(asset["browser_download_url"])
            sys.exit(0)
sys.exit("no srp-full " + size + " " + style + " release found")
' "$style" "$size")"

    echo "Downloading ${url##*/}..."
    mkdir -p "$work/$style"
    curl -fsSL "$url" | tar -xJ -C "$work/$style"
}

fetch light
fetch dark

echo "Picking the logos of ${namespaces[*]}..."
python3 - "$work" "$output" "${namespaces[@]}" <<'PYTHON'
import hashlib, io, os, sys, zipfile

work, output, namespaces = sys.argv[1], sys.argv[2], {n.upper() for n in sys.argv[3:]}

def pictures(style):
    """Service reference picon files of one style: 1_0_<type>_<SID>_<TSID>_<ONID>_<NS>_0_0_0.png."""
    found = {}
    for root, _, files in os.walk(os.path.join(work, style)):
        for name in files:
            if not name.endswith(".png"):
                continue
            parts = name[:-4].upper().split("_")
            if len(parts) < 7 or parts[6] not in namespaces:
                continue
            # The service type (field 2) is left out of the key: the same service is listed
            # once per type it was ever broadcast as, and the logo does not change with it.
            key = "_".join(parts[3:7])
            found.setdefault(key, os.path.join(root, name))
    return found

light = pictures("light")
dark = pictures("dark")

# One name per logo, taken from the light file's content, so services that share a logo share
# its file. The dark file of a service is stored under the same name; a service with only a
# dark picon is named after that one.
index = {}
written = set()

# The folder is ignored by git, so a fresh checkout - every CI run - does not have it yet.
os.makedirs(os.path.dirname(os.path.abspath(output)), exist_ok=True)

with zipfile.ZipFile(output + ".tmp", "w", zipfile.ZIP_STORED) as archive:
    def put(entry, path):
        if entry not in written:
            written.add(entry)
            archive.writestr(entry, open(path, "rb").read())

    for key in sorted(set(light) | set(dark)):
        first = light.get(key) or dark[key]
        name = hashlib.sha1(open(first, "rb").read()).hexdigest()[:16]
        index[key] = name
        if key in light:
            put(f"light/{name}.png", light[key])
        if key in dark:
            put(f"dark/{name}.png", dark[key])

    archive.writestr("index.txt", "".join(f"{key}={name}\n" for key, name in sorted(index.items())))

os.replace(output + ".tmp", output)
logos = len({name for name in index.values()})
print(f"{len(index)} services, {logos} logos, {os.path.getsize(output) // 1024} KiB -> {output}")
PYTHON
