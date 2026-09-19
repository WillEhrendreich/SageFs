#!/usr/bin/env bash
# drive-dashboard.sh — SageFs-specific driver for the dashboard demo recording.
#
# This script is meant to run as the --command of record-x11.sh (which owns
# Xvfb and ffmpeg). It:
#   1. starts an isolated SageFs daemon (own --mcp-port, own SAGEFS_DATA_DIR,
#      never touches the real ~/.SageFs)
#   2. creates a bare FSI session through the daemon's HTTP API and runs a
#      couple of small evals, so the dashboard has real content to show
#   3. opens the dashboard in Chromium under the X display it inherits via
#      $DISPLAY, and holds it open so record-x11.sh's x11grab has something
#      to film
#   4. tears down everything it started (chromium, the daemon, and the
#      worker process the daemon spawns) before it exits
#
# Xvfb is the "headless" part here (no real monitor) — Chromium itself runs
# in its normal windowed mode, not its own --headless flag, because
# --headless rendering never paints a window onto the X display and
# x11grab would capture nothing. This mirrors how the existing VS Code E2E
# fixture drives an Electron app under Xvfb.
#
# This script owns cleanup of only what it starts. It never signals or kills
# any process by name — only by the PID it recorded when it started that
# process (or that process's own recorded children).

set -euo pipefail

SCRIPT_NAME="$(basename "$0")"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

MCP_PORT=""
DATA_DIR=""
DURATION=6
SAGEFS_BIN=""
CHROMIUM_BIN=""
WORKDIR=""
LOG_DIR=""
NO_SESSION=0

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME --mcp-port N --data-dir DIR [options]

Required:
  --mcp-port N          Port for this job's isolated SageFs daemon (the
                         dashboard listens on N+1). Must not collide with
                         any other running daemon (default main daemon
                         uses 37749/37750 — never use those here).
  --data-dir DIR         Directory for this job's SAGEFS_DATA_DIR. Must be
                         a fresh directory dedicated to this job — the real
                         ~/.SageFs is never touched.

Options:
  --duration SECONDS      Roughly how long the caller will be recording for;
                           this script holds chromium open for duration+10s
                           as a safety margin (default: 6)
  --sagefs-bin PATH        Path to the built SageFs executable (default:
                           <repo>/SageFs/bin/Release/net10.0/SageFs — build
                           it first with:
                           dotnet build SageFs/SageFs.fsproj -c Release)
  --chromium PATH           Path to a chromium binary (default: first of
                             'chromium'/'chromium-browser' on PATH)
  --workdir DIR              Working directory for the FSI session (default:
                             a fresh empty dir under --data-dir — a bare,
                             project-less session)
  --log-dir DIR               Where to write daemon/chromium logs (default:
                             <data-dir>/logs)
  --no-session                 Skip session creation and evals; just show the
                             dashboard's no-session picker landing. Use this
                             as a fallback if real-session content is flaky.
  -h, --help                    Show this help and exit

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
    --workdir) WORKDIR="$2"; shift 2 ;;
    --log-dir) LOG_DIR="$2"; shift 2 ;;
    --no-session) NO_SESSION=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
done

[[ -n "$MCP_PORT" ]] || { usage; die "--mcp-port is required"; }
[[ -n "$DATA_DIR" ]] || { usage; die "--data-dir is required"; }
[[ -n "${DISPLAY:-}" ]] || die "\$DISPLAY is not set — run this under record-x11.sh"

for tool in curl pgrep; do
  command -v "$tool" >/dev/null 2>&1 || die "'$tool' not found in PATH"
done

DASHBOARD_PORT=$((MCP_PORT + 1))

[[ -n "$SAGEFS_BIN" ]] || SAGEFS_BIN="$REPO_ROOT/SageFs/bin/Release/net10.0/SageFs"
[[ -x "$SAGEFS_BIN" ]] || die "sagefs binary not found or not executable: $SAGEFS_BIN (build it first: dotnet build SageFs/SageFs.fsproj -c Release)"

if [[ -z "$CHROMIUM_BIN" ]]; then
  CHROMIUM_BIN="$(command -v chromium 2>/dev/null || command -v chromium-browser 2>/dev/null || true)"
fi
[[ -n "$CHROMIUM_BIN" && -x "$CHROMIUM_BIN" ]] || die "no chromium binary found; pass --chromium PATH"

[[ -n "$LOG_DIR" ]] || LOG_DIR="$DATA_DIR/logs"
mkdir -p "$DATA_DIR" "$LOG_DIR"
[[ -n "$WORKDIR" ]] || { WORKDIR="$DATA_DIR/workdir"; mkdir -p "$WORKDIR"; }

log "mcp-port=$MCP_PORT dashboard-port=$DASHBOARD_PORT data-dir=$DATA_DIR display=$DISPLAY"
log "sagefs-bin=$SAGEFS_BIN chromium=$CHROMIUM_BIN workdir=$WORKDIR"

DAEMON_PID=""
CHROMIUM_PID=""

# Kill a pid and all of its descendants: TERM first, give it a moment, then
# KILL anything still alive. Only ever called with a pid THIS script started
# (DAEMON_PID or CHROMIUM_PID) — never a name-based match.
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
  log "cleanup: daemon pid=${DAEMON_PID:-none} chromium pid=${CHROMIUM_PID:-none}"
  [[ -n "$CHROMIUM_PID" ]] && kill_tree "$CHROMIUM_PID"
  [[ -n "$DAEMON_PID" ]] && kill_tree "$DAEMON_PID"
  sleep 0.5
  local pid
  for pid in "$CHROMIUM_PID" "$DAEMON_PID"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      log "pid $pid still alive after TERM, sending KILL"
      kill -9 "$pid" 2>/dev/null || true
    fi
  done
  log "cleanup done"
  exit "$exit_code"
}
trap cleanup EXIT INT TERM

log "starting isolated SageFs daemon"
SAGEFS_DATA_DIR="$DATA_DIR" "$SAGEFS_BIN" \
  --mcp-port "$MCP_PORT" \
  --owner-pid "$$" \
  --ttl 5m \
  --no-resume \
  >"$LOG_DIR/daemon.log" 2>&1 &
DAEMON_PID=$!
log "daemon pid=$DAEMON_PID"

log "waiting for daemon health on port $MCP_PORT"
ready=0
for _ in $(seq 1 60); do
  if ! kill -0 "$DAEMON_PID" 2>/dev/null; then
    die "daemon exited before becoming healthy, see $LOG_DIR/daemon.log"
  fi
  if curl -sf -m 2 -o /dev/null "http://localhost:$MCP_PORT/health"; then
    ready=1
    break
  fi
  sleep 1
done
[[ "$ready" -eq 1 ]] || die "daemon did not become healthy within 60s, see $LOG_DIR/daemon.log"
log "daemon healthy"

SESSION_ID=""
if [[ "$NO_SESSION" -eq 0 ]]; then
  log "creating a bare session at $WORKDIR"
  create_resp="$(curl -sf -m 15 -X POST "http://localhost:$MCP_PORT/api/sessions/create" \
    -H "Content-Type: application/json" \
    -H "Origin: http://localhost:$MCP_PORT" \
    -d "{\"workingDirectory\": \"$WORKDIR\", \"projects\": [], \"workflow\": \"Interactive\"}" \
    2>>"$LOG_DIR/session-create.log" || true)"
  log "create response: $create_resp"
  SESSION_ID="$(printf '%s' "$create_resp" | grep -o '"message":"[^"]*"' | head -1 | sed -E 's/"message":"([^"]*)"/\1/')"

  if [[ -n "$SESSION_ID" ]]; then
    log "session $SESSION_ID created, waiting for it to report Ready"
    for _ in $(seq 1 60); do
      status_json="$(curl -sf -m 5 "http://localhost:$MCP_PORT/api/sessions" 2>>"$LOG_DIR/session-create.log" || true)"
      if printf '%s' "$status_json" | grep -q "\"id\":\"$SESSION_ID\"[^}]*\"status\":\"Ready\""; then
        log "session $SESSION_ID is Ready"
        break
      fi
      sleep 1
    done

    log "running small evals so the dashboard has real content"
    # Plain, quote-free F# so no runtime JSON escaping is needed.
    for code in \
      '1 + 1;;' \
      'let sageFsDemoNumbers = List.map (fun x -> x * 2) [ 1 .. 5 ];;' \
      'System.DateTime.UtcNow.ToString();;'
    do
      curl -sf -m 20 -X POST "http://localhost:$MCP_PORT/exec" \
        -H "Content-Type: application/json" \
        -H "Origin: http://localhost:$MCP_PORT" \
        -d "{\"code\": \"$code\"}" \
        >>"$LOG_DIR/eval.log" 2>&1 \
        || log "eval failed for: $code (see $LOG_DIR/eval.log) — continuing anyway, the session view still proves the pipeline"
    done
  else
    log "session creation did not return a session id — falling back to the no-session picker landing (see $LOG_DIR/session-create.log)"
  fi
fi

DASHBOARD_URL="http://localhost:$DASHBOARD_PORT/dashboard"
[[ -n "$SESSION_ID" ]] && DASHBOARD_URL="${DASHBOARD_URL}?session=${SESSION_ID}"

CHROME_PROFILE="$DATA_DIR/chrome-profile"
mkdir -p "$CHROME_PROFILE"
# Self-contained config: chromium reads $XDG_CONFIG_HOME/chromium-flags.conf on
# startup. On this box that file (~/.config/chromium-flags.conf) can force
# `--ozone-platform=wayland` + desktop extensions — under Xvfb the wayland
# backend can't init and chromium SIGTRAPs. Point XDG_CONFIG_HOME at an empty
# dir under DATA_DIR so the recorder never inherits the user's desktop flags.
CHROME_XDG_CONFIG="$DATA_DIR/xdg-config"
mkdir -p "$CHROME_XDG_CONFIG"
log "launching chromium at $DASHBOARD_URL"
# CRITICAL on a Wayland box: chromium defaults to the Wayland backend, which
# ignores $DISPLAY and renders on the user's REAL screen (and --kiosk then takes
# it over). Force the X11 backend AND unset WAYLAND_DISPLAY so chromium can only
# ever reach the Xvfb display, never the real compositor. --kiosk is removed: a
# normal window sized to the virtual screen is enough and cannot hijack a real
# display if anything about the isolation is off.
env -u WAYLAND_DISPLAY XDG_CONFIG_HOME="$CHROME_XDG_CONFIG" "$CHROMIUM_BIN" \
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

hold=$((DURATION + 10))
log "holding chromium open for ${hold}s (the caller's recording window fits inside this)"
sleep "$hold"
log "hold time elapsed — exiting, cleanup trap tears everything down"
