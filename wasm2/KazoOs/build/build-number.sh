#!/usr/bin/env bash
# Prints the build number of Kazo OS (ADR 0236): the number of commits reachable from HEAD. On
# main it grows with every merge, and every workflow that builds the same commit (Pages, Release,
# server) gets the same number. The workflows pass it to MSBuild as the KazoOsBuildNumber
# environment variable; a build without it is a local « dev » build.
#
#   KazoOsBuildNumber="$(build/build-number.sh)" dotnet publish OS/Kazo.Os.Ecosystem/...
#
# A shallow clone would count only its own commits: it is refused (checkout with fetch-depth: 0).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "$(git -C "$ROOT" rev-parse --is-shallow-repository)" == "true" ]]; then
  echo "build-number.sh: shallow clone, the commit count would be wrong (use fetch-depth: 0)" >&2
  exit 1
fi

git -C "$ROOT" rev-list --count HEAD
