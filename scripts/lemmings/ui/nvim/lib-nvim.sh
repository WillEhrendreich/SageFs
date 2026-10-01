#!/usr/bin/env bash
# scripts/lemmings/ui/nvim/lib-nvim.sh (sourced, never executed)
#
# Process plumbing for a Neovim lemming: the editor sandbox (bubblewrap + tmux + nvim)
# and the lemming's own sandbox. Every piece of logic lives in the F# LemDrive tool.
#
# Two sandboxes, on purpose:
#   editor sandbox   tmux server + Neovim + a plain shell. The workspace is writable.
#                    The tmux socket lives under out/tmux and is never mounted into the
#                    lemming's sandbox, so the lemming cannot ask tmux for a window of
#                    its own. Anything tmux starts stays inside this sandbox.
#   lemming sandbox  cmdc and the dotnet runtime for the LemDrive client. The workspace is
#                    READ-ONLY here. The only way a file changes is through the editor.
#                    It can reach the driver's unix socket and nothing else of the harness.
#
# Callers set: RUN OUT NVIM_BIN PLUGIN_DIR DRIVE_DIR LABEL before using these.

LEM_NVIM_DEFAULT_BIN=${LEM_NVIM_DEFAULT_BIN:-$HOME/.local/share/bob/nvim-bin/nvim}
LEM_PLUGIN_DEFAULT_DIR=${LEM_PLUGIN_DEFAULT_DIR:-$HOME/Work/sagefs.nvim}
LEM_NODE_DIR=${LEM_NODE_DIR:-$(dirname "$(dirname "$(readlink -f "$(command -v cmdc)")")")}

# Arguments every sandbox shares: the read-only toolchain, a private tmpfs home, nothing else.
lem_base_bwrap() {
  LEM_BASE_BWRAP=(
    --die-with-parent
    --ro-bind /usr /usr --ro-bind /etc /etc
    --symlink usr/bin /bin --symlink usr/sbin /sbin --symlink usr/lib /lib --symlink usr/lib64 /lib64
    --ro-bind /run/systemd/resolve /run/systemd/resolve
    --proc /proc --dev /dev --tmpfs /tmp --tmpfs "$HOME"
  )
}

# The editor sandbox runs `tmux -D`: a foreground server with no session yet. LemDrive
# `nvim serve` then creates the nvim and shell windows through the socket, so the
# commands run here, inside the sandbox, and the sandbox lives exactly as long as tmux.
lem_start_editor_sandbox() {
  lem_base_bwrap
  mkdir -p "$OUT/tmux"
  local bob_dir
  bob_dir=$(dirname "$(dirname "$NVIM_BIN")")
  bwrap "${LEM_BASE_BWRAP[@]}" \
    --ro-bind "$bob_dir" "$bob_dir" \
    --ro-bind "$PLUGIN_DIR" "$PLUGIN_DIR" \
    --bind "$RUN" "$RUN" \
    --tmpfs "$OUT" \
    --bind "$OUT/tmux" "$OUT/tmux" \
    --ro-bind "$OUT/ui" "$OUT/ui" \
    --chdir "$RUN" \
    --setenv HOME "$HOME" \
    --setenv PATH "$bob_dir/nightly/bin:$bob_dir/nvim-bin:/usr/bin:/bin" \
    --setenv LANG C.UTF-8 --setenv LC_ALL C.UTF-8 \
    --setenv TMUX_TMPDIR "$OUT/tmux" \
    --setenv SAGEFS_NVIM_DIR "$PLUGIN_DIR" \
    --setenv LEM_TS_DIR "$OUT/ui/ts" \
    tmux -L "$LABEL" -f /dev/null -D \
    > "$OUT/editor-sandbox.log" 2>&1 &
  LEM_EDITOR_PID=$!
}

# `nvim serve` is the harness side of the driver, run OUTSIDE both sandboxes.
lem_start_driver_server() {
  mkdir -p "$OUT/ipc"
  rm -f "$OUT/ready"
  dotnet "$DRIVE_DIR/LemDrive.dll" nvim serve \
    --tmux-dir "$OUT/tmux" --label "$LABEL" --socket "$OUT/ipc/drive.sock" \
    --out "$OUT" --workspace "$RUN" --nvim "$NVIM_BIN" --init "$OUT/ui/init.lua" \
    --open "$LEM_OPEN_FILE" --ready "$OUT/ready" \
    > "$OUT/driver-server.log" 2>&1 &
  LEM_DRIVER_PID=$!
  local waited=0
  while [ ! -s "$OUT/ready" ] && [ "$waited" -lt "${LEM_DRIVER_START_SECONDS:-90}" ]; do
    kill -0 "$LEM_DRIVER_PID" 2>/dev/null || break
    sleep 1; waited=$((waited + 1))
  done
  grep -q '^ready' "$OUT/ready" 2>/dev/null
}

# The lemming's sandbox. Arguments: prompt file, model, max turns.
# Writes out/events.ndjson and out/cmdc.stderr; sets LEM_EXIT and LEM_SECONDS.
lem_run_ui_cmdc() {
  local prompt_file=$1 model=$2 turns=$3
  lem_base_bwrap
  local start
  start=$(date +%s)
  set +e
  bwrap "${LEM_BASE_BWRAP[@]}" \
    --ro-bind "$LEM_NODE_DIR" "$LEM_NODE_DIR" \
    --ro-bind "$HOME/.dotnet" "$HOME/.dotnet" \
    --ro-bind "$DRIVE_DIR" /lem/drive \
    --ro-bind "$OUT/docs" /lem/docs \
    --ro-bind "$RUN" "$RUN" \
    --tmpfs "$OUT" \
    --bind "$OUT/ipc" "$OUT/ipc" \
    --bind "$OUT/cfg/config.json" "$HOME/.commandcode/config.json" \
    --bind "$OUT/cfg/settings.json" "$HOME/.commandcode/settings.json" \
    --ro-bind "$HOME/.commandcode/auth.json" "$HOME/.commandcode/auth.json" \
    --chdir "$RUN" \
    --setenv HOME "$HOME" \
    --setenv PATH "$LEM_NODE_DIR/bin:/usr/bin:/bin" \
    --setenv LANG C.UTF-8 \
    --setenv DOTNET_ROOT "$HOME/.dotnet" \
    --setenv DOTNET_CLI_HOME /tmp --setenv DOTNET_NOLOGO 1 --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 \
    --setenv LEM_DRIVE_SOCKET "$OUT/ipc/drive.sock" \
    timeout "${LEM_CMDC_TIMEOUT_SECONDS:-1500}" cmdc -p "$(cat "$prompt_file")" \
      --model "$model" --max-turns "$turns" --output-format json \
      --yolo --no-session --skip-onboarding --no-auto-update --no-skills \
    > "$OUT/events.ndjson" 2> "$OUT/cmdc.stderr"
  LEM_EXIT=$?
  set -e
  LEM_SECONDS=$(( $(date +%s) - start ))
}

# Graceful first: ask tmux to end (nvim gets a hangup), wait, force only if it will not go.
lem_stop_editor_sandbox() {
  LEM_EDITOR_STOP=graceful
  TMUX_TMPDIR="$OUT/tmux" tmux -L "$LABEL" kill-server 2>/dev/null || true
  local waited=0
  while kill -0 "$LEM_EDITOR_PID" 2>/dev/null && [ "$waited" -lt 10 ]; do sleep 1; waited=$((waited + 1)); done
  if kill -0 "$LEM_EDITOR_PID" 2>/dev/null; then
    LEM_EDITOR_STOP=forced
    kill "$LEM_EDITOR_PID" 2>/dev/null || true
  fi
}

# Stops the driver server by its exact pid (SIGTERM lets it write the final screen).
lem_stop_driver_server() {
  [ -n "${LEM_DRIVER_PID:-}" ] || return 0
  kill "$LEM_DRIVER_PID" 2>/dev/null || true
  local waited=0
  while kill -0 "$LEM_DRIVER_PID" 2>/dev/null && [ "$waited" -lt 10 ]; do sleep 1; waited=$((waited + 1)); done
  kill -0 "$LEM_DRIVER_PID" 2>/dev/null && kill -9 "$LEM_DRIVER_PID" 2>/dev/null || true
}
