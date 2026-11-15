#!/usr/bin/env bash
# ADR 0271 §4: changes the version of the app, the Version of its [AppManifest] class, and the
# "version" of the committed manifest.json the build generates from it, so both stay equal without
# a build. The Kazo OS SDK update runs it; an author can too.
#
#   bash .github/scripts/bump-app-version.sh <app directory> <new version | major | minor | patch>
#
# major, minor and patch raise that part of the current version and drop any suffix
# (1.2.3 -> 2.0.0, 1.3.0, 1.2.4; patch of 1.2.4-beta.1 -> 1.2.4). An explicit version is
# <major>.<minor>.<patch> with an optional suffix (1.2.4-beta.2). Prints the new version.
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <app directory> <new version | major | minor | patch>" >&2
  exit 2
fi
directory="${1%/}"
request="$2"
semver='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'

# The class with [AppManifest] (bin/ and obj/ hold generated copies, never the source).
mapfile -t sources < <(grep -rlE '^\s*\[AppManifest(\(\))?\]' --include='*.cs' --exclude-dir=bin --exclude-dir=obj "$directory" || true)
if [[ ${#sources[@]} -ne 1 ]]; then
  echo "error: expected one [AppManifest] class under $directory, found ${#sources[@]}." >&2
  exit 1
fi
source="${sources[0]}"

pattern='(Version[[:space:]]*=>[[:space:]]*")([^"]*)(")'
current="$(sed -nE "s/.*${pattern}.*/\2/p" "$source")"
if [[ -z "$current" || "$(printf '%s\n' "$current" | wc -l)" -ne 1 ]]; then
  echo "error: no single 'Version => \"...\"' in $source." >&2
  exit 1
fi
if [[ ! "$current" =~ $semver ]]; then
  echo "error: the current version \"$current\" of $source is not <major>.<minor>.<patch>[-suffix]." >&2
  exit 1
fi
major="${BASH_REMATCH[1]}" minor="${BASH_REMATCH[2]}" patch="${BASH_REMATCH[3]}" suffix="${BASH_REMATCH[4]}"

case "$request" in
  major) next="$((major + 1)).0.0" ;;
  minor) next="$major.$((minor + 1)).0" ;;
  # The patch of a beta version 1.2.4-beta.1 is the version it leads to, 1.2.4.
  patch) if [[ -n "$suffix" ]]; then next="$major.$minor.$patch"; else next="$major.$minor.$((patch + 1))"; fi ;;
  *)
    if [[ ! "$request" =~ $semver ]]; then
      echo "error: \"$request\" is neither major, minor, patch nor a version <major>.<minor>.<patch>[-suffix]." >&2
      exit 2
    fi
    next="$request"
    ;;
esac

sed -i -E "s/${pattern}/\1${next}\3/" "$source"
manifest="$directory/manifest.json"
if [[ -f "$manifest" ]]; then
  # Only the app's own version: the first "version" key, written by the manifest generator.
  sed -i -E "0,/(\"version\"[[:space:]]*:[[:space:]]*\")[^\"]*\"/s//\1${next}\"/" "$manifest"
fi
echo "$next"
