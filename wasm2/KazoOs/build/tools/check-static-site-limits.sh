#!/usr/bin/env bash
# ADR 0271 §3: the beta site is hosted by Cloudflare Pages, which refuses a deployment with more than
# 20,000 files or with a file over 25 MiB. "Deploy Kazo OS beta site" runs this check before the
# upload, so the failure says which limit is crossed (and by which file) instead of a vague upload
# error. If a limit is ever crossed, the fallback named by the plan is a second GitHub Pages site.
#
#   build/tools/check-static-site-limits.sh <directory> [<max files>] [<max bytes per file>]
#
# Prints "files=<n>", "largest=<bytes>" and "largest_path=<path>" (ready for $GITHUB_OUTPUT).
set -euo pipefail

if [[ $# -lt 1 || ! -d "$1" ]]; then
  echo "usage: $0 <directory> [<max files>] [<max bytes per file>]" >&2
  exit 2
fi

directory="$1"
max_files="${2:-20000}"
max_bytes="${3:-26214400}"

files="$(find "$directory" -type f | wc -l | tr -d ' ')"
largest_line="$(find "$directory" -type f -printf '%s %P\n' | sort -n | tail -n 1)"
largest="${largest_line%% *}"
largest_path="${largest_line#* }"
largest="${largest:-0}"

echo "files=$files"
echo "largest=$largest"
echo "largest_path=$largest_path"

status=0
if (( files > max_files )); then
  echo "::error title=Static site limits::$files files, the host accepts at most $max_files." >&2
  status=1
fi
if (( largest > max_bytes )); then
  echo "::error title=Static site limits::$largest_path is $largest bytes, the host accepts at most $max_bytes per file." >&2
  status=1
fi
exit $status
