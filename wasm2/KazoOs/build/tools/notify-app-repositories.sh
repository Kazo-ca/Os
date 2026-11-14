#!/usr/bin/env bash
# ADR 0271 §4: tells the app repositories that Kazo OS published a version, so their
# "Kazo OS SDK update" workflow moves each app to it right away instead of at its next hourly run.
# Used by "Release Kazo OS" (job notify-apps) once the version is released and its packages are on
# nuget.org.
#
#   build/tools/notify-app-repositories.sh --version <X.Y.Z[-suffix]> --channel <stable|beta>
#                                          [--list <file>] [--dry-run]
#
# Sends to each repository of the list (build/app-repositories.txt by default):
#   POST /repos/<owner>/<name>/dispatches
#   {"event_type": "kazoos-sdk-released", "client_payload": {"version": "...", "channel": "..."}}
# with GH_TOKEN, the token of the Kazo OS automation GitHub App. Without GH_TOKEN it prints a
# warning and sends nothing (the apps still find the version at their hourly run). A repository that
# refuses the dispatch (App not installed, repository renamed) gets a warning; the others are still
# told, and the script succeeds: the release itself is already done. A wrong version, channel or
# list line is an error (exit 1). --dry-run prints what would be sent. GH names the gh command
# (tests use a stand-in).
set -euo pipefail

version=""
channel=""
list="$(cd "$(dirname "$0")/.." && pwd)/app-repositories.txt"
dry_run="false"
gh_command="${GH:-gh}"

fail() {
  echo "::error title=App notification::$1" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="${2:-}"; shift 2 ;;
    --channel) channel="${2:-}"; shift 2 ;;
    --list) list="${2:-}"; shift 2 ;;
    --dry-run) dry_run="true"; shift ;;
    *) echo "usage: $0 --version <version> --channel <stable|beta> [--list <file>] [--dry-run]" >&2; exit 2 ;;
  esac
done

# The payload is written into JSON as is, so only a version and a channel of known shapes pass.
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || fail "'$version' is not a Kazo OS version (X.Y.Z or X.Y.Z-suffix)."
case "$channel" in
  stable) [[ "$version" != *-* ]] || fail "the stable channel takes a version without suffix, not '$version'." ;;
  beta) [[ "$version" == *-* ]] || fail "the beta channel takes a version with a suffix, not '$version'." ;;
  *) fail "the channel is stable or beta, not '$channel'." ;;
esac
[[ -f "$list" ]] || fail "$list does not exist."

# Reads the list: comments and blank lines dropped, each repository once, every line checked first
# so a typo sends nothing rather than half the notifications.
repositories=()
line_number=0
while IFS= read -r line || [[ -n "$line" ]]; do
  line_number=$((line_number + 1))
  entry="${line%%#*}"
  entry="$(tr -d '[:space:]' <<< "$entry")"
  [[ -n "$entry" ]] || continue
  [[ "$entry" =~ ^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$ ]] || fail "$list line $line_number: '$entry' is not owner/name."
  duplicate="false"
  for known in "${repositories[@]}"; do
    [[ "${known,,}" == "${entry,,}" ]] && duplicate="true"
  done
  [[ "$duplicate" == "true" ]] || repositories+=("$entry")
done < "$list"

if [[ ${#repositories[@]} -eq 0 ]]; then
  echo "::warning title=App notification::$list lists no repository: no app is told about Kazo OS $version."
  exit 0
fi

payload="{\"event_type\":\"kazoos-sdk-released\",\"client_payload\":{\"version\":\"$version\",\"channel\":\"$channel\"}}"

if [[ "$dry_run" == "true" ]]; then
  for repository in "${repositories[@]}"; do
    echo "would notify $repository: $payload"
  done
  exit 0
fi

if [[ -z "${GH_TOKEN:-}" ]]; then
  echo "::warning title=App notification::No token of the Kazo OS automation GitHub App (organization secrets KAZO_AUTOMATION_APP_ID and KAZO_AUTOMATION_APP_KEY): the ${#repositories[@]} app repositories are not told about Kazo OS $version; they find it at their hourly run."
  exit 0
fi

notified=0
for repository in "${repositories[@]}"; do
  if output="$("$gh_command" api --method POST "repos/$repository/dispatches" --input - <<< "$payload" 2>&1)"; then
    notified=$((notified + 1))
    echo "notified $repository"
  else
    # One line, so the reason stays readable in the warning annotation.
    reason="$(tr '\n' ' ' <<< "$output")"
    echo "::warning title=App notification::$repository was not told about Kazo OS $version (is the Kazo OS automation GitHub App installed on it?): $reason"
  fi
done

echo "notified=$notified/${#repositories[@]}"
