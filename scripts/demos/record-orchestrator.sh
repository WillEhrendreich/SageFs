#!/usr/bin/env bash
# record-orchestrator.sh — parallel, fully-headless demo-recording harness.
#
# Given a list of demo "jobs", this allocates a distinct X display number, a
# distinct free daemon port, and a distinct temp SAGEFS_DATA_DIR to each job,
# then runs each job through the existing generic recorders:
#   - record-x11.sh (Xvfb + ffmpeg x11grab -> gif) for "video" jobs
#   - record-terminal.sh (asciinema + agg -> gif) for "terminal" jobs
#
# Concurrency: video jobs are bounded by --pool (default 2), because
# x11grab captures live frames and CPU/GPU contention drops them. Terminal
# jobs run fully unbounded, because asciinema records an event stream, not
# live frames — it isn't timing sensitive, so contention there only costs
# CPU, not correctness.
#
# This script does not know anything about SageFs beyond the isolation
# convention (an mcp port and a SAGEFS_DATA_DIR handed to the job's own
# driver script). Each job's driver (e.g. drive-dashboard.sh) is the part
# that actually knows how to stand up and drive a SageFs daemon for its
# particular demo. This script does NOT edit or replace record-x11.sh /
# record-terminal.sh — it drives them.
#
# Job spec (repeatable --job flag), semicolon-separated key=value pairs:
#   name=NAME            unique job name (used for logs/temp dirs/default output)
#   type=video|terminal   which recorder to use
#   driver=PATH            driver script to invoke with the isolated resources
#   duration=SECONDS        recording duration, video jobs only (default 6)
#   output=PATH               where to write the final .gif
#                              (default: <out-dir>/<name>.gif)
#   args=EXTRA ARGS             extra args appended verbatim to the driver
#                              invocation (e.g. --sagefs-bin / --chromium)
#
# Every video-job driver is invoked as:
#   <driver> --mcp-port PORT --data-dir DIR --duration SECONDS <args>
# with $DISPLAY set by record-x11.sh to this job's allocated display.
#
# Every terminal-job driver is invoked as:
#   <driver> --mcp-port PORT --data-dir DIR <args>
# (no $DISPLAY — terminal jobs never touch X.)
#
# Example (the dashboard proof run):
#   scripts/demos/record-orchestrator.sh --pool 2 \
#     --job 'name=dashboard;type=video;driver=scripts/demos/drive-dashboard.sh;duration=6;args=--sagefs-bin /path/to/SageFs --chromium /usr/bin/chromium'

set -euo pipefail

SCRIPT_NAME="$(basename "$0")"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

RECORD_X11="$SCRIPT_DIR/record-x11.sh"
RECORD_TERMINAL="$SCRIPT_DIR/record-terminal.sh"
OUT_DIR="$SCRIPT_DIR/out"
POOL=2
BASE_DISPLAY=100
BASE_PORT=46000
PORT_STEP=10
SIZE="1280x800"
KEEP_STATUS=0
STATUS_ROOT=""
declare -a JOB_SPECS=()

log() { echo "[$SCRIPT_NAME] $*" >&2; }
die() { echo "[$SCRIPT_NAME] ERROR: $*" >&2; exit 1; }

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME --job '<spec>' [--job '<spec>' ...] [options]

At least one --job is required. See the top of this script for the full
job-spec field list (name/type/driver/duration/output/args).

Options:
  --pool N              Max concurrent VIDEO (x11grab) jobs (default: 2).
                         Terminal (asciinema) jobs are never pool-limited.
  --base-display N        First Xvfb display NUMBER to use; job i gets
                         display :N+i (default: 100 — never :99, that's
                         used by manual/other tooling on this box).
  --base-port N             First daemon port to use; job i gets port
                         N+i*step (default: 46000). Must stay clear of
                         37749/37750 (the real daemon) and any other
                         range in use on this box.
  --port-step N               Port spacing between jobs (default: 10 — a
                         daemon uses its port and port+1 for the dashboard;
                         the gap leaves room for the worker's own dynamic
                         port allocation without colliding with the next
                         job's daemon).
  --size WxH                    Virtual screen size for video jobs (default:
                         1280x800)
  --out-dir DIR                  Default directory for job output gifs when a
                         job doesn't set output= itself (default:
                         scripts/demos/out)
  --record-x11 PATH               Path to record-x11.sh (default: alongside
                         this script)
  --record-terminal PATH           Path to record-terminal.sh (default:
                         alongside this script)
  --keep-status                     Do not delete the per-run status dir
                         (job logs, .rc files, resource markers) on exit
  -h, --help                         Show this help and exit
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --job) JOB_SPECS+=("$2"); shift 2 ;;
    --pool) POOL="$2"; shift 2 ;;
    --base-display) BASE_DISPLAY="$2"; shift 2 ;;
    --base-port) BASE_PORT="$2"; shift 2 ;;
    --port-step) PORT_STEP="$2"; shift 2 ;;
    --size) SIZE="$2"; shift 2 ;;
    --out-dir) OUT_DIR="$2"; shift 2 ;;
    --record-x11) RECORD_X11="$2"; shift 2 ;;
    --record-terminal) RECORD_TERMINAL="$2"; shift 2 ;;
    --keep-status) KEEP_STATUS=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
done

[[ ${#JOB_SPECS[@]} -gt 0 ]] || { usage; die "at least one --job is required"; }
[[ -x "$RECORD_X11" ]] || die "record-x11.sh not found/executable at $RECORD_X11"
[[ -x "$RECORD_TERMINAL" ]] || die "record-terminal.sh not found/executable at $RECORD_TERMINAL"
for tool in ffprobe ps; do
  command -v "$tool" >/dev/null 2>&1 || die "'$tool' not found in PATH"
done

mkdir -p "$OUT_DIR"
STATUS_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/record-orchestrator.XXXXXX")"
log "status dir: $STATUS_ROOT"

# --- parse one job spec into an assoc array (via nameref) -----------------
# Fields with no default cause the job to run with that feature off/empty.
parse_job_spec() {
  local spec="$1"
  local -n out=$2
  out[name]=""
  out[type]=""
  out[driver]=""
  out[duration]="6"
  out[output]=""
  out[args]=""
  local IFS=';'
  local -a pairs
  read -ra pairs <<<"$spec"
  local pair key val
  for pair in "${pairs[@]}"; do
    [[ -z "$pair" ]] && continue
    key="${pair%%=*}"
    val="${pair#*=}"
    case "$key" in
      name|type|driver|duration|output|args) out[$key]="$val" ;;
      *) die "unknown job field '$key' in spec: $spec" ;;
    esac
  done
  [[ -n "${out[name]}" ]] || die "job spec missing name=: $spec"
  [[ "${out[type]}" == "video" || "${out[type]}" == "terminal" ]] || die "job '${out[name]}': type must be video or terminal"
  [[ -n "${out[driver]}" ]] || die "job '${out[name]}': driver= is required"
  [[ -x "${out[driver]}" ]] || die "job '${out[name]}': driver not found/executable: ${out[driver]}"
}

# --- process-tree kill, by pid only, never by name -------------------------
kill_tree() {
  local pid="$1"
  [[ -n "$pid" ]] || return 0
  local children
  children="$(pgrep -P "$pid" 2>/dev/null || true)"
  local c
  for c in $children; do kill_tree "$c"; done
  kill -TERM "$pid" 2>/dev/null || true
}

declare -a ALL_JOB_PIDS=()
declare -a ALL_JOB_NAMES=()
declare -a RUNNING_VIDEO_PIDS=()

cleanup_orchestrator() {
  local exit_code=$?
  # Only fires on our own interruption (Ctrl-C etc) or an unexpected error —
  # on the normal path all job wrapper processes have already exited by the
  # time we get here. Kill whatever job wrappers are still alive; each one's
  # own trap (record-x11.sh's or record-terminal.sh's) handles its Xvfb/
  # driver/daemon/browser tree.
  local pid
  for pid in "${ALL_JOB_PIDS[@]}"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      log "orchestrator exiting — stopping still-running job wrapper pid $pid"
      kill_tree "$pid"
    fi
  done
  if [[ "$KEEP_STATUS" -eq 0 ]]; then
    rm -rf "$STATUS_ROOT"
  else
    log "status dir kept at $STATUS_ROOT"
  fi
  exit "$exit_code"
}
trap cleanup_orchestrator EXIT INT TERM

# --- launch one job (in the background) ------------------------------------
run_job() {
  local spec="$1"
  local idx="$2"
  declare -A job
  parse_job_spec "$spec" job

  local name="${job[name]}" type="${job[type]}" driver="${job[driver]}"
  local duration="${job[duration]}" args="${job[args]}"
  local output="${job[output]}"
  [[ -n "$output" ]] || output="$OUT_DIR/$name.gif"
  mkdir -p "$(dirname "$output")"

  local display_num=":$((BASE_DISPLAY + idx))"
  local mcp_port=$((BASE_PORT + idx * PORT_STEP))
  local data_dir
  data_dir="$(mktemp -d "$STATUS_ROOT/${name}-data.XXXXXX")"

  # Resource markers for the final sweep — the ONLY thing that identifies
  # "processes this job owns" to the rest of this script.
  {
    echo "MCP_PORT=$mcp_port"
    echo "DATA_DIR=$data_dir"
    echo "DISPLAY_NUM=$display_num"
    echo "TYPE=$type"
  } >"$STATUS_ROOT/$name.markers"

  local driver_cmd="\"$driver\" --mcp-port $mcp_port --data-dir \"$data_dir\""
  [[ "$type" == "video" ]] && driver_cmd="$driver_cmd --duration $duration"
  [[ -n "$args" ]] && driver_cmd="$driver_cmd $args"

  log "job '$name' ($type): display=$display_num mcp-port=$mcp_port data-dir=$data_dir output=$output"

  case "$type" in
    video)
      (
        "$RECORD_X11" \
          --display "$display_num" \
          --size "$SIZE" \
          --duration "$duration" \
          --output "$output" \
          --command "$driver_cmd" \
          >"$STATUS_ROOT/$name.log" 2>&1
        echo $? >"$STATUS_ROOT/$name.rc"
      ) &
      ;;
    terminal)
      local term_driver_cmd="\"$driver\" --mcp-port $mcp_port --data-dir \"$data_dir\""
      [[ -n "$args" ]] && term_driver_cmd="$term_driver_cmd $args"
      (
        "$RECORD_TERMINAL" \
          --output "$output" \
          --command "$term_driver_cmd" \
          >"$STATUS_ROOT/$name.log" 2>&1
        echo $? >"$STATUS_ROOT/$name.rc"
      ) &
      ;;
  esac
  local pid=$!
  ALL_JOB_PIDS+=("$pid")
  ALL_JOB_NAMES+=("$name")
  echo "$output" >"$STATUS_ROOT/$name.output"
  if [[ "$type" == "video" ]]; then
    RUNNING_VIDEO_PIDS+=("$pid")
  fi
}

# --- prune RUNNING_VIDEO_PIDS of anything that already finished ------------
prune_video_pool() {
  local -a still_running=()
  local p
  for p in "${RUNNING_VIDEO_PIDS[@]}"; do
    kill -0 "$p" 2>/dev/null && still_running+=("$p")
  done
  RUNNING_VIDEO_PIDS=("${still_running[@]}")
}

wait_for_video_pool_slot() {
  prune_video_pool
  while [[ ${#RUNNING_VIDEO_PIDS[@]} -ge $POOL ]]; do
    sleep 1
    prune_video_pool
  done
}

# --- launch every job, bounding only the video pool -------------------------
idx=0
for spec in "${JOB_SPECS[@]}"; do
  # Peek the type before committing the slot so terminal jobs never wait on
  # the video pool.
  type_field="$(printf '%s' "$spec" | grep -o 'type=[a-z]*' | head -1 | cut -d= -f2)"
  if [[ "$type_field" == "video" ]]; then
    wait_for_video_pool_slot
  fi
  run_job "$spec" "$idx"
  idx=$((idx + 1))
done

log "all ${#ALL_JOB_PIDS[@]} job(s) launched, waiting for completion"
for pid in "${ALL_JOB_PIDS[@]}"; do
  wait "$pid" 2>/dev/null || true
done
log "all jobs finished"

# --- verify each job's output gif -------------------------------------------
verify_gif() {
  local path="$1"
  [[ -s "$path" ]] || { echo "empty-or-missing||0"; return 1; }
  local dims frames bytes
  dims="$(ffprobe -v error -select_streams v -show_entries stream=width,height -of csv=p=0:s=x "$path" 2>/dev/null || echo unknown)"
  frames="$(ffprobe -v error -select_streams v -show_entries stream=nb_frames -of default=noprint_wrappers=1:nokey=1 "$path" 2>/dev/null || echo "")"
  if [[ -z "$frames" || "$frames" == "N/A" ]]; then
    frames="$(ffprobe -v error -count_frames -select_streams v -show_entries stream=nb_read_frames -of default=noprint_wrappers=1:nokey=1 "$path" 2>/dev/null || echo 0)"
  fi
  bytes="$(stat -c%s "$path" 2>/dev/null || echo 0)"
  echo "$dims|$frames|$bytes"
  [[ "$bytes" -gt 0 ]] && [[ "$frames" =~ ^[0-9]+$ ]] && [[ "$frames" -ge 2 ]]
}

overall_ok=1
log "----- results -----"
for name in "${ALL_JOB_NAMES[@]}"; do
  rc="$(cat "$STATUS_ROOT/$name.rc" 2>/dev/null || echo unknown)"
  output="$(cat "$STATUS_ROOT/$name.output" 2>/dev/null || echo "")"
  verdict="$(verify_gif "$output")"
  gif_ok=$?
  status="PASS"
  if [[ "$rc" != "0" || "$gif_ok" -ne 0 ]]; then
    status="FAIL"
    overall_ok=0
  fi
  IFS='|' read -r dims frames bytes <<<"$verdict"
  log "$status  job=$name  recorder-exit=$rc  output=$output  dims=$dims  frames=$frames  bytes=$bytes"
  [[ "$status" == "FAIL" ]] && log "  see $STATUS_ROOT/$name.log for detail"
done

# --- sweep: confirm no process this run started is still alive -------------
# Match ONLY by this run's own markers (its data-dir path appearing in a
# process's argv) — never by process name, so other SageFs daemons already
# running on this box are left completely alone.
#
# The process snapshot is taken ONCE, into a variable, before any per-job
# filtering runs. Filtering with awk/grep afterwards means those filter
# processes never existed yet when the snapshot was taken, so they can
# never show up as a false "stray" match against their own --data-dir
# argument (a live `ps | awk -v d=...` pipe self-matches on exactly this).
log "----- sweep -----"
ps_snapshot="$(ps -eo pid=,args=)"
strays_found=0
for name in "${ALL_JOB_NAMES[@]}"; do
  markers_file="$STATUS_ROOT/$name.markers"
  [[ -f "$markers_file" ]] || continue
  # shellcheck disable=SC1090
  source "$markers_file"
  while read -r pid args; do
    [[ -z "$pid" ]] && continue
    strays_found=1
    log "stray process for job '$name': pid=$pid args=$args"
    kill -9 "$pid" 2>/dev/null || true
    log "  killed pid $pid"
  done < <(printf '%s\n' "$ps_snapshot" | awk -v d="$DATA_DIR" '$0 ~ d { $1=$1; print }')
done

if [[ "$strays_found" -eq 0 ]]; then
  log "no stray processes found for any job — all daemons/workers/browsers/Xvfb torn down cleanly"
else
  log "stray processes were found and killed above — investigate the driver's own cleanup if this recurs"
fi

log "----- summary -----"
if [[ "$overall_ok" -eq 1 && "$strays_found" -eq 0 ]]; then
  log "all jobs PASSED, no leftover processes"
  exit 0
else
  log "one or more jobs FAILED or left stray processes — see above"
  exit 1
fi
