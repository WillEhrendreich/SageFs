#!/usr/bin/env bash
# Phase-0 Stage 4: "the real thing" - a SageFs daemon, running inside the
# sealed cell, driven end to end through the boundary demo-gif-plan.md §4.1
# describes: the runner (this script) pipes a one-line ScenarioPlan JSON into
# the cell-agent's stdin (crossing the bwrap namespace wall as the process's
# own stdin - nothing is opened from the host into the cell), and the
# cell-agent inside the cell drives Xvfb + a real Chromium + a libXtst click
# on the dashboard's Quick Start control, observes a session card appear via
# Playwright, and streams back one line of StepLog JSON on stdout, while
# ffmpeg records the whole exchange to /out/stage4.mkv.
#
# CRITICAL SAFETY RULE: this daemon must NEVER touch the user's real ports
# (37749/37750) or real ~/.SageFs data. It runs on 47749/47750 inside the
# cell's own private network namespace (so those ports don't even collide
# with anything on the host - this is belt-and-suspenders) with
# SAGEFS_DATA_DIR pointed at the cell's own tmpfs.
set -euo pipefail

SPIKE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SPIKE_DIR/../.." && pwd)"
CELL_AGENT_SRC="$SPIKE_DIR/cell-agent"
CELL_AGENT_BUILD="/tmp/sagefs-demo-spike/cell-agent-build-stage4"
HOSTOUT="${1:-/tmp/sagefs-demo-spike/cells/stage4/out}"
mkdir -p "$HOSTOUT"
rm -f "$HOSTOUT"/*

CHROME_DIR="$HOME/.cache/ms-playwright/chromium-1208/chrome-linux64"
SAGEFS_BIN_DIR="$REPO_ROOT/SageFs/bin/Release/net10.0"
if [ ! -x "$CHROME_DIR/chrome" ]; then
  echo "FAIL: bundled Playwright chromium not found at $CHROME_DIR/chrome" >&2
  exit 1
fi
if [ ! -f "$SAGEFS_BIN_DIR/SageFs.dll" ]; then
  echo "FAIL: SageFs.dll not found at $SAGEFS_BIN_DIR - run: dotnet build $REPO_ROOT/SageFs/SageFs.fsproj -c Release" >&2
  exit 1
fi

echo "== Stage 4: the real thing =="
echo "Building self-contained cell-agent (outside the cell)..."
rm -rf "$CELL_AGENT_BUILD"
dotnet publish "$CELL_AGENT_SRC/CellAgent.fsproj" -c Release -r linux-x64 --self-contained true \
  -o "$CELL_AGENT_BUILD" >/tmp/cellagent-build.log 2>&1 \
  || { echo "FAIL: publish failed, see /tmp/cellagent-build.log" >&2; tail -50 /tmp/cellagent-build.log >&2; exit 1; }
echo "Built: $CELL_AGENT_BUILD/cellagent"

INNER_SCRIPT='
set -euo pipefail
export HOME=/home/demo
mkdir -p /home/demo /home/demo/chrome-profile /home/demo/.sagefs
mkdir -m 1777 -p /tmp/.X11-unix

Xvfb :99 -screen 0 1280x720x24 -nocursor &
XVFB_PID=$!
for i in $(seq 1 50); do [ -e /tmp/.X11-unix/X99 ] && break; sleep 0.1; done
[ -e /tmp/.X11-unix/X99 ] || { echo "CELL: Xvfb never came up" >&2; exit 1; }
export DISPLAY=:99

# Start the SageFs daemon on isolated ports, isolated data dir (private netns:
# these ports cannot collide with the host'\''s real 37749/37750 daemon even
# without the offset, but the offset is kept anyway for belt-and-suspenders).
export SAGEFS_DATA_DIR=/home/demo/.sagefs
export SAGEFS_BIND_HOST=127.0.0.1
export DOTNET_ROOT=/dotnet-root
/dotnet-root/dotnet /sagefs-bin/SageFs.dll --mcp-port 47749 --no-watch --no-resume \
  </dev/null >/out/daemon.log 2>&1 &
DAEMON_PID=$!

echo "CELL: waiting for daemon health on :47749..." >&2
DAEMON_UP=0
for i in $(seq 1 60); do
  if curl -s -o /dev/null -m 1 http://127.0.0.1:47749/health; then DAEMON_UP=1; break; fi
  sleep 0.5
done
if [ "$DAEMON_UP" != "1" ]; then
  echo "CELL: daemon never became healthy" >&2
  cat /out/daemon.log >&2
  kill -TERM "$XVFB_PID" 2>/dev/null || true
  exit 1
fi
echo "CELL: daemon healthy" >&2

# Record the whole exchange (~6s: chrome launch + click + observe).
ffmpeg -y -f x11grab -framerate 15 -video_size 1280x720 -i :99 -t 6 \
  -c:v libx264 -preset ultrafast -qp 0 /out/stage4.mkv </dev/null >/tmp/ffmpeg.log 2>&1 &
FFMPEG_PID=$!

# THE BOUNDARY: read the ScenarioPlan JSON from OUR OWN stdin (piped in by
# bwrap from the host runner'\''s stdin) and write the cell-agent'\''s stdout
# (the StepLog JSON) straight through - nothing here inspects or rewrites it.
set +e
/cellagent-bin/cellagent
CELLAGENT_EXIT=$?
set -e

wait "$FFMPEG_PID" 2>/dev/null || true
kill -TERM "$DAEMON_PID" 2>/dev/null || true
for i in $(seq 1 20); do kill -0 "$DAEMON_PID" 2>/dev/null || break; sleep 0.2; done
kill -KILL "$DAEMON_PID" 2>/dev/null || true
kill -TERM "$XVFB_PID" 2>/dev/null || true
for i in $(seq 1 20); do kill -0 "$XVFB_PID" 2>/dev/null || break; sleep 0.1; done
kill -KILL "$XVFB_PID" 2>/dev/null || true
exit "$CELLAGENT_EXIT"
'

DOTNET_DIR="$(dirname "$(readlink -f "$(command -v dotnet)")")"
if [ ! -x "$DOTNET_DIR/dotnet" ]; then
  echo "FAIL: could not resolve dotnet root from $(command -v dotnet)" >&2
  exit 1
fi

cat > /tmp/sagefs-demo-spike/stage4-plan.json <<EOF
{"scenarioId":"hello-dashboard","chromePath":"/chrome-bin/chrome","pageUrl":"http://127.0.0.1:47750/dashboard","clickSelector":"[data-testid=quick-start]","expectSelector":"[data-testid=session-card]","userDataDir":"/home/demo/chrome-profile"}
EOF

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
  --ro-bind /etc/ssl /etc/ssl \
  --ro-bind "$CHROME_DIR" /chrome-bin \
  --ro-bind "$CELL_AGENT_BUILD" /cellagent-bin \
  --ro-bind "$SAGEFS_BIN_DIR" /sagefs-bin \
  --ro-bind "$DOTNET_DIR" /dotnet-root \
  --proc /proc \
  --dev /dev \
  --bind "$HOSTOUT" /out \
  --die-with-parent \
  --clearenv \
  --setenv HOME /home/demo \
  --setenv PATH /usr/bin \
  --setenv LIBGL_ALWAYS_SOFTWARE 1 \
  --setenv __EGL_VENDOR_LIBRARY_FILENAMES /usr/share/glvnd/egl_vendor.d/50_mesa.json \
  -- /bin/sh -c "$INNER_SCRIPT" \
  < /tmp/sagefs-demo-spike/stage4-plan.json \
  > "$HOSTOUT/steplog.json"
CELL_EXIT=$?
set -e

echo "== Host-side verification =="
echo "Cell exit code: $CELL_EXIT"
echo "--- daemon.log (tail) ---"
[ -f "$HOSTOUT/daemon.log" ] && tail -20 "$HOSTOUT/daemon.log"
echo "--- StepLog (the boundary'\''s output, read by the runner from the cell-agent'\''s stdout) ---"
cat "$HOSTOUT/steplog.json" 2>/dev/null || echo "(empty)"

ok=1
if [ "$CELL_EXIT" = "0" ] && grep -q '"outcome":"Passed"' "$HOSTOUT/steplog.json" 2>/dev/null; then
  echo "PASS: boundary crossed (stdin ScenarioPlan -> stdout StepLog) and the click registered"
else
  echo "FAIL: cell exited $CELL_EXIT or StepLog does not report Passed"
  ok=0
fi

if [ -s "$HOSTOUT/stage4.mkv" ]; then
  echo "PASS: $HOSTOUT/stage4.mkv exists, size=$(stat -c%s "$HOSTOUT/stage4.mkv") bytes"
else
  echo "FAIL: stage4.mkv missing or empty"
  ok=0
fi

[ "$ok" = "1" ]
