#!/usr/bin/env bash
# ADR 0271 §1: the version in build/Versions.props must match the release channel of the branch.
#   main (stable)  - a plain version, X.Y.Z (a suffix would publish a prerelease as the stable
#                    version of os.kazo.ca and of nuget.org);
#   beta (beta)    - a version with a suffix, X.Y.Z-preview.<n> (a plain version would publish the
#                    stable version from beta, before Benoit merges beta into main).
# Any other branch (a <series>/base, a work item) has no channel: every valid version passes.
# Used by "Verify Kazo OS WASM" (pushes to main/beta and pull requests to them) and by
# "Release Kazo OS" before it publishes anything.
#
#   build/tools/check-release-channel.sh <branch> [<Versions.props>]
#
# Prints "version=<v>", "channel=<stable|beta|none>" and "prerelease=<true|false>" on stdout
# (ready for $GITHUB_OUTPUT); exits 1 with an explanation when the version does not fit the branch.
set -euo pipefail

if [[ $# -lt 1 || -z "$1" ]]; then
  echo "usage: $0 <branch> [<Versions.props>]" >&2
  exit 2
fi

branch="$1"
props="${2:-$(cd "$(dirname "$0")/.." && pwd)/Versions.props}"

fail() {
  echo "::error title=Release channel::$1" >&2
  exit 1
}

[[ -f "$props" ]] || fail "$props does not exist."
version="$(sed -n 's:.*<KazoOsVersion>\(.*\)</KazoOsVersion>.*:\1:p' "$props")"
if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  fail "build/Versions.props has no valid KazoOsVersion ('$version')."
fi

prerelease="false"
[[ "$version" == *-* ]] && prerelease="true"

case "$branch" in
  main)
    channel="stable"
    [[ "$prerelease" == "false" ]] || fail "main publishes the stable version: KazoOsVersion must be X.Y.Z, not '$version'. A prerelease (X.Y.Z-preview.<n>) is published from beta (ADR 0271)."
    ;;
  beta)
    channel="beta"
    [[ "$prerelease" == "true" ]] || fail "beta publishes prereleases: KazoOsVersion must carry a suffix (X.Y.Z-preview.<n>), not '$version'. The stable version is published by merging beta into main (ADR 0271)."
    ;;
  *)
    channel="none"
    ;;
esac

echo "version=$version"
echo "channel=$channel"
echo "prerelease=$prerelease"
