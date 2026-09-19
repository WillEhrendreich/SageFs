#!/usr/bin/env bash
# record-x11.sh — generic headless X11 screen recorder -> GIF.
#
# Starts a virtual X display (Xvfb), runs a caller-supplied "drive" command
# under that display (the command is responsible for launching and driving
# whatever should be on screen — an editor, a browser, a terminal emulator,
# anything that opens an X11 window), records the display with ffmpeg's
# x11grab for a fixed duration, and converts the recording to an animated
# GIF using ffmpeg's two-pass palette filter.
#
# This script knows nothing about SageFs. It is a reusable building block:
# callers pass in the command that drives whatever they want recorded.
#
# Example:
#   scripts/demos/record-x11.sh \
#     --display :99 \
#     --size 1280x800 \
#     --duration 5 \
#     --output /tmp/demo.gif \
#     --command 'alacritty -e sh -c "echo hello; sleep 10"'
#
set -euo pipefail

SCRIPT_NAME="$(basename "$0")"

DISPLAY_NUM=":99"
SIZE="1280x800"
FRAMERATE=15
DURATION=5
OUTPUT=""
DRIVE_CMD=""
GIF_FPS=12
GIF_SCALE_WIDTH=900
KEEP_WORKDIR=0
WORKDIR=""

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME --output <path.gif> --command '<launch+drive command>' [options]

Required:
  --output PATH          Where to write the final .gif
  --command CMD          Shell command to run under the virtual display.
                          This command must launch (and, if needed, drive)
                          whatever should appear on screen. It runs with
                          DISPLAY set to the virtual display; record-x11.sh
                          does not wait for it to finish before recording —
                          recording starts immediately after launch and runs
                          for --duration seconds.

Options:
  --display DISP         X display number to use (default: :99)
  --size WxH             Virtual screen size (default: 1280x800)
  --framerate N           ffmpeg capture framerate (default: 15)
  --duration SECONDS      How long to record (default: 5)
  --gif-fps N              Output gif frame rate (default: 12)
  --gif-width N            Output gif width in px, height auto (default: 900)
  --keep-workdir           Do not delete the intermediate mp4/palette files
  -h, --help               Show this help and exit

Requires: Xvfb, ffmpeg (with x11grab + libx264), ffprobe.
On Arch/Omarchy:  sudo pacman -S xorg-server-xvfb ffmpeg
Terminal emulators for a quick visual probe (any one is fine): alacritty, kitty, ghostty.
EOF
}

log() { echo "[$SCRIPT_NAME] $*" >&2; }
die() { echo "[$SCRIPT_NAME] ERROR: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --display) DISPLAY_NUM="$2"; shift 2 ;;
    --size) SIZE="$2"; shift 2 ;;
    --framerate) FRAMERATE="$2"; shift 2 ;;
    --duration) DURATION="$2"; shift 2 ;;
    --output) OUTPUT="$2"; shift 2 ;;
    --command) DRIVE_CMD="$2"; shift 2 ;;
    --gif-fps) GIF_FPS="$2"; shift 2 ;;
    --gif-width) GIF_SCALE_WIDTH="$2"; shift 2 ;;
    --keep-workdir) KEEP_WORKDIR=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
done

[[ -n "$OUTPUT" ]] || { usage; die "--output is required"; }
[[ -n "$DRIVE_CMD" ]] || { usage; die "--command is required"; }

for tool in Xvfb ffmpeg ffprobe; do
  command -v "$tool" >/dev/null 2>&1 || die "'$tool' not found in PATH. See --help for install hints."
done

WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/record-x11.XXXXXX")"
MP4="$WORKDIR/capture.mp4"
PALETTE="$WORKDIR/palette.png"

XVFB_PID=""
DRIVE_PID=""

cleanup() {
  local exit_code=$?
  if [[ -n "$DRIVE_PID" ]] && kill -0 "$DRIVE_PID" 2>/dev/null; then
    log "stopping drive command and its children (pgid $DRIVE_PID)"
    # DRIVE_PID was launched via setsid, so it is its own process group
    # leader: a negative PID kills the whole group, including any window
    # the drive command spawned (e.g. a terminal emulator child process
    # that a plain `kill $DRIVE_PID` would otherwise orphan).
    kill -TERM -- "-$DRIVE_PID" 2>/dev/null || kill "$DRIVE_PID" 2>/dev/null || true
    wait "$DRIVE_PID" 2>/dev/null || true
  fi
  if [[ -n "$XVFB_PID" ]] && kill -0 "$XVFB_PID" 2>/dev/null; then
    log "stopping Xvfb (pid $XVFB_PID)"
    kill "$XVFB_PID" 2>/dev/null || true
    wait "$XVFB_PID" 2>/dev/null || true
  fi
  if [[ "$KEEP_WORKDIR" -eq 0 ]]; then
    rm -rf "$WORKDIR"
  else
    log "workdir kept at $WORKDIR"
  fi
  exit "$exit_code"
}
trap cleanup EXIT INT TERM

log "starting Xvfb $DISPLAY_NUM -screen 0 ${SIZE}x24"
Xvfb "$DISPLAY_NUM" -screen 0 "${SIZE}x24" >"$WORKDIR/xvfb.log" 2>&1 &
XVFB_PID=$!

# Give Xvfb a moment to create its socket; poll instead of a blind sleep.
for _ in $(seq 1 20); do
  if [[ -e "/tmp/.X11-unix/X${DISPLAY_NUM#:}" ]]; then
    break
  fi
  sleep 0.25
done
kill -0 "$XVFB_PID" 2>/dev/null || die "Xvfb exited immediately, see $WORKDIR/xvfb.log"

log "launching drive command under DISPLAY=$DISPLAY_NUM: $DRIVE_CMD"
command -v setsid >/dev/null 2>&1 || die "'setsid' not found in PATH (util-linux) — needed for clean process-group teardown"
# Unset WAYLAND_DISPLAY for the whole drive command. On a Wayland host, GUI
# toolkits (chromium, Electron/VS Code) prefer the Wayland backend and ignore
# $DISPLAY, so they render on the user's REAL screen. Hiding the Wayland socket
# forces them onto the Xvfb X11 display (or to fail cleanly) — they can never
# reach the real compositor.
env -u WAYLAND_DISPLAY DISPLAY="$DISPLAY_NUM" setsid bash -c "$DRIVE_CMD" >"$WORKDIR/drive.log" 2>&1 &
DRIVE_PID=$!

# Small settle delay so the driven app has a window painted before we grab.
sleep 0.5

log "recording ${DURATION}s at ${FRAMERATE}fps from $DISPLAY_NUM"
ffmpeg -y -f x11grab -video_size "$SIZE" -framerate "$FRAMERATE" -i "$DISPLAY_NUM" \
  -t "$DURATION" "$MP4" >"$WORKDIR/ffmpeg-record.log" 2>&1 \
  || die "ffmpeg capture failed, see $WORKDIR/ffmpeg-record.log"

[[ -s "$MP4" ]] || die "capture produced an empty file"

log "generating palette"
ffmpeg -y -i "$MP4" -update 1 -frames:v 1 \
  -vf "fps=$GIF_FPS,scale=${GIF_SCALE_WIDTH}:-1:flags=lanczos,palettegen" \
  "$PALETTE" >"$WORKDIR/ffmpeg-palette.log" 2>&1 \
  || die "palettegen failed, see $WORKDIR/ffmpeg-palette.log"

log "encoding gif -> $OUTPUT"
mkdir -p "$(dirname "$OUTPUT")"
ffmpeg -y -i "$MP4" -i "$PALETTE" \
  -lavfi "fps=$GIF_FPS,scale=${GIF_SCALE_WIDTH}:-1:flags=lanczos,paletteuse" \
  "$OUTPUT" >"$WORKDIR/ffmpeg-gif.log" 2>&1 \
  || die "paletteuse failed, see $WORKDIR/ffmpeg-gif.log"

[[ -s "$OUTPUT" ]] || die "gif encode produced an empty file"

FRAMES="$(ffprobe -v error -select_streams v -show_entries stream=nb_frames \
  -of default=noprint_wrappers=1:nokey=1 "$OUTPUT" 2>/dev/null || echo unknown)"
DIMS="$(ffprobe -v error -select_streams v -show_entries stream=width,height \
  -of csv=p=0:s=x "$OUTPUT" 2>/dev/null || echo unknown)"
SIZE_BYTES="$(stat -c%s "$OUTPUT" 2>/dev/null || wc -c <"$OUTPUT")"

log "done: $OUTPUT (${DIMS}, ${FRAMES} frames, ${SIZE_BYTES} bytes)"
