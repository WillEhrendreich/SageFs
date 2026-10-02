#!/usr/bin/env bash
# scripts/lemmings/ui/nvim/lib-nvim.sh (sourced, never executed)
#
# Process plumbing for a Neovim lemming: the editor sandbox (bubblewrap + tmux + nvim) and
# the extra arguments that turn the core lemming sandbox (lib-cmd.sh) into one that can only
# touch the editor through the driver. Every piece of logic lives in the F# LemDrive tool.
#
# Two sandboxes, on purpose:
#   editor sandbox   tmux server + Neovim + a plain shell. Only the workspace is writable (and not
#                    its .git); see lem_editor_bwrap_args. The tmux socket lives under out/tmux
#                    and is never mounted into the lemming's sandbox, so the lemming cannot ask
#                    tmux for a window of its own.
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

# Names everything this harness needs and cannot find, all at once, before a run directory, a
# daemon call or a model call exists. `lem_nvim_preflight lemming|tour` exits 4 with one line per
# missing thing and how to get it; the same list is the Prerequisites section of README.md.
# Callers set NVIM_BIN and PLUGIN_DIR first.
lem_nvim_preflight() {
  local mode=${1:-lemming}
  local -a missing=()
  local tool
  for tool in bwrap tmux git jq flock curl dotnet; do
    command -v "$tool" > /dev/null 2>&1 || missing+=("$tool is not on PATH (install it with your package manager; bubblewrap is the package that provides bwrap)")
  done
  if command -v dotnet > /dev/null 2>&1; then
    dotnet --list-sdks 2> /dev/null | grep -q '^11\.' || missing+=("no .NET 11 SDK (the LemScore tool targets net11.0): dotnet --list-sdks shows $(dotnet --list-sdks 2> /dev/null | cut -d' ' -f1 | paste -sd, - || true)")
    dotnet --list-runtimes 2> /dev/null | grep -q '^Microsoft.NETCore.App 10\.' || missing+=("no .NET 10 runtime (the Neovim driver LemDrive.dll targets net10.0)")
  fi
  [ -x "$NVIM_BIN" ] || missing+=("no Neovim at $NVIM_BIN (install one with bob, or point SAGEFS_LEMMING_NVIM at a Neovim 0.10 or newer; the Neovim the runs were made with is a 0.13 nightly)")
  [ -f "$LEM_TS_PARSER_DIR/parser/fsharp.so" ] || missing+=("no F# tree-sitter parser at $LEM_TS_PARSER_DIR/parser/fsharp.so (:TSInstall fsharp in a Neovim with nvim-treesitter, or set LEM_TS_PARSER_DIR to a site directory that has parser/fsharp.so and queries/fsharp)")
  [ -d "$LEM_TS_PARSER_DIR/queries/fsharp" ] || missing+=("no F# tree-sitter queries at $LEM_TS_PARSER_DIR/queries/fsharp")
  [ -f "$PLUGIN_DIR/lua/sagefs/init.lua" ] || missing+=("no sagefs.nvim checkout at $PLUGIN_DIR (git clone https://github.com/WillEhrendreich/sagefs.nvim there, or set SAGEFS_LEMMING_NVIM_PLUGIN)")
  # Directories the sandbox binds: bwrap refuses to start when one is missing.
  [ -d /run/systemd/resolve ] || missing+=("no /run/systemd/resolve (the sandbox binds it for DNS, so it needs systemd-resolved)")
  if [ "$mode" = lemming ]; then
    [ -d "$HOME/.dotnet" ] || missing+=("no $HOME/.dotnet (the lemming sandbox binds the SDK from there; install dotnet with dotnet-install into it)")
    local cmdc_path
    if cmdc_path=$(command -v cmdc 2> /dev/null); then
      case $(readlink -f "$cmdc_path") in
        "$HOME"/.local/share/mise/*) ;;
        *) missing+=("cmdc is at $cmdc_path, outside ~/.local/share/mise, which is the only toolchain directory the lemming sandbox binds (install it with mise)") ;;
      esac
    else
      missing+=("cmdc (Command Code) is not on PATH (install it with mise: it must live under ~/.local/share/mise)")
    fi
    [ -d "$HOME/.local/share/mise" ] || missing+=("no $HOME/.local/share/mise (the lemming sandbox binds cmdc's toolchain from there)")
    [ -s "$HOME/.commandcode/auth.json" ] || missing+=("no $HOME/.commandcode/auth.json: log in with cmdc once, outside the harness")
  fi
  # Reachability only. The harness never starts a daemon: it is the one I have open in the dashboard.
  local port=${LEM_PORT:-37749}
  curl -s -o /dev/null -m 3 "http://localhost:$port/health" 2> /dev/null \
    || missing+=("no SageFs daemon answers on localhost:$port. This harness never starts one. Start the dev daemon yourself from a SageFs checkout, in its own terminal: dotnet build SageFs -c Release, then dotnet SageFs/bin/Release/net11.0/SageFs.dll --no-resume")
  if [ "${#missing[@]}" -gt 0 ]; then
    echo "lem-nvim: cannot start, ${#missing[@]} thing(s) missing:" >&2
    local m; for m in "${missing[@]}"; do echo "  - $m" >&2; done
    exit 4
  fi
}

# Builds LemDrive once, under a lock, into $LEM_ROOT/.lemdrive, then copies it into the run
# (reflink where there is one) so a rebuild cannot change a trial that is already running.
lem_nvim_ensure_drive() {
  local proj=${LEM_DRIVE_PROJECT:-$LEM_NVIM_HERE/LemDriveNvim.fsproj}
  local built=$LEM_ROOT/.lemdrive
  mkdir -p "$LEM_ROOT" "$RUN/bin"
  if ! flock "$LEM_ROOT/.lemdrive-build.lock" dotnet build "$proj" -c Release -nologo -v quiet -o "$built" >&2; then
    # A tool that is being edited can fail to build. A build that already exists is still a
    # known tool, so use it, and say so in the run's own files.
    [ -f "$built/LemDrive.dll" ] || { echo "lem-nvim: LemDrive failed to build and there is no earlier build" >&2; exit 4; }
    echo "lem-nvim: LemDrive failed to build; using the earlier build in $built" >&2
    mkdir -p "$OUT"; echo "LemDrive failed to build; the earlier build was used" > "$OUT/drive-build.warn"
  fi
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

# The editor sandbox's bubblewrap arguments, in LEM_EDITOR_BW (a test runs another command in it).
#
# Neovim cannot be stopped from running a shell (`:!`, `:terminal`, `:lua os.execute`, a filter
# like `!!sh`): it is an editor, and a key list cannot be allow-listed down to "edits only". So
# the sandbox, not the key parser, is what decides what a shell inside Neovim can reach. It gets:
#
#   writable   the workspace $W, and the tmux socket directory
#   read-only  $W/.git (a repository names programs to run: hooks, fsmonitor, textconv), the
#              Neovim install, the plugin, init.lua and the F# parser, the system
#   absent     everything else under $RUN: bin/ (the driver the harness later runs OUTSIDE every
#              sandbox with `dotnet LemDrive.dll`), out/ (the evidence), dotnethome, cmdchome;
#              and a tmpfs HOME, so no credential, cache or dotfile of mine is in reach
#
# What it does NOT cut: the network. The plugin must reach the shared daemon on localhost, and a
# tour or task curls the app the daemon runs on another localhost port, so a shell here can reach
# the daemon's HTTP API (which is what the plugin itself does) and the internet. The daemon is
# the product and runs F# for whoever asks it; that is accepted for this harness and written down
# in README.md. The point of the binds above is that nothing the editor writes is code the
# harness or I run later.
lem_editor_bwrap_args() {
  lem_nvim_base
  local bob_dir
  bob_dir=$(dirname "$(dirname "$NVIM_BIN")")
  LEM_EDITOR_BW=(
    "${LEM_NVIM_BW[@]}"
    --ro-bind "$bob_dir" "$bob_dir"
    --ro-bind "$PLUGIN_DIR" "$PLUGIN_DIR"
    --bind "$W" "$W"
  )
  # The editor never needs to write the repository, only the files in it. Later mount wins.
  [ -e "$W/.git" ] && LEM_EDITOR_BW+=(--ro-bind "$W/.git" "$W/.git")
  LEM_EDITOR_BW+=(
    --tmpfs "$OUT"
    --bind "$OUT/tmux" "$OUT/tmux"
    --ro-bind "$OUT/ui" "$OUT/ui"
    --chdir "$W"
    --setenv HOME "$HOME"
    --setenv PATH "$bob_dir/nightly/bin:$bob_dir/nvim-bin:/usr/bin:/bin"
    --setenv LANG C.UTF-8 --setenv LC_ALL C.UTF-8 --setenv TERM tmux-256color
    --setenv TMUX_TMPDIR "$OUT/tmux"
    --setenv SAGEFS_NVIM_DIR "$PLUGIN_DIR"
    --setenv LEM_TS_DIR "$OUT/ui/ts"
    --setenv SAGEFS_MCP_PORT "${LEM_PORT:-37749}"
  )
}

# git for the harness, run OUTSIDE the sandboxes on a workspace the editor could write to. The
# editor's .git is read-only, but a repository is also a list of programs to run, so those are
# pinned off on the command line as well (a -c beats every config file).
lem_git() {
  git -c core.fsmonitor=false -c core.hooksPath=/dev/null -c core.pager=cat -c diff.external= "$@"
}

# `tmux -D` is a foreground server with no session; the driver server then creates the nvim and
# shell windows through its socket, so both run here, inside this sandbox, and it lives exactly
# as long as the tmux server does.
lem_start_editor_sandbox() {
  lem_editor_bwrap_args
  bwrap "${LEM_EDITOR_BW[@]}" \
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
