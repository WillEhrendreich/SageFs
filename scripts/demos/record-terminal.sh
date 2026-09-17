#!/usr/bin/env bash
# record-terminal.sh — generic terminal-session recorder -> GIF, no X11 needed.
#
# Wraps `asciinema rec` (records a scripted terminal session as a .cast file)
# and `agg` (converts a .cast to an animated GIF). This is crisper and more
# deterministic than video capture for terminal-only demos (nvim, CLI tools)
# and avoids Xvfb/GPU/font fragility entirely.
#
# This script knows nothing about SageFs. It is a reusable building block:
# callers pass in the command to record (e.g. a script that drives nvim
# headlessly over RPC and prints progress, or any scripted CLI session).
#
# Example:
#   scripts/demos/record-terminal.sh \
#     --command /path/to/scripted-session.sh \
#     --output /tmp/demo.gif
#
set -euo pipefail

SCRIPT_NAME="$(basename "$0")"

DRIVE_CMD=""
OUTPUT=""
CAST_PATH=""
KEEP_CAST=0

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME --output <path.gif> --command '<command to record>' [options]

Required:
  --output PATH        Where to write the final .gif
  --command CMD         Command to record. Runs under 'asciinema rec --command'.
                         Prefer pointing this at a script file rather than an
                         inline shell one-liner with control flow — asciinema
                         just execs the string, and simple commands are easier
                         to debug from the .cast if something goes wrong.

Options:
  --cast-path PATH      Where to write the intermediate .cast file
                         (default: a temp file, deleted unless --keep-cast)
  --keep-cast            Do not delete the intermediate .cast file
  -h, --help             Show this help and exit

Requires: asciinema, agg (asciinema-gif).
Install (user-space, no sudo, via linuxbrew): brew install asciinema agg
Install (Arch/Omarchy, system, needs sudo):   sudo pacman -S asciinema
                                               yay -S asciinema-agg-bin
EOF
}

log() { echo "[$SCRIPT_NAME] $*" >&2; }
die() { echo "[$SCRIPT_NAME] ERROR: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --command) DRIVE_CMD="$2"; shift 2 ;;
    --output) OUTPUT="$2"; shift 2 ;;
    --cast-path) CAST_PATH="$2"; shift 2 ;;
    --keep-cast) KEEP_CAST=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
done

[[ -n "$OUTPUT" ]] || { usage; die "--output is required"; }
[[ -n "$DRIVE_CMD" ]] || { usage; die "--command is required"; }

for tool in asciinema agg; do
  command -v "$tool" >/dev/null 2>&1 || die "'$tool' not found in PATH. See --help for install hints."
done

WORKDIR=""
if [[ -z "$CAST_PATH" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/record-terminal.XXXXXX")"
  CAST_PATH="$WORKDIR/session.cast"
fi

cleanup() {
  local exit_code=$?
  if [[ "$KEEP_CAST" -eq 0 && -n "$WORKDIR" ]]; then
    rm -rf "$WORKDIR"
  elif [[ "$KEEP_CAST" -eq 1 ]]; then
    log "cast kept at $CAST_PATH"
  fi
  exit "$exit_code"
}
trap cleanup EXIT INT TERM

mkdir -p "$(dirname "$CAST_PATH")"
log "recording session -> $CAST_PATH"
asciinema rec --command "$DRIVE_CMD" --overwrite "$CAST_PATH" \
  || die "asciinema rec failed"

[[ -s "$CAST_PATH" ]] || die "asciinema produced an empty cast file"

log "converting cast -> gif: $OUTPUT"
mkdir -p "$(dirname "$OUTPUT")"
agg "$CAST_PATH" "$OUTPUT" || die "agg conversion failed"

[[ -s "$OUTPUT" ]] || die "gif encode produced an empty file"

if command -v ffprobe >/dev/null 2>&1; then
  FRAMES="$(ffprobe -v error -select_streams v -show_entries stream=nb_frames \
    -of default=noprint_wrappers=1:nokey=1 "$OUTPUT" 2>/dev/null || echo unknown)"
  DIMS="$(ffprobe -v error -select_streams v -show_entries stream=width,height \
    -of csv=p=0:s=x "$OUTPUT" 2>/dev/null || echo unknown)"
else
  FRAMES="unknown (ffprobe not found)"
  DIMS="unknown"
fi
SIZE_BYTES="$(stat -c%s "$OUTPUT" 2>/dev/null || wc -c <"$OUTPUT")"

log "done: $OUTPUT (${DIMS}, ${FRAMES} frames, ${SIZE_BYTES} bytes)"
