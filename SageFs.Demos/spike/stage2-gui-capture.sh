#!/usr/bin/env bash
# Phase-0 Stage 2: real headed Chromium inside the cell, rendering a local
# file:// page, captured to /out/stage2.mkv, with a pixel-level proof (an
# in-cell X11 screenshot, ImageMagick `import`) that the page actually painted
# rather than trusting the video by eye.
set -euo pipefail

SPIKE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOSTOUT="${1:-/tmp/sagefs-demo-spike/cells/stage2/out}"
mkdir -p "$HOSTOUT"
rm -f "$HOSTOUT/stage2.mkv" "$HOSTOUT/stage2.png"

CHROME_BIN="$HOME/.cache/ms-playwright/chromium-1208/chrome-linux64/chrome"
if [ ! -x "$CHROME_BIN" ]; then
  echo "FAIL: bundled Playwright chromium not found at $CHROME_BIN" >&2
  exit 1
fi

echo "== Stage 2: GUI in the cell + capture =="
echo "Host out dir: $HOSTOUT"
echo "Chrome binary: $CHROME_BIN"

INNER_SCRIPT='
set -euo pipefail
export HOME=/home/demo
mkdir -p /home/demo /home/demo/chrome-profile
mkdir -m 1777 -p /tmp/.X11-unix
Xvfb :99 -screen 0 1280x720x24 -nocursor &
XVFB_PID=$!
for i in $(seq 1 50); do [ -e /tmp/.X11-unix/X99 ] && break; sleep 0.1; done
[ -e /tmp/.X11-unix/X99 ] || { echo "CELL: Xvfb never came up" >&2; exit 1; }
export DISPLAY=:99

/chrome/chrome \
  --no-sandbox --disable-gpu --ozone-platform=x11 \
  --window-position=0,0 --window-size=1280,720 \
  --user-data-dir=/home/demo/chrome-profile \
  --no-first-run --disable-features=Translate --disable-extensions \
  --disable-background-networking --disable-sync --disable-default-apps \
  --disable-infobars --no-default-browser-check \
  --app="file:///work/stage2.html" >/tmp/chrome.log 2>&1 &
CHROME_PID=$!

# Bounded wait for the window to actually map (no fixed guess-sleep-then-hope):
# poll xwininfo-free via /proc for the process and give the renderer a fixed
# ceiling to paint under llvmpipe.
for i in $(seq 1 40); do
  sleep 0.25
  if ! kill -0 "$CHROME_PID" 2>/dev/null; then
    echo "CELL: chromium exited early, log:" >&2
    cat /tmp/chrome.log >&2
    exit 1
  fi
done

echo "CELL: capturing 2s to /out/stage2.mkv"
ffmpeg -y -f x11grab -framerate 15 -video_size 1280x720 -i :99 -t 2 \
  -c:v libx264 -preset ultrafast -qp 0 /out/stage2.mkv </dev/null >/tmp/ffmpeg.log 2>&1

echo "CELL: taking pixel-proof screenshot to /out/stage2.png"
import -window root -display :99 /out/stage2.png

kill -TERM "$CHROME_PID" 2>/dev/null || true
for i in $(seq 1 20); do kill -0 "$CHROME_PID" 2>/dev/null || break; sleep 0.1; done
kill -KILL "$CHROME_PID" 2>/dev/null || true
wait "$CHROME_PID" 2>/dev/null || true
kill -TERM "$XVFB_PID" 2>/dev/null || true
for i in $(seq 1 20); do kill -0 "$XVFB_PID" 2>/dev/null || break; sleep 0.1; done
kill -KILL "$XVFB_PID" 2>/dev/null || true
echo "CELL: done"
'

bwrap \
  --unshare-user --unshare-net --unshare-pid --unshare-ipc \
  --uid 0 --gid 0 \
  --tmpfs /tmp \
  --tmpfs /home/demo \
  --ro-bind /usr /usr \
  --symlink usr/bin /bin \
  --symlink usr/bin /sbin \
  --symlink usr/lib /lib \
  --symlink usr/lib /lib64 \
  --ro-bind /etc/fonts /etc/fonts \
  --ro-bind "$HOME/.cache/ms-playwright/chromium-1208/chrome-linux64" /chrome \
  --ro-bind "$SPIKE_DIR/fixtures" /work \
  --proc /proc \
  --dev /dev \
  --bind "$HOSTOUT" /out \
  --die-with-parent \
  --clearenv \
  --setenv HOME /home/demo \
  --setenv PATH /usr/bin \
  --setenv LIBGL_ALWAYS_SOFTWARE 1 \
  --setenv __EGL_VENDOR_LIBRARY_FILENAMES /usr/share/glvnd/egl_vendor.d/50_mesa.json \
  -- /bin/sh -c "$INNER_SCRIPT"

echo "== Host-side verification =="
ok=1
if [ -s "$HOSTOUT/stage2.mkv" ]; then
  echo "PASS: $HOSTOUT/stage2.mkv exists, size=$(stat -c%s "$HOSTOUT/stage2.mkv") bytes"
else
  echo "FAIL: stage2.mkv missing or empty"
  ok=0
fi

if [ -s "$HOSTOUT/stage2.png" ]; then
  echo "PASS: $HOSTOUT/stage2.png exists, size=$(stat -c%s "$HOSTOUT/stage2.png") bytes"
  # Pixel-level proof: the button occupies (40,40)-(440,240), centre (240,140),
  # rendered rgb(0,200,60). Background elsewhere is rgb(16,24,32) (#101820).
  PIXEL=$(convert "$HOSTOUT/stage2.png" -format "%[pixel:p{240,140}]" info:)
  echo "Pixel at button centre (240,140): $PIXEL"
  case "$PIXEL" in
    *"srgb(0,200,60)"*|*"srgba(0,200,60"*) echo "PASS: button colour rendered exactly as authored" ;;
    *) echo "FAIL: unexpected pixel colour, page did not render as expected"; ok=0 ;;
  esac
else
  echo "FAIL: stage2.png missing or empty"
  ok=0
fi

[ "$ok" = "1" ]
