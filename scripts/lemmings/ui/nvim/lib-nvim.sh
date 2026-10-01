#!/usr/bin/env bash
# scripts/lemmings/ui/nvim/lib-nvim.sh (sourced, never executed)
#
# Process plumbing for a Neovim lemming: the editor sandbox (bubblewrap + tmux + nvim) and
# the extra arguments that turn the core lemming sandbox (lib-cmd.sh) into one that can only
# touch the editor through the driver. Every piece of logic lives in the F# LemDrive tool.
#
# Two sandboxes, on purpose:
#   editor sandbox   tmux server + Neovim + a plain shell. The workspace is writable. The tmux
#                    socket lives under out/tmux and is never mounted into the lemming's
#                    sandbox, so the lemming cannot ask tmux for a window of its own.
#   lemming sandbox  cmdc, built by lem_run_cmdc. Here the workspace is READ-ONLY and the only
#                    way a file changes is through the editor. It sees the driver's unix socket
#                    (out/ipc), the driver tool at /lem/drive and, when the task allows it, the
#                    plugin README at /lem/docs. Its .mcp.json is overlaid with an empty server
#                    list: a UI lemming drives the editor, it does not call MCP.
#
# Callers set before using these: RUN W OUT NVIM_BIN PLUGIN_DIR DRIVE_DLL LABEL LEM_OPEN_FILE.

LEM_NVIM_DEFAULT_BIN=${LEM_NVIM_DEFAULT_BIN:-$HOME/.local/share/bob/nvim-bin/nvim}
LEM_PLUGIN_DEFAULT_DIR=${LEM_PLUGIN_DEFAULT_DIR:-$HOME/Work/sagefs.nvim}
LEM_TS_PARSER_DIR=${LEM_TS_PARSER_DIR:-$HOME/.local/share/nvim/site}
LEM_NVIM_HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# Builds LemDrive once, under a lock, into $LEM_ROOT/.lemdrive, then copies it into the run
# (reflink where there is one) so a rebuild cannot change a trial that is already running.
lem_nvim_ensure_drive() {
  local proj=${LEM_DRIVE_PROJECT:-$LEM_NVIM_HERE/LemDriveNvim.fsproj}
  local built=$LEM_ROOT/.lemdrive
  mkdir -p "$LEM_ROOT" "$RUN/bin"
  flock "$LEM_ROOT/.lemdrive-build.lock" \
    dotnet build "$proj" -c Release -nologo -v quiet -o "$built" >&2 \
    || { echo "lem-nvim: LemDrive failed to build" >&2; exit 4; }
  cp -r --reflink=auto "$built" "$RUN/bin/lemdrive"
  DRIVE_DIR=$RUN/bin/lemdrive
  DRIVE_DLL=$DRIVE_DIR/LemDrive.dll
}

# The editor's own files for this run: init.lua, the F# tree-sitter parser, tmux config, an
# empty MCP file, and the docs the lemming may read (the plugin README, when the task says so).
lem_nvim_stage() {
  local want_readme=${1:-no}
  mkdir -p "$OUT/ui/ts/parser" "$OUT/ui/ts/queries" "$OUT/ipc" "$OUT/tmux" "$OUT/screens" "$OUT/shots" "$OUT/docs"
  cp "$LEM_NVIM_HERE/init.lua" "$OUT/ui/init.lua"
  cp "$LEM_TS_PARSER_DIR/parser/fsharp.so" "$OUT/ui/ts/parser/"
  cp -r "$LEM_TS_PARSER_DIR/queries/fsharp" "$OUT/ui/ts/queries/"
  printf 'set -g exit-empty off\nset -g escape-time 0\n' > "$OUT/ui/tmux.conf"
  printf '{ "mcpServers": {} }\n' > "$OUT/ui/empty-mcp.json"
  printf '%s\n' "$PLUGIN_DIR" > "$OUT/ui/plugin-dir"
  if [ "$want_readme" = yes ]; then cp "$PLUGIN_DIR/README.md" "$OUT/docs/README.md"; fi
}

lem_nvim_base() {
  LEM_NVIM_BW=(
    --die-with-parent --unshare-pid --unshare-ipc --unshare-uts --clearenv
    --ro-bind /usr /usr --ro-bind /etc /etc
    --symlink usr/bin /bin --symlink usr/sbin /sbin --symlink usr/lib /lib --symlink usr/lib64 /lib64
    --ro-bind /run/systemd/resolve /run/systemd/resolve
    --proc /proc --dev /dev --tmpfs /tmp --tmpfs "$HOME"
  )
}

# `tmux -D` is a foreground server with no session; the driver server then creates the nvim and
# shell windows through its socket, so both run here, inside this sandbox, and it lives exactly
# as long as the tmux server does.
lem_start_editor_sandbox() {
  lem_nvim_base
  local bob_dir
  bob_dir=$(dirname "$(dirname "$NVIM_BIN")")
  bwrap "${LEM_NVIM_BW[@]}" \
    --ro-bind "$bob_dir" "$bob_dir" \
    --ro-bind "$PLUGIN_DIR" "$PLUGIN_DIR" \
    --bind "$RUN" "$RUN" \
    --tmpfs "$OUT" \
    --bind "$OUT/tmux" "$OUT/tmux" \
    --ro-bind "$OUT/ui" "$OUT/ui" \
    --chdir "$W" \
    --setenv HOME "$HOME" \
    --setenv PATH "$bob_dir/nightly/bin:$bob_dir/nvim-bin:/usr/bin:/bin" \
    --setenv LANG C.UTF-8 --setenv LC_ALL C.UTF-8 --setenv TERM tmux-256color \
    --setenv TMUX_TMPDIR "$OUT/tmux" \
    --setenv SAGEFS_NVIM_DIR "$PLUGIN_DIR" \
    --setenv LEM_TS_DIR "$OUT/ui/ts" \
    --setenv SAGEFS_MCP_PORT "${LEM_PORT:-37749}" \
    tmux -L "$LABEL" -f "$OUT/ui/tmux.conf" -D \
    > "$OUT/editor-sandbox.log" 2>&1 &
  LEM_EDITOR_PID=$!
}

# The harness side of the driver, run OUTSIDE both sandboxes. Returns 0 once the editor is up
# and the plugin has put its status text on screen.
lem_start_driver_server() {
  rm -f "$OUT/ready" "$OUT/ipc/drive.sock"
  dotnet "$DRIVE_DLL" nvim serve \
    --tmux-dir "$OUT/tmux" --label "$LABEL" --socket "$OUT/ipc/drive.sock" \
    --out "$OUT" --workspace "$W" --nvim "$NVIM_BIN" --init "$OUT/ui/init.lua" \
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

# Extra bubblewrap arguments and environment for lem_run_cmdc. Later mounts win over the core
# sandbox's, which is how the workspace becomes read-only and the MCP file becomes empty.
lem_nvim_lemming_extras() {
  LEM_EXTRA_BWRAP=(
    --ro-bind "$W" "$W"
    --ro-bind "$OUT/ui/empty-mcp.json" "$W/.mcp.json"
    --bind "$OUT/ipc" "$OUT/ipc"
    --ro-bind "$DRIVE_DIR" /lem/drive
    --ro-bind "$OUT/docs" /lem/docs
  )
  LEM_EXTRA_ENV=( "LEM_DRIVE_SOCKET=$OUT/ipc/drive.sock" )
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

# Stops the driver server by its exact pid (SIGTERM lets it finish the call in flight).
lem_stop_driver_server() {
  [ -n "${LEM_DRIVER_PID:-}" ] || return 0
  kill "$LEM_DRIVER_PID" 2>/dev/null || true
  local waited=0
  while kill -0 "$LEM_DRIVER_PID" 2>/dev/null && [ "$waited" -lt 10 ]; do sleep 1; waited=$((waited + 1)); done
  if kill -0 "$LEM_DRIVER_PID" 2>/dev/null; then kill -9 "$LEM_DRIVER_PID" 2>/dev/null || true; fi
}
