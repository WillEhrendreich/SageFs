#!/usr/bin/env bash
# Phase-0 Stage 3: fake input via libXtst, delivered from INSIDE the bwrap cell.
#
# Proves: a self-contained .NET executable (built from xtest-app/Program.fs,
# published self-contained so the cell needs no dotnet SDK) P/Invokes
# XOpenDisplay (libX11.so.6) and XTestFakeMotionEvent/XTestFakeButtonEvent
# (libXtst.so.6) to move the pointer and click a real Chromium page's button,
# then reads the result back through Playwright (document.title changed) -
# not by eyeballing pixels.
#
# NOTE ON THE PLAN'S §2 CLAIM: demo-gif-plan.md says libXtst is "NOT installed"
# on this machine and must be bundled/extracted. That is stale - `ldconfig -p`
# shows /usr/lib/libXtst.so.6 present today (see STAGE-REPORT.md). This script
# therefore RO-binds the system libXtst/libX11 rather than extracting a package;
# the P/Invoke edge itself is identical either way, so this still proves the
# input-edge mechanism the plan describes, just with one less bundling step
# than the plan assumed was necessary.
set -euo pipefail

SPIKE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_SRC="$SPIKE_DIR/xtest-app"
BUILD_DIR="/tmp/sagefs-demo-spike/xtest-app-build"
HOSTOUT="${1:-/tmp/sagefs-demo-spike/cells/stage3/out}"
mkdir -p "$HOSTOUT"
rm -f "$HOSTOUT"/*

CHROME_DIR="$HOME/.cache/ms-playwright/chromium-1208/chrome-linux64"
if [ ! -x "$CHROME_DIR/chrome" ]; then
  echo "FAIL: bundled Playwright chromium not found at $CHROME_DIR/chrome" >&2
  exit 1
fi

echo "== Stage 3: fake input via libXtst =="
echo "Building self-contained xtestapp (outside the cell; dotnet is not bound into it)..."
rm -rf "$BUILD_DIR"
# NOTE: -p:PublishSingleFile=true was tried first and produces a binary that
# SIGABRTs on startup with FileNotFoundException: FSharp.Core - the single-file
# bundler drops it under this SDK/RID combination. A plain self-contained
# folder publish (no single-file) includes FSharp.Core.dll correctly and is
# what's used here; documented as a Stage 3 finding in STAGE-REPORT.md.
dotnet publish "$APP_SRC/XTestApp.fsproj" -c Release -r linux-x64 --self-contained true \
  -o "$BUILD_DIR" >/tmp/xtestapp-build.log 2>&1 \
  || { echo "FAIL: publish failed, see /tmp/xtestapp-build.log" >&2; tail -50 /tmp/xtestapp-build.log >&2; exit 1; }
echo "Built: $BUILD_DIR/xtestapp ($(stat -c%s "$BUILD_DIR/xtestapp") bytes)"

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

# Start recording the whole exchange for evidence (not required for the pass/
# fail verdict - that comes from the app'\''s own PASS/FAIL line below).
ffmpeg -y -f x11grab -framerate 15 -video_size 1280x720 -i :99 -t 4 \
  -c:v libx264 -preset ultrafast -qp 0 /out/stage3.mkv </dev/null >/tmp/ffmpeg.log 2>&1 &
FFMPEG_PID=$!

set +e
/app/xtestapp \
  /chrome/chrome \
  "file:///work/stage2.html" \
  240 140 \
  /home/demo/chrome-profile \
  >/out/xtestapp.log 2>/out/xtestapp.stderr.log
APP_EXIT=$?
set -e

wait "$FFMPEG_PID" 2>/dev/null || true
kill -TERM "$XVFB_PID" 2>/dev/null || true
for i in $(seq 1 20); do kill -0 "$XVFB_PID" 2>/dev/null || break; sleep 0.1; done
kill -KILL "$XVFB_PID" 2>/dev/null || true
echo "CELL: xtestapp exit=$APP_EXIT"
exit "$APP_EXIT"
'

set +e
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
  --ro-bind "$CHROME_DIR" /chrome \
  --ro-bind "$SPIKE_DIR/fixtures" /work \
  --ro-bind "$BUILD_DIR" /app \
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
CELL_EXIT=$?
set -e

echo "== Host-side verification =="
echo "Cell exit code: $CELL_EXIT"
if [ -f "$HOSTOUT/xtestapp.log" ]; then
  echo "--- xtestapp.log ---"
  cat "$HOSTOUT/xtestapp.log"
fi
if [ "$CELL_EXIT" = "0" ] && grep -q "^PASS:" "$HOSTOUT/xtestapp.log" 2>/dev/null; then
  echo "PASS: XTest-delivered click registered inside the sealed cell"
else
  echo "FAIL: cell exited $CELL_EXIT or no PASS line in xtestapp.log"
  [ -f "$HOSTOUT/xtestapp.stderr.log" ] && { echo "--- stderr ---"; cat "$HOSTOUT/xtestapp.stderr.log"; }
  exit 1
fi
