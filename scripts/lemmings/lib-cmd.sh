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
#                                     by id, verifies. Sets LEM_CLEANUP=clean|residue-stopped|failed.
#                                     LEM_OWN_SESSIONS (comma separated ids) names sessions the harness
#                                     made for the run: stopped with the rest, not counted as residue
#   lem_daemon_pid                    the pid listening on the shared daemon's port (before/after a run)
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
# lem_sessions_under.

LEM_LIB_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
LEM_ROOT=${LEM_ROOT:-/tmp/lem}
LEM_TOOL_DLL=$LEM_LIB_DIR/LemScore/bin/Release/net11.0/LemScore.dll
LEM_TIMEOUT_SECONDS=${LEM_TIMEOUT_SECONDS:-1500}
LEM_KILL_AFTER_SECONDS=30
LEM_MODEL_CHECKED=

# ---- the F# tool --------------------------------------------------------------------------

# Builds LemScore when the dll is missing or older than a source file. flock keeps two
# lemmings starting together from building at once.
lem_ensure_tool() {
  local stale=
  if [ ! -f "$LEM_TOOL_DLL" ]; then
    stale=1
  elif [ -n "$(find "$LEM_LIB_DIR/LemScore" -maxdepth 1 \( -name '*.fs' -o -name '*.fsproj' \) -newer "$LEM_TOOL_DLL" -print -quit)" ]; then
    stale=1
  fi
  [ -z "$stale" ] && return 0
  echo "lem: building LemScore" >&2
  mkdir -p "$LEM_ROOT"; flock "$LEM_ROOT/.lemscore-build.lock" \
    dotnet build "$LEM_LIB_DIR/LemScore/LemScore.fsproj" -c Release -nologo -v quiet >&2 \
    || { echo "lem: LemScore failed to build" >&2; exit 4; }
}

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
}

lem_cleanup_sessions() {
  local workdir=${1:?workdir} out k v
  local run_dir; run_dir=$(dirname "$workdir")
  mkdir -p "$run_dir/out"
  LEM_CLEANUP=failed
  # LEM_OWN_SESSIONS: ids (comma separated) of sessions the harness made for this run. They are
  # stopped with the rest but are not residue.
  out=$(lem_tool cleanup --workdir "$workdir" --out "$run_dir/out/residue.json" --port "${LEM_PORT:-37749}" ${LEM_OWN_SESSIONS:+--own "$LEM_OWN_SESSIONS"}) || return 0
  while IFS='=' read -r k v; do
    [ "$k" = LEM_CLEANUP ] && LEM_CLEANUP=$v
  done <<< "$out"
  return 0
}

# The pid of the process listening on the shared daemon's port, or nothing. Read before and
# after a run: the harness never stops the daemon, so a different pid, or none, afterwards means
# something in the run did, and that is reported instead of assumed away.
lem_daemon_pid() {
  ss -ltnpH "( sport = :${LEM_PORT:-37749} )" 2>/dev/null | grep -o 'pid=[0-9]*' | head -n 1 | cut -d= -f2
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
  mkdir -p "$run_dir/bin"
  if [ "$bin" = published ]; then
    version=$(sagefs --version 2>/dev/null | head -n 1)
    LEM_BRIDGE_CMD=(sagefs mcp)
    LEM_BRIDGE_DESC="published tool: ${version:-unknown}"
  else
    if [ -z "$bin" ]; then
      dll=$(lem_main_checkout)/SageFs/bin/Release/net11.0/SageFs.dll
    elif [ -d "$bin" ]; then
      dll=$bin/SageFs.dll
    else
      dll=$bin
    fi
    [ -f "$dll" ] || { echo "lem: no SageFs.dll at $dll (build master, or set SAGEFS_LEMMING_BIN)" >&2; exit 4; }
    if [ ! -d "$run_dir/bin/sagefs" ]; then
      cp -r --reflink=auto "$(dirname "$dll")" "$run_dir/bin/sagefs"
    fi
    version=$(lem_tool dll-version "$run_dir/bin/sagefs/$(basename "$dll")")
    LEM_BRIDGE_CMD=(dotnet "$run_dir/bin/sagefs/$(basename "$dll")" mcp)
    LEM_BRIDGE_DESC="dev build: $version"
  fi
  LEM_BRIDGE_VERSION=${version:-unknown}
  LEM_SAGEFS_VERSION="daemon ${LEM_DAEMON_VERSION:-unknown}; bridge $LEM_BRIDGE_DESC"
  # Two builds nothing made the same one: the bridge is the main checkout's last build and the
  # daemon is whatever was last started. Both go in the summary's version string, and a
  # difference is a warning, once per run.
  local skew
  skew=$(lem_tool version-skew --daemon "${LEM_DAEMON_VERSION:-unknown}" --bridge "$LEM_BRIDGE_VERSION") || skew=
  if [ -n "$skew" ] && [ -z "${LEM_SKEW_WARNED:-}" ]; then
    LEM_SKEW_WARNED=1
    echo "lem: WARNING: $skew" >&2
  fi
  [ -z "$skew" ] || LEM_SAGEFS_VERSION="$LEM_SAGEFS_VERSION; VERSION SKEW: $skew"
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
# reachable, because the lemming's MCP bridge talks to the daemon over localhost.
lem_bw_base() {
  local run_dir=${1:?run-dir} workdir=${2:?workdir} extra_path=${3:-}
  mkdir -p "$run_dir/dotnethome" "$run_dir/out/sbx" "$run_dir/bin"
  LEM_BW=(
    --die-with-parent --unshare-pid --unshare-ipc --unshare-uts --clearenv
    --ro-bind /usr /usr --ro-bind /etc /etc
    --symlink usr/bin /bin --symlink usr/sbin /sbin --symlink usr/lib /lib --symlink usr/lib64 /lib64
    --ro-bind /run/systemd/resolve /run/systemd/resolve
    --proc /proc --dev /dev --tmpfs /tmp --tmpfs "$HOME"
    --ro-bind "$HOME/.dotnet" "$HOME/.dotnet"
    --ro-bind "$HOME/.local/share/mise" "$HOME/.local/share/mise"
    --bind "$workdir" "$workdir"
    --ro-bind "$run_dir/bin" "$run_dir/bin"
    --bind "$run_dir/dotnethome" "$run_dir/dotnethome"
    --bind "$run_dir/out/sbx" "$run_dir/out/sbx"
    --chdir "$workdir"
    --setenv HOME "$HOME" --setenv TERM dumb --setenv LANG C.UTF-8
    --setenv PATH "$HOME/.dotnet:$HOME/.dotnet/tools:${extra_path:+$extra_path:}/usr/bin:/bin"
    --setenv DOTNET_ROOT "$HOME/.dotnet" --setenv DOTNET_CLI_HOME "$run_dir/dotnethome"
    --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 --setenv DOTNET_NOLOGO 1 --setenv DOTNET_SKIP_FIRST_TIME_EXPERIENCE 1
    --setenv NUGET_PACKAGES "$run_dir/dotnethome/nuget/packages"
    --setenv NUGET_HTTP_CACHE_PATH "$run_dir/dotnethome/nuget/http"
    --setenv NUGET_PLUGINS_CACHE_PATH "$run_dir/dotnethome/nuget/plugins"
  )
  # NuGet: the packages the lemming restores go to a cache of this run's own, and the user's real
  # package cache is only a read-only fallback, so a restore (or a package a lemming wrote over)
  # cannot change what the next build on this machine sees.
  mkdir -p "$run_dir/dotnethome/nuget"
  if [ -d "$HOME/.nuget/packages" ]; then
    LEM_BW+=(--ro-bind "$HOME/.nuget/packages" "$HOME/.nuget/packages" --setenv NUGET_FALLBACK_PACKAGES "$HOME/.nuget/packages")
  fi
  # The global tools (the published `sagefs`, which can stop the shared daemon) are not in the
  # sandbox unless the run uses the published tool as its bridge.
  if [ -n "${LEM_MASK_DOTNET_TOOLS:-}" ] && [ -d "$HOME/.dotnet/tools" ]; then
    LEM_BW+=(--tmpfs "$HOME/.dotnet/tools")
  fi
}

# Runs a command in the same sandbox, with NO network and no credentials, for oracles that must
# execute code the lemming wrote (a test run). lem_sandbox_exec <run-dir> <seconds> cmd args...
# LEM_ORACLE_NET=1 keeps the network for a restore that needs it.
lem_sandbox_exec() {
  local run_dir=${1:?run-dir} seconds=${2:?seconds}; shift 2
  lem_bw_base "$run_dir" "$run_dir/w"
  local -a bw=("${LEM_BW[@]}")
  [ -n "${LEM_ORACLE_NET:-}" ] || bw+=(--unshare-net)
  [ "${#LEM_EXTRA_BWRAP[@]}" -gt 0 ] && bw+=("${LEM_EXTRA_BWRAP[@]}")
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
  [ "${#LEM_EXTRA_BWRAP[@]}" -gt 0 ] && bw+=("${LEM_EXTRA_BWRAP[@]}")

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
}

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
    --sagefs-version "${LEM_SAGEFS_VERSION:-unknown}" --cleanup "${LEM_CLEANUP:-not-run}" \
    --mem-start "${LEM_MEM_PRESSURE_START:-unknown}" --avail-start "${LEM_MEM_AVAIL_START:-0}" \
    --leases-start "${LEM_LEASES_START:-0}" --port "${LEM_PORT:-37749}" > /dev/null
  cat "$run_dir/out/summary.json"
}

# ---- small helpers ------------------------------------------------------------------------

# The next free <model-short>-<task>-<nn> under LEM_ROOT.
lem_run_id() { lem_tool run-id "$LEM_ROOT" "${1:?model}" "${2:?task}"; }

# Prints "<id> <status>" for each session on the shared daemon under <workdir>.
lem_sessions_under() { lem_tool sessions-under --workdir "${1:?workdir}" --port "${LEM_PORT:-37749}"; }
