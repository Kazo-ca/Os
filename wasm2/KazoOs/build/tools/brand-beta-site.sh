#!/usr/bin/env bash
# ADR 0271 §3: the beta site presents itself as a beta. Run by "Deploy Kazo OS beta site" on the
# Ecosystem's wwwroot BEFORE `dotnet publish`, so the service worker's asset manifest hashes the
# renamed files (renaming them after the publish would fail the integrity check of the cache).
#   - manifest.webmanifest: name and short_name become "Kazo OS Beta" (the installed PWA's name);
#   - index.html: the tab title becomes "Kazo OS Beta".
# The « Beta » badge and the « Back to the stable version » link need no change here: the OS reads
# its channel from its version (X.Y.Z-preview.<n>).
#
#   build/tools/brand-beta-site.sh [<wwwroot>]
#
# Exits 1 with an explanation when a file no longer has the expected text, so a renamed product
# never ships as a beta that still calls itself "Kazo OS".
set -euo pipefail

root="${1:-$(cd "$(dirname "$0")/../.." && pwd)/OS/Kazo.Os.Ecosystem/wwwroot}"
manifest="$root/manifest.webmanifest"
index="$root/index.html"

fail() {
  echo "::error title=Beta site::$1" >&2
  exit 1
}

[[ -f "$manifest" ]] || fail "$manifest does not exist."
[[ -f "$index" ]] || fail "$index does not exist."

grep -q '"name": "Kazo OS",' "$manifest" || fail "manifest.webmanifest has no \"name\": \"Kazo OS\" to rename."
grep -q '"short_name": "Kazo OS",' "$manifest" || fail "manifest.webmanifest has no \"short_name\": \"Kazo OS\" to rename."
grep -q '<title>Kazo OS</title>' "$index" || fail "index.html has no <title>Kazo OS</title> to rename."

sed -i -e 's/"name": "Kazo OS",/"name": "Kazo OS Beta",/' \
       -e 's/"short_name": "Kazo OS",/"short_name": "Kazo OS Beta",/' "$manifest"
sed -i -e 's#<title>Kazo OS</title>#<title>Kazo OS Beta</title>#' "$index"
echo "Beta branding applied to $root"
