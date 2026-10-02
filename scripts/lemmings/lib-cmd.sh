# scripts/lemmings/lib-cmd.sh -- sourced, never executed.
#
# Process plumbing for lemmings that are Command Code (cmdc) on FREE models, all clients of
# the ONE shared SageFs daemon on 37749 (Will's dev daemon; he watches every lemming in its
# dashboard). Every piece of logic lives in F# (scripts/lemmings/LemScore); this file only
# starts processes, builds the bubblewrap command line and reads exit codes.
#
# The contract the editor harnesses (nvim, vscode) source:
#
#   lem_assert_free_model <model>     exit 2 unless the live `cmdc --list-models` marks it FREE
#   lem_use_shared_daemon             checks the shared daemon, never starts one. Sets LEM_PORT,
#                                     LEM_DASH_PORT, LEM_SAGEFS_VERSION (+ the start readings).
#                                     exit 3 when it is unreachable, unhealthy or short of room
#   lem_cleanup_sessions <workdir>    reads the sessions under <workdir> FIRST, stops exactly those
#                                     by id, verifies. Sets LEM_CLEANUP=clean|residue-stopped|failed
#   lem_run_cmdc <workdir> <prompt-file> <model> <max-turns> <events-file> <stderr-file>
#                                     cmdc -p under bubblewrap in <workdir>. Honours LEM_EXTRA_BWRAP
#                                     (array of bwrap args) and LEM_EXTRA_ENV (array of NAME=VALUE).
#                                     Sets LEM_EXIT and LEM_SECONDS
#   lem_score <run-dir> <task> <model> <harness> <oracle-exit>
#                                     writes <run-dir>/out/summary.json (harness: cmdc|cmdc-nvim|
#                                     cmdc-vscode; oracle-exit: an exit code, or "skip")
#
# Layout the functions assume: <workdir> is <run-dir>/w, and the run directory is named
# <model-short>-<task>-<nn> so the lemming is recognisable in the dashboard. The harness's own
# files go in <run-dir>/out: events.ndjson, residue.json, oracle.out, changed.txt, summary.json,
# and optionally fellover.extra.json ([{"stage","symptom","evidence"}]) from an editor driver.
#
# Helpers beyond the contract: lem_run_id, lem_install_workspace, lem_run_oracle,
# lem_sessions_under, lem_prepare_bridge.
#
# What this file does NOT wall off (the README's Isolation section says the same, and every
# summary.json lists it under knownLimits):
#   * Code the lemming sends to SageFs (send_fsharp_code, hot reload, the builds that
#     create_project_session starts) runs in the SHARED DAEMON's processes, outside the sandbox,
#     with the daemon owner's full file access. A lemming can read or write anything that user can
#     through an eval, and can start a session anywhere. Cleanup only stops sessions under the run
#     directory, so a session started elsewhere is reported (an Isolation finding) but not stopped.
#   * The daemon is shared: the sessions list shows other agents' sessions and project paths, and
#     stop_session, switch_session and release_work_lease can reach sessions that are not the lemming's.
#   * cmdc's credential (~/.commandcode/auth.json) is readable by the model, the network is open and
#     cmdc runs with --yolo.
# The sandbox protects the host from the lemming's own processes. It is not a policy on the daemon.
#
# Environment: LEM_REQUIRE_SAME_VERSION=1 makes a bridge that is a different commit from the daemon
# a refusal (exit 4) instead of a recorded finding.

LEM_LIB_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
LEM_ROOT=${LEM_ROOT:-/tmp/lem}
LEM_TOOL_DLL=$LEM_LIB_DIR/LemScore/bin/Release/net11.0/LemScore.dll
LEM_TIMEOUT_SECONDS=${LEM_TIMEOUT_SECONDS:-1500}
LEM_KILL_AFTER_SECONDS=30
LEM_WATCH_STOP_SECONDS=15
LEM_MODEL_CHECKED=

# ---- the F# tool --------------------------------------------------------------------------

# Builds an F# project under scripts/lemmings when its dll is missing or older than one of its
# (or LemScore's) source files. flock keeps two lemmings starting together from building at once.
#   lem_ensure_project <project-dir-name> <dll-path>
lem_ensure_project() {
  local name=${1:?project} dll=${2:?dll} stale=
  if [ ! -f "$dll" ]; then
    stale=1
  elif [ -n "$(find "$LEM_LIB_DIR/$name" "$LEM_LIB_DIR/LemScore" -maxdepth 1 \( -name '*.fs' -o -name '*.fsproj' \) -newer "$dll" -print -quit)" ]; then
    stale=1
  fi
  [ -z "$stale" ] && return 0
  echo "lem: building $name" >&2
  mkdir -p "$LEM_ROOT"
  flock "$LEM_ROOT/.lemscore-build.lock" \
    dotnet build "$LEM_LIB_DIR/$name/$name.fsproj" -c Release -nologo -v quiet >&2 \
    || { echo "lem: $name failed to build" >&2; exit 4; }
}

lem_ensure_tool() { lem_ensure_project LemScore "$LEM_TOOL_DLL"; }

lem_tool() {
  lem_ensure_tool
  DOTNET_NOLOGO=1 dotnet "$LEM_TOOL_DLL" "$@"
}

# ---- contract ----------------------------------------------------------------------------

lem_assert_free_model() {
  local model=${1:?model}
  if lem_tool free-model "$model" >&2; then
    LEM_MODEL_CHECKED=$model
  else
    echo "lem: $model refused. Lemmings run on FREE models only." >&2
    exit 2
  fi
}

lem_use_shared_daemon() {
  local out
  out=$(lem_tool daemon-check) || { echo "lem: refusing to run without a healthy shared daemon" >&2; exit 3; }
  local k v
  while IFS='=' read -r k v; do
    case $k in
      LEM_PORT) LEM_PORT=$v ;;
      LEM_DASH_PORT) LEM_DASH_PORT=$v ;;
      LEM_DAEMON_VERSION) LEM_DAEMON_VERSION=$v ;;
      LEM_MEM_PRESSURE_START) LEM_MEM_PRESSURE_START=$v ;;
      LEM_MEM_AVAIL_START) LEM_MEM_AVAIL_START=$v ;;
      LEM_LEASES_START) LEM_LEASES_START=$v ;;
    esac
  done <<< "$out"
  LEM_SAGEFS_VERSION="daemon $LEM_DAEMON_VERSION"
  LEM_BRIDGE_DESC=
  LEM_BRIDGE_VERSION=unknown
  LEM_VERSION_SKEW=unknown
}

lem_cleanup_sessions() {
  local workdir=${1:?workdir} out k v
  local run_dir; run_dir=$(dirname "$workdir")
  mkdir -p "$run_dir/out"
  LEM_CLEANUP=failed
  out=$(lem_tool cleanup --workdir "$workdir" --out "$run_dir/out/residue.json" --port "${LEM_PORT:-37749}") || return 0
  while IFS='=' read -r k v; do
    [ "$k" = LEM_CLEANUP ] && LEM_CLEANUP=$v
  done <<< "$out"
  return 0
}

# ---- the SageFs the lemming gets -----------------------------------------------------------

# The dev build of the main checkout unless SAGEFS_LEMMING_BIN says otherwise:
#   unset      <main checkout>/SageFs/bin/Release/net11.0/SageFs.dll, copied into the run dir
#   published  the global tool `sagefs` (baseline comparison)
#   <path>     a SageFs.dll (or a directory holding one), copied into the run dir
# A copy (reflink where the filesystem has it) means a rebuild cannot change a running trial.
# On tmpfs there is no reflink, so each run holds a real 380 MB copy; point LEM_ROOT at btrfs
# if that matters.
lem_main_checkout() {
  local common
  common=$(git -C "$LEM_LIB_DIR" rev-parse --git-common-dir 2>/dev/null) || { echo "$HOME/Work/SageFs"; return; }
  (cd "$LEM_LIB_DIR" && cd "$(dirname "$common")" && pwd)
}

# Sets LEM_BRIDGE_CMD (array) and LEM_BRIDGE_DESC, and puts the version into LEM_SAGEFS_VERSION.
lem_prepare_bridge() {
  local run_dir=${1:?run-dir} bin=${SAGEFS_LEMMING_BIN:-} dll version
  [ "${LEM_BRIDGE_READY:-}" = "$run_dir" ] && return 0
  if [ "$bin" = published ]; then
    version=$(sagefs --version 2>/dev/null | head -n 1)
    LEM_BRIDGE_CMD=(sagefs mcp)
    LEM_BRIDGE_DESC="published tool: ${version:-unknown}"
    LEM_BRIDGE_VERSION=${version:-unknown}
  else
    if [ -z "$bin" ]; then
      dll=$(lem_main_checkout)/SageFs/bin/Release/net11.0/SageFs.dll
    elif [ -d "$bin" ]; then
      dll=$bin/SageFs.dll
    else
      dll=$bin
    fi
    [ -f "$dll" ] || { echo "lem: no SageFs.dll at $dll (build master, or set SAGEFS_LEMMING_BIN)" >&2; exit 4; }
    # Read the version from the source and judge the skew BEFORE the 380 MB copy, so a refusal
    # leaves nothing behind.
    version=$(lem_tool dll-version "$dll")
    LEM_BRIDGE_CMD=(dotnet "$run_dir/bin/sagefs/$(basename "$dll")" mcp)
    LEM_BRIDGE_DESC="dev build: $version"
    LEM_BRIDGE_VERSION=$version
  fi
  LEM_SAGEFS_VERSION="daemon ${LEM_DAEMON_VERSION:-unknown}; bridge $LEM_BRIDGE_DESC"
  lem_check_version_skew
  mkdir -p "$run_dir/bin"
  if [ "$bin" != published ] && [ ! -d "$run_dir/bin/sagefs" ]; then
    cp -r --reflink=auto "$(dirname "$dll")" "$run_dir/bin/sagefs"
  fi
  LEM_BRIDGE_READY=$run_dir
}

# Compares the bridge the lemming runs with the shared daemon it talks to. A different commit can
# change the protocol or the tool surface, and that would be blamed on the model or on SageFs, so
# it is said out loud here, recorded in summary.json (versionSkew, a Preflight finding), and
# refused when LEM_REQUIRE_SAME_VERSION=1. The daemon's own dll cannot be used instead: the file it
# started from may have been rebuilt since, so the only honest thing is to compare what is running.
# Sets LEM_VERSION_SKEW (same|skewed|unknown).
lem_check_version_skew() {
  local out k v
  LEM_VERSION_SKEW=unknown
  out=$(lem_tool version-skew --daemon "${LEM_DAEMON_VERSION:-unknown}" --bridge "${LEM_BRIDGE_VERSION:-unknown}") || return 0
  while IFS='=' read -r k v; do
    case $k in
      LEM_VERSION_SKEW) LEM_VERSION_SKEW=$v ;;
      LEM_BRIDGE_VERSION) LEM_BRIDGE_VERSION=$v ;;
    esac
  done <<< "$out"
  [ "$LEM_VERSION_SKEW" = skewed ] || return 0
  echo "lem: VERSION SKEW: the lemming's bridge is $LEM_BRIDGE_VERSION but the shared daemon is $LEM_DAEMON_VERSION." >&2
  echo "lem: a difference in protocol or tools may be the skew, not the model. Rebuild master and restart the daemon, or point SAGEFS_LEMMING_BIN at the build the daemon runs." >&2
  if [ -n "${LEM_REQUIRE_SAME_VERSION:-}" ]; then
    echo "lem: refusing (LEM_REQUIRE_SAME_VERSION is set)" >&2
    exit 4
  fi
}

# The skill where Command Code finds project skills, and the MCP registration written the way
# the README says (`sagefs mcp`), pointed at the shared daemon. Nothing in either says how
# SageFs works beyond what the skill itself teaches. Idempotent.
lem_install_workspace() {
  local workdir=${1:?workdir} repo; repo=$(cd "$LEM_LIB_DIR/../.." && pwd)
  mkdir -p "$workdir/.commandcode/skills"
  [ -d "$workdir/.commandcode/skills/sagefs" ] || cp -r --reflink=auto "$repo/skills/sagefs" "$workdir/.commandcode/skills/sagefs"
  local cmd=${LEM_BRIDGE_CMD[0]} args="" a
  for a in "${LEM_BRIDGE_CMD[@]:1}"; do args="$args\"$a\", "; done
  args=${args%, }
  cat > "$workdir/.mcp.json" <<JSON
{ "mcpServers": { "sagefs": { "command": "$cmd", "args": [$args],
  "env": { "SAGEFS_MCP_PORT": "${LEM_PORT:-37749}" } } } }
JSON
  # Harness files stay out of `git status`, so the changed-files list is the lemming's work.
  if [ -d "$workdir/.git" ]; then
    printf '%s\n' '.commandcode/' '.mcp.json' >> "$workdir/.git/info/exclude"
  fi
}

# ---- running the lemming ------------------------------------------------------------------

# The sandbox every lemming-facing process shares, in LEM_BW: the toolchain read-only, a tmpfs
# HOME, its own pid/ipc/uts namespaces (so it cannot see or signal any host process, the shared
# daemon included), a cleared environment, and only <workdir>, the bridge copy (read-only) and
# a few scratch dirs writable. The network is NOT isolated: the model API and localhost are
# reachable, because the lemming's MCP bridge talks to the daemon over localhost. That also means
# the shared daemon's HTTP API is reachable, and what the daemon does for the lemming is outside
# this sandbox (see the header). The NuGet cache is the user's, read-only: restores resolve from it
# through a fallback folder and write new packages to a per-run folder, so a lemming cannot
# poison the shared cache.
lem_bw_base() {
  local run_dir=${1:?run-dir} workdir=${2:?workdir} extra_path=${3:-}
  mkdir -p "$run_dir/dotnethome" "$run_dir/out/sbx" "$run_dir/bin" "$run_dir/nuget" "$HOME/.nuget/packages"
  LEM_BW=(
    --die-with-parent --unshare-pid --unshare-ipc --unshare-uts --clearenv
    --ro-bind /usr /usr --ro-bind /etc /etc
    --symlink usr/bin /bin --symlink usr/sbin /sbin --symlink usr/lib /lib --symlink usr/lib64 /lib64
    --ro-bind /run/systemd/resolve /run/systemd/resolve
    --proc /proc --dev /dev --tmpfs /tmp --tmpfs "$HOME"
    --ro-bind "$HOME/.dotnet" "$HOME/.dotnet"
    --ro-bind "$HOME/.local/share/mise" "$HOME/.local/share/mise"
    --ro-bind "$HOME/.nuget/packages" "$HOME/.nuget/packages"
    --bind "$run_dir/nuget" "$run_dir/nuget"
    --bind "$workdir" "$workdir"
    --ro-bind "$run_dir/bin" "$run_dir/bin"
    --bind "$run_dir/dotnethome" "$run_dir/dotnethome"
    --bind "$run_dir/out/sbx" "$run_dir/out/sbx"
    --chdir "$workdir"
    --setenv HOME "$HOME" --setenv TERM dumb --setenv LANG C.UTF-8
    --setenv PATH "$HOME/.dotnet:$HOME/.dotnet/tools:${extra_path:+$extra_path:}/usr/bin:/bin"
    --setenv DOTNET_ROOT "$HOME/.dotnet" --setenv DOTNET_CLI_HOME "$run_dir/dotnethome"
    --setenv NUGET_PACKAGES "$run_dir/nuget" --setenv NUGET_FALLBACK_PACKAGES "$HOME/.nuget/packages"
    --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 --setenv DOTNET_NOLOGO 1 --setenv DOTNET_SKIP_FIRST_TIME_EXPERIENCE 1
  )
}

# Runs a command in the same sandbox, with NO network and no credentials, for oracles that must
# execute code the lemming wrote (a test run). lem_sandbox_exec <run-dir> <seconds> cmd args...
# LEM_ORACLE_NET=1 keeps the network for a restore that needs it.
lem_sandbox_exec() {
  local run_dir=${1:?run-dir} seconds=${2:?seconds}; shift 2
  lem_bw_base "$run_dir" "$run_dir/w"
  local -a bw=("${LEM_BW[@]}")
  [ -n "${LEM_ORACLE_NET:-}" ] || bw+=(--unshare-net)
  bw+=(${LEM_EXTRA_BWRAP[@]+"${LEM_EXTRA_BWRAP[@]}"})
  bwrap "${bw[@]}" timeout --kill-after="$LEM_KILL_AFTER_SECONDS" "$seconds" "$@"
}

lem_run_cmdc() {
  local workdir=${1:?workdir} prompt_file=${2:?prompt-file} model=${3:?model} turns=${4:?max-turns}
  local events=${5:?events-file} stderr_file=${6:?stderr-file}
  local run_dir; run_dir=$(dirname "$workdir")
  [ "$LEM_MODEL_CHECKED" = "$model" ] || lem_assert_free_model "$model"
  [ -n "${LEM_PORT:-}" ] || { echo "lem: call lem_use_shared_daemon before lem_run_cmdc" >&2; exit 3; }
  local cmdc_path; cmdc_path=$(command -v cmdc) || { echo "lem: cmdc is not on PATH" >&2; exit 4; }
  local cmdc_dir; cmdc_dir=$(dirname "$cmdc_path")
  case $(readlink -f "$cmdc_path") in
    "$HOME"/.local/share/mise/*) ;;
    *) echo "lem: cmdc is not under ~/.local/share/mise, which is the only toolchain the sandbox binds" >&2; exit 4 ;;
  esac

  lem_prepare_bridge "$run_dir"
  lem_install_workspace "$workdir"
  mkdir -p "$run_dir/cmdchome" "$run_dir/dotnethome" "$run_dir/out/sbx"
  : > "$run_dir/cmdchome/auth.json"   # bwrap's mount point; the credential itself is bound over it

  lem_bw_base "$run_dir" "$workdir" "$cmdc_dir"
  local -a bw=("${LEM_BW[@]}"
    --bind "$run_dir/cmdchome" "$HOME/.commandcode"
    --ro-bind "$HOME/.commandcode/auth.json" "$HOME/.commandcode/auth.json"
    --setenv COMMANDCODE_SKIP_UPDATES 1)
  local e
  for e in "${LEM_EXTRA_ENV[@]:-}"; do
    [ -n "$e" ] && bw+=(--setenv "${e%%=*}" "${e#*=}")
  done
  bw+=(${LEM_EXTRA_BWRAP[@]+"${LEM_EXTRA_BWRAP[@]}"})

  # While the lemming runs, a watcher reads the shared daemon's sessions list (out/sessions.seen.json):
  # the proof that this lemming's sessions showed up in the dashboard Will is watching.
  rm -f "$run_dir/out/watch.stop"
  lem_ensure_tool
  DOTNET_NOLOGO=1 dotnet "$LEM_TOOL_DLL" watch --workdir "$workdir" --out "$run_dir/out/sessions.seen.json" \
    --stop-file "$run_dir/out/watch.stop" --port "$LEM_PORT" > /dev/null 2>&1 &
  local watch_pid=$!

  local prompt; prompt=$(cat "$prompt_file")
  local start; start=$(date +%s)
  set +e
  # After cmdc exits, list what is still alive in the sandbox (out/sbx/ps.txt): the residue
  # of processes. Everything in the sandbox dies with it, so this is a record, not a cleanup.
  bwrap "${bw[@]}" bash -c '
    timeout --kill-after="$1" "$2" cmdc -p "$3" --model "$4" --max-turns "$5" \
      --output-format json --yolo --no-session --skip-onboarding --no-auto-update
    rc=$?
    ps -eo pid,ppid,etimes,args --no-headers > "$6/ps.txt" 2>/dev/null
    exit $rc' _ "$LEM_KILL_AFTER_SECONDS" "$LEM_TIMEOUT_SECONDS" "$prompt" "$model" "$turns" "$run_dir/out/sbx" \
    > "$events" 2> "$stderr_file"
  LEM_EXIT=$?
  set -e
  LEM_SECONDS=$(( $(date +%s) - start ))
  # Stop the watcher: it ends itself on the stop file; if it does not, kill that exact pid.
  : > "$run_dir/out/watch.stop"
  local waited=0
  while kill -0 "$watch_pid" 2>/dev/null && [ "$waited" -lt "$LEM_WATCH_STOP_SECONDS" ]; do
    sleep 1; waited=$((waited + 1))
  done
  kill -0 "$watch_pid" 2>/dev/null && kill "$watch_pid" 2>/dev/null
  wait "$watch_pid" 2>/dev/null || true
}

# Expecto colours its summary line; oracles that read it strip the escape codes first.
lem_strip_ansi() { sed -E 's/\x1B\[[0-9;?]*[A-Za-z]//g'; }

# Runs scripts/lemmings/oracles/<task>.sh <run-dir> OUTSIDE the sandbox. Sets LEM_ORACLE_EXIT
# (the script's exit code, or "skip" when the task has no oracle). Output goes to out/oracle.out.
lem_run_oracle() {
  local run_dir=${1:?run-dir} task=${2:?task}
  local oracle=$LEM_LIB_DIR/oracles/$task.sh
  if [ ! -x "$oracle" ]; then LEM_ORACLE_EXIT=skip; return 0; fi
  set +e
  timeout 900 "$oracle" "$run_dir" > "$run_dir/out/oracle.out" 2>&1
  LEM_ORACLE_EXIT=$?
  set -e
}

lem_score() {
  local run_dir=${1:?run-dir} task=${2:?task} model=${3:?model} harness=${4:?harness} oracle=${5:?oracle-exit}
  lem_tool score --run-dir "$run_dir" --task "$task" --model "$model" --harness "$harness" \
    --oracle-exit "$oracle" --cmdc-exit "${LEM_EXIT:--1}" --seconds "${LEM_SECONDS:-0}" \
    --sagefs-version "${LEM_SAGEFS_VERSION:-unknown}" --daemon-version "${LEM_DAEMON_VERSION:-unknown}" \
    --bridge-version "${LEM_BRIDGE_VERSION:-unknown}" --cleanup "${LEM_CLEANUP:-not-run}" \
    --mem-start "${LEM_MEM_PRESSURE_START:-unknown}" --avail-start "${LEM_MEM_AVAIL_START:-0}" \
    --leases-start "${LEM_LEASES_START:-0}" --port "${LEM_PORT:-37749}" > /dev/null
  cat "$run_dir/out/summary.json"
}

# ---- small helpers ------------------------------------------------------------------------

# The next free <model-short>-<task>-<nn> under LEM_ROOT.
lem_run_id() { lem_tool run-id "$LEM_ROOT" "${1:?model}" "${2:?task}"; }

# Prints "<id> <status>" for each session on the shared daemon under <workdir>.
lem_sessions_under() { lem_tool sessions-under --workdir "${1:?workdir}" --port "${LEM_PORT:-37749}"; }
