#!/usr/bin/env bash
# Phase-0 Stage 1: bwrap cell + Xvfb + ffmpeg x11grab capture to a host-bind /out.
# Proves: sandbox construction, private display, and the RW bind-mount data plane
# (a file written inside the sealed cell lands on the host at a known path).
set -euo pipefail

HOSTOUT="${1:-/tmp/sagefs-demo-spike/cells/stage1/out}"
mkdir -p "$HOSTOUT"
rm -f "$HOSTOUT/stage1.mkv"

echo "== Stage 1: sandbox + capture =="
echo "Host out dir: $HOSTOUT"

# The cell-agent script that runs as PID 1 inside the sandbox.
INNER_SCRIPT='
set -euo pipefail
export HOME=/home/demo
mkdir -p /home/demo
# Xvfb refuses to mkdir /tmp/.X11-unix itself unless euid==0 (it is not, inside
# an unprivileged userns mapping our own uid) - pre-create it with the standard
# X11 socket-dir mode so Xvfb only has to create the socket file inside it.
mkdir -m 1777 -p /tmp/.X11-unix
id
Xvfb :99 -screen 0 1280x720x24 -nocursor &
XVFB_PID=$!
# Wait for the display socket to appear (bounded, no indefinite block).
for i in $(seq 1 50); do
  if [ -e /tmp/.X11-unix/X99 ]; then break; fi
  sleep 0.1
done
if [ ! -e /tmp/.X11-unix/X99 ]; then
  echo "CELL: Xvfb did not create its socket in time" >&2
  exit 1
fi
export DISPLAY=:99
echo "CELL: Xvfb up, capturing 2s to /out/stage1.mkv"
if [ -e /tmp/.X11-unix/X99 ]; then
  echo "CELL: X11 socket confirmed present"
fi
ffmpeg -y -f x11grab -framerate 15 -video_size 1280x720 -i :99 -t 2 \
  -c:v libx264 -preset ultrafast -qp 0 /out/stage1.mkv </dev/null >/tmp/ffmpeg.log 2>&1
echo "CELL: ffmpeg exit=$? file=$(ls -la /out/stage1.mkv 2>&1)"
# Graceful teardown: SIGTERM, brief wait, SIGKILL only if it refuses to die.
kill -TERM "$XVFB_PID" 2>/dev/null || true
for i in $(seq 1 20); do kill -0 "$XVFB_PID" 2>/dev/null || break; sleep 0.1; done
kill -KILL "$XVFB_PID" 2>/dev/null || true
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
if [ -s "$HOSTOUT/stage1.mkv" ]; then
  echo "PASS: $HOSTOUT/stage1.mkv exists, size=$(stat -c%s "$HOSTOUT/stage1.mkv") bytes"
  ffprobe -v error -show_entries format=duration,size -show_entries stream=codec_type,width,height -of default=noprint_wrappers=0 "$HOSTOUT/stage1.mkv"
else
  echo "FAIL: $HOSTOUT/stage1.mkv missing or empty"
  exit 1
fi
