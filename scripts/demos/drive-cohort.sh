#!/usr/bin/env bash
# drive-cohort.sh — SageFs-specific driver for the COHORT landing-gate demo
# recording.
#
# Meant to run as the --command of record-x11.sh (which owns Xvfb and
# ffmpeg), exactly like drive-dashboard.sh. It:
#   1. Builds a throwaway temp git repo carrying a real, tiny, PREBUILT
#      Expecto fixture project via the CohortOrchestrator's `setup-fixture`
#      subcommand — see scripts/demos/cohort-orchestrator/Program.fs and
#      SageFs.Tests/CohortLandingGateIntegrationTests.fs's header for why the
#      fixture must be prebuilt and SDK-pinned before the daemon ever sees it.
#   2. Starts an ISOLATED SageFs daemon (own --mcp-port, own SAGEFS_DATA_DIR,
#      --owner-pid/--ttl/--no-resume, NEVER the real ~/.SageFs) rooted at
#      that fixture repo.
#   3. Opens the dashboard in Chromium under the X display it inherits via
#      $DISPLAY (forced X11 backend, windowed — not --headless, which never
#      paints onto the display and x11grab would capture nothing).
#   4. Runs the CohortOrchestrator's `run-beats` subcommand, which drives two
#      real MCP client connections ("alice", "bob") through the five demo
#      beats (join -> disjoint claims + a real conflict -> set_integration_ref
#      + live testing -> a GOOD landing lands -> a BREAKING landing is
#      automatically BLOCKED by a real, discovered, failing test), printing
#      each beat's MCP response to stdout so the transcript proves the flow
#      independent of the recording.
#   5. Holds Chromium open a bit longer so the final "Blocked" state stays on
#      screen, then tears down everything it started (chromium, orchestrator,
#      daemon, and any worker the daemon spawned) — never by process name,
#      only by the pid this script itself recorded.
#
# This script owns cleanup of only what it starts.

set -euo pipefail

SCRIPT_NAME="$(basename "$0")"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

MCP_PORT=""
DATA_DIR=""
DURATION=200
SAGEFS_BIN=""
CHROMIUM_BIN=""
ORCHESTRATOR_DLL=""
LOG_DIR=""
PAUSE_SECONDS=3
GATE_TIMEOUT_SECONDS=40

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME --mcp-port N --data-dir DIR [options]

Required:
  --mcp-port N          Port for this job's isolated SageFs daemon (the
                         dashboard listens on N+1). Must not collide with any
                         other running daemon (default main daemon uses
                         37749/37750 — never use those here).
  --data-dir DIR         Directory for this job's SAGEFS_DATA_DIR AND the
                         throwaway fixture git repo. Must be fresh — the real
                         ~/.SageFs is never touched.

Options:
  --duration SECONDS         Roughly how long the caller will be recording
                              for; this script holds chromium open for
                              duration+15s as a safety margin (default: 200 —
                              the full five-beat choreography, including the
                              gate-proof poll, needs real wall-clock time).
  --sagefs-bin PATH           Path to the built SageFs executable (default:
                              <repo>/SageFs/bin/Release/net10.0/SageFs — build
                              it first: dotnet build SageFs/SageFs.fsproj -c Release)
  --chromium PATH              Path to a chromium binary (default: first of
                              'chromium'/'chromium-browser' on PATH)
  --orchestrator-dll PATH       Path to the built CohortOrchestrator.dll
                              (default: <repo>/scripts/demos/cohort-orchestrator/bin/Release/net10.0/CohortOrchestrator.dll —
                              build it first: dotnet build scripts/demos/cohort-orchestrator/CohortOrchestrator.fsproj -c Release)
  --log-dir DIR                  Where to write daemon/chromium/orchestrator
                              logs (default: <data-dir>/logs)
  --pause-seconds N                Camera pause between beats (default: 3)
  --gate-timeout-seconds N          How long the orchestrator polls to prove
                              the breaking landing NEVER fast-forwards the
                              integration branch (default: 40)
  -h, --help                        Show this help and exit

Must be run with \$DISPLAY already set (record-x11.sh sets this).
EOF
}

log() { echo "[$SCRIPT_NAME] $*" >&2; }
die() { echo "[$SCRIPT_NAME] ERROR: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mcp-port) MCP_PORT="$2"; shift 2 ;;
    --data-dir) DATA_DIR="$2"; shift 2 ;;
    --duration) DURATION="$2"; shift 2 ;;
    --sagefs-bin) SAGEFS_BIN="$2"; shift 2 ;;
    --chromium) CHROMIUM_BIN="$2"; shift 2 ;;
    --orchestrator-dll) ORCHESTRATOR_DLL="$2"; shift 2 ;;
    --log-dir) LOG_DIR="$2"; shift 2 ;;
    --pause-seconds) PAUSE_SECONDS="$2"; shift 2 ;;
    --gate-timeout-seconds) GATE_TIMEOUT_SECONDS="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
done

[[ -n "$MCP_PORT" ]] || { usage; die "--mcp-port is required"; }
[[ -n "$DATA_DIR" ]] || { usage; die "--data-dir is required"; }
[[ -n "${DISPLAY:-}" ]] || die "\$DISPLAY is not set — run this under record-x11.sh"

for tool in curl pgrep git dotnet; do
  command -v "$tool" >/dev/null 2>&1 || die "'$tool' not found in PATH"
done

DASHBOARD_PORT=$((MCP_PORT + 1))

[[ -n "$SAGEFS_BIN" ]] || SAGEFS_BIN="$REPO_ROOT/SageFs/bin/Release/net10.0/SageFs"
[[ -x "$SAGEFS_BIN" ]] || die "sagefs binary not found or not executable: $SAGEFS_BIN (build it first: dotnet build SageFs/SageFs.fsproj -c Release)"

[[ -n "$ORCHESTRATOR_DLL" ]] || ORCHESTRATOR_DLL="$SCRIPT_DIR/cohort-orchestrator/bin/Release/net10.0/CohortOrchestrator.dll"
[[ -f "$ORCHESTRATOR_DLL" ]] || die "orchestrator dll not found: $ORCHESTRATOR_DLL (build it first: dotnet build scripts/demos/cohort-orchestrator/CohortOrchestrator.fsproj -c Release)"

if [[ -z "$CHROMIUM_BIN" ]]; then
  CHROMIUM_BIN="$(command -v chromium 2>/dev/null || command -v chromium-browser 2>/dev/null || true)"
fi
[[ -n "$CHROMIUM_BIN" && -x "$CHROMIUM_BIN" ]] || die "no chromium binary found; pass --chromium PATH"

[[ -n "$LOG_DIR" ]] || LOG_DIR="$DATA_DIR/logs"
mkdir -p "$DATA_DIR" "$LOG_DIR"
FIXTURE_REPO="$DATA_DIR/fixture-repo"

log "mcp-port=$MCP_PORT dashboard-port=$DASHBOARD_PORT data-dir=$DATA_DIR display=$DISPLAY"
log "sagefs-bin=$SAGEFS_BIN chromium=$CHROMIUM_BIN orchestrator-dll=$ORCHESTRATOR_DLL"
log "fixture-repo=$FIXTURE_REPO pause=${PAUSE_SECONDS}s gate-timeout=${GATE_TIMEOUT_SECONDS}s"

DAEMON_PID=""
CHROMIUM_PID=""
ORCH_PID=""
NAV_WATCHER_PID=""

# Kill a pid and all of its descendants: TERM first. Only ever called with a
# pid THIS script started — never a name-based match.
kill_tree() {
  local pid="$1"
  [[ -n "$pid" ]] || return 0
  local children
  children="$(pgrep -P "$pid" 2>/dev/null || true)"
  local c
  for c in $children; do
    kill_tree "$c"
  done
  kill -TERM "$pid" 2>/dev/null || true
}

cleanup() {
  local exit_code=$?
  log "cleanup: orchestrator pid=${ORCH_PID:-none} chromium pid=${CHROMIUM_PID:-none} daemon pid=${DAEMON_PID:-none} nav-watcher pid=${NAV_WATCHER_PID:-none}"
  [[ -n "$NAV_WATCHER_PID" ]] && kill_tree "$NAV_WATCHER_PID"
  [[ -n "$ORCH_PID" ]] && kill_tree "$ORCH_PID"
  [[ -n "$CHROMIUM_PID" ]] && kill_tree "$CHROMIUM_PID"
  [[ -n "$DAEMON_PID" ]] && kill_tree "$DAEMON_PID"
  sleep 0.5
  local pid
  for pid in "$ORCH_PID" "$CHROMIUM_PID" "$DAEMON_PID" "$NAV_WATCHER_PID"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      log "pid $pid still alive after TERM, sending KILL"
      kill -9 "$pid" 2>/dev/null || true
    fi
  done
  log "cleanup done"
  exit "$exit_code"
}
trap cleanup EXIT INT TERM

log "setting up the fixture git repo (real, tiny, prebuilt Expecto project)"
dotnet "$ORCHESTRATOR_DLL" setup-fixture --dir "$FIXTURE_REPO" >"$LOG_DIR/fixture-setup.log" 2>&1 \
  || die "fixture setup failed, see $LOG_DIR/fixture-setup.log"
log "fixture ready at $FIXTURE_REPO"

log "starting isolated SageFs daemon rooted at the fixture repo"
# The daemon's OWN process working directory is what set_integration_ref
# treats as "the main repo" (there is no --working-directory flag) — a
# subshell cd's into the fixture repo and execs SageFs there, exactly
# CohortLandingGateIntegrationTests.fs's startIsolatedDaemon
# (psi.WorkingDirectory <- workingDir).
(
  cd "$FIXTURE_REPO"
  exec env SAGEFS_DATA_DIR="$DATA_DIR" "$SAGEFS_BIN" \
    --mcp-port "$MCP_PORT" \
    --owner-pid "$$" \
    --ttl 10m \
    --no-resume
) >"$LOG_DIR/daemon.log" 2>&1 &
DAEMON_PID=$!
log "daemon pid=$DAEMON_PID"

log "waiting for daemon health on port $MCP_PORT"
ready=0
for _ in $(seq 1 120); do
  if ! kill -0 "$DAEMON_PID" 2>/dev/null; then
    die "daemon exited before becoming healthy, see $LOG_DIR/daemon.log"
  fi
  if curl -sf -m 2 -o /dev/null "http://localhost:$MCP_PORT/health"; then
    ready=1
    break
  fi
  sleep 1
done
[[ "$ready" -eq 1 ]] || die "daemon did not become healthy within 120s, see $LOG_DIR/daemon.log"
log "daemon healthy"

DASHBOARD_URL="http://localhost:$DASHBOARD_PORT/dashboard"

CHROME_PROFILE="$DATA_DIR/chrome-profile"
mkdir -p "$CHROME_PROFILE"
log "launching chromium at $DASHBOARD_URL"
# CRITICAL on a Wayland box: chromium defaults to the Wayland backend, which
# ignores $DISPLAY and renders on the user's REAL screen. Force the X11
# backend AND unset WAYLAND_DISPLAY so chromium can only ever reach the Xvfb
# display, never the real compositor.
env -u WAYLAND_DISPLAY "$CHROMIUM_BIN" \
  --no-sandbox --disable-gpu --disable-dev-shm-usage \
  --ozone-platform=x11 \
  --window-size=1280,800 --window-position=0,0 \
  --user-data-dir="$CHROME_PROFILE" \
  --no-first-run --disable-fre --disable-background-networking \
  --disable-session-crashed-bubble --disable-infobars \
  --autoplay-policy=no-user-gesture-required \
  "$DASHBOARD_URL" \
  >"$LOG_DIR/chromium.log" 2>&1 &
CHROMIUM_PID=$!
log "chromium pid=$CHROMIUM_PID"

# Settle so the dashboard shell paints before we start driving MCP calls
# against the same daemon (harmless overlap either way — the dashboard is
# daemon-scoped SSE, it will pick up cohort state the moment it changes).
sleep 1.5

# The cohort panels (Cohort/Lanes) only render inside a session VIEW
# (/dashboard?session=<id>), not on the bare no-session picker landing —
# confirmed empirically via a screenshot probe of the no-session page, which
# shows no Cohort panel at all. The orchestrator writes the real integration
# session id to SESSION_FILE the moment set_integration_ref creates it
# (beat 3); this background watcher polls for that file and, once present,
# uses xdotool to retarget the already-open chromium window at the session
# URL (Ctrl+L, type, Enter) so the SSE-driven cohort panel becomes reachable
# for the rest of the recording. Best-effort: if xdotool is missing or the
# navigation doesn't land, the orchestrator's own printed MCP transcript
# still proves every beat — only the video's visual framing degrades.
SESSION_FILE="$DATA_DIR/integration-session-id.txt"
rm -f "$SESSION_FILE"
if command -v xdotool >/dev/null 2>&1; then
  (
    for _ in $(seq 1 120); do
      if [[ -s "$SESSION_FILE" ]]; then
        sid="$(cat "$SESSION_FILE")"
        win_id="$(DISPLAY="$DISPLAY" xdotool search --sync --onlyvisible --class chromium 2>/dev/null | head -1 || true)"
        if [[ -n "$win_id" ]]; then
          DISPLAY="$DISPLAY" xdotool windowactivate "$win_id" 2>/dev/null || true
          sleep 0.2
          DISPLAY="$DISPLAY" xdotool key --window "$win_id" ctrl+l 2>/dev/null || true
          sleep 0.2
          DISPLAY="$DISPLAY" xdotool type --window "$win_id" --delay 5 "http://localhost:$DASHBOARD_PORT/dashboard?session=$sid" 2>/dev/null || true
          DISPLAY="$DISPLAY" xdotool key --window "$win_id" Return 2>/dev/null || true
          log "navigated chromium to the integration session view ($sid)"
        else
          log "xdotool could not find the chromium window — session-view navigation skipped (cosmetic only)"
        fi
        break
      fi
      sleep 1
    done
  ) &
  NAV_WATCHER_PID=$!
else
  log "xdotool not found — session-view navigation skipped (cosmetic only)"
  NAV_WATCHER_PID=""
fi

log "running the cohort orchestrator (two real MCP connections, five beats)"
dotnet "$ORCHESTRATOR_DLL" run-beats \
  --mcp-port "$MCP_PORT" \
  --main-repo "$FIXTURE_REPO" \
  --pause-seconds "$PAUSE_SECONDS" \
  --gate-timeout-seconds "$GATE_TIMEOUT_SECONDS" \
  --session-file "$SESSION_FILE" \
  2>&1 | tee "$LOG_DIR/orchestrator.log" &
ORCH_PID=$!
log "orchestrator pid=$ORCH_PID"

wait "$ORCH_PID"
ORCH_EXIT=$?
ORCH_PID=""
log "orchestrator exited with code $ORCH_EXIT (see $LOG_DIR/orchestrator.log)"

hold=10
log "holding chromium open for ${hold}s so the final BLOCKED state stays on screen"
sleep "$hold"
log "hold time elapsed — exiting, cleanup trap tears everything down"

exit "$ORCH_EXIT"
