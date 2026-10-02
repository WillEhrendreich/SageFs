#!/usr/bin/env bash
# scripts/lemmings/ui/vscode/lib-vscode.sh: thin process plumbing for the VS Code lemmings.
# Sourced, not executed. Every decision that is more than starting or stopping a process
# (settings, readiness, the oracle, scoring) is F#, in scripts/lemmings/ui/LemDrive.
#
# What this file starts, and the rule it follows for each:
#   Xvfb       on a display number it picks itself, NEVER 0 and NEVER -displayfd. On this
#              Hyprland machine the real desktop's Xwayland sits on :0 behind a socket in
#              /tmp/.X11-unix, and an Xvfb asked to find a free display took :0 and deleted
#              the real socket file. So: a number from a fixed high range, and only if no
#              socket and no lock for it exist.
#   VS Code    the real /usr/share/code/code, inside bubblewrap, with its own pid namespace,
#              a cleared environment, a tmpfs HOME and only the run directory writable.
#              It never sees the real desktop's Wayland socket or any real profile.
# Everything started is recorded by exact pid; nothing is ever killed by name.

# Display numbers the lemmings may use. The real desktop is on :0, so none of these can
# collide with it. Kept far from the usual 1 to 20 that other tools pick.
LEM_DISPLAY_FIRST=${LEM_DISPLAY_FIRST:-80}
LEM_DISPLAY_LAST=${LEM_DISPLAY_LAST:-179}
LEM_CDP_PORT_FIRST=${LEM_CDP_PORT_FIRST:-39200}
LEM_CDP_PORT_LAST=${LEM_CDP_PORT_LAST:-39799}
LEM_VSCODE_BIN=${LEM_VSCODE_BIN:-/usr/share/code/code}
LEM_SCREEN_GEOMETRY=${LEM_SCREEN_GEOMETRY:-1600x1000x24}
LEM_X_START_SECONDS=${LEM_X_START_SECONDS:-15}
LEM_CODE_STOP_SECONDS=${LEM_CODE_STOP_SECONDS:-20}
# The run directory path, at most this long: VS Code's IPC socket is under <run>/.lem/vsc and a
# socket path past about 107 characters fails with `listen EINVAL`.
LEM_MAX_RUN_PATH=${LEM_MAX_RUN_PATH:-75}

vsc_free_display() {
  local n
  for n in $(seq "$LEM_DISPLAY_FIRST" "$LEM_DISPLAY_LAST"); do
    [ -e "/tmp/.X11-unix/X$n" ] && continue
    [ -e "/tmp/.X$n-lock" ] && continue
    ss -xl 2>/dev/null | grep -q "X$n\b" && continue
    echo "$n"; return 0
  done
  echo "no free display number between $LEM_DISPLAY_FIRST and $LEM_DISPLAY_LAST" >&2
  return 1
}

vsc_free_port() {
  local p
  for _ in $(seq 1 300); do
    p=$((LEM_CDP_PORT_FIRST + RANDOM % (LEM_CDP_PORT_LAST - LEM_CDP_PORT_FIRST + 1)))
    ss -ltn "( sport = :$p )" | grep -q LISTEN || { echo "$p"; return 0; }
  done
  echo "no free CDP port" >&2
  return 1
}

# vsc_start_xvfb <run-dir>   sets VSC_DISPLAY (":NN") and VSC_XVFB_PID
# Choosing a display number and starting Xvfb on it is one step under a lock: the number is
# only taken once Xvfb has opened its socket, so two runs that start together cannot both
# see the same number free. The lock is held on a file descriptor of this shell and released
# on every way out.
vsc_start_xvfb() {
  local run=$1 n i fd rc=1
  mkdir -p "$LEM_LOCK_DIR"
  exec {fd}> "$LEM_LOCK_DIR/.display.lock"
  flock "$fd" || { exec {fd}>&-; echo "could not take the display lock" >&2; return 1; }
  n=$(vsc_free_display) || { exec {fd}>&-; return 1; }
  [ "$n" -ge 1 ] || { echo "refusing display :$n" >&2; exec {fd}>&-; return 1; }
  # The lock is not inherited by Xvfb: fd is closed for it, so a long-lived Xvfb never holds it.
  setsid Xvfb ":$n" -screen 0 "$LEM_SCREEN_GEOMETRY" -nolisten tcp {fd}>&- > "$run/out/xvfb.log" 2>&1 &
  VSC_XVFB_PID=$!
  VSC_DISPLAY=":$n"
  for i in $(seq 1 $((LEM_X_START_SECONDS * 10))); do
    [ -S "/tmp/.X11-unix/X$n" ] && { rc=0; break; }
    kill -0 "$VSC_XVFB_PID" 2>/dev/null || { echo "Xvfb exited early; see $run/out/xvfb.log" >&2; break; }
    sleep 0.1
  done
  [ "$rc" -eq 0 ] || echo "Xvfb :$n did not start (see $run/out/xvfb.log)" >&2
  exec {fd}>&-
  return "$rc"
}

# vsc_bwrap_args <run-dir>   the sandbox VS Code (and nothing else) runs in, one word per line
#
# The run directory is writable (the window edits the workspace and keeps its profile there),
# but four places inside it are laid over read-only, because whatever runs in the window (a
# terminal that got past the driver's guard, an extension) must not be able to rewrite the
# harness's evidence or the tools the lemming runs:
#   out/                       the oracle's inputs: daemon-evals.tsv, timeline.ndjson, screens
#   bin/                       the driver the lemming runs, the sagefs shim, the bridge copy
#   .lem/vsc/User/settings.json and keybindings.json   the guard's own settings
vsc_bwrap_args() {
  local run=$1
  mkdir -p "$run/out" "$run/bin" "$run/.lem/vsc/User"
  local ro=() f
  for f in "$run/out" "$run/bin" "$run/.lem/vsc/User/settings.json" "$run/.lem/vsc/User/keybindings.json"; do
    [ -e "$f" ] && ro+=(--ro-bind "$f" "$f")
  done
  printf '%s\n' \
    --die-with-parent --unshare-pid --unshare-ipc --unshare-uts \
    --ro-bind /usr /usr --ro-bind /etc /etc \
    --symlink usr/bin /bin --symlink usr/sbin /sbin --symlink usr/lib /lib --symlink usr/lib64 /lib64 \
    --proc /proc --dev /dev --tmpfs /tmp \
    --ro-bind "/tmp/.X11-unix/X${VSC_DISPLAY#:}" "/tmp/.X11-unix/X${VSC_DISPLAY#:}" \
    --bind "$run" "$run" \
    "${ro[@]}" \
    --ro-bind "$HOME/.dotnet" "$HOME/.dotnet" \
    --clearenv \
    --setenv HOME "$run/.lem/home" \
    --setenv XDG_CONFIG_HOME "$run/.lem/home/.config" \
    --setenv XDG_CACHE_HOME "$run/.lem/home/.cache" \
    --setenv XDG_DATA_HOME "$run/.lem/home/.local/share" \
    --setenv XDG_STATE_HOME "$run/.lem/home/.local/state" \
    --setenv TMPDIR "$run/.lem/tmp" \
    --setenv DISPLAY "$VSC_DISPLAY" \
    --setenv DOTNET_ROOT "$HOME/.dotnet" --setenv DOTNET_NOLOGO 1 --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 \
    --setenv DOTNET_CLI_HOME "$run/.lem/tmp" --setenv NODE_NO_WARNINGS 1 \
    --setenv PATH "$run/bin/vsc-path:$HOME/.dotnet:/usr/bin:/bin" \
    --chdir "$run/w"
}

# vsc_start_code <run-dir> <cdp-port> <extension-dir>   sets VSC_PID
# The window opens on the run directory, the same directory the lemming works in, so the
# sessions the extension makes are keyed by it and show up under its name in the dashboard.
vsc_start_code() {
  local run=$1 port=$2 ext=$3
  mkdir -p "$run/.lem/home" "$run/.lem/tmp"
  local args=()
  mapfile -t args < <(vsc_bwrap_args "$run")
  setsid bwrap "${args[@]}" \
    "$LEM_VSCODE_BIN" \
    --user-data-dir="$run/.lem/vsc" --extensions-dir="$run/.lem/vsc-ext" \
    --extensionDevelopmentPath="$ext" \
    --remote-debugging-port="$port" --remote-allow-origins='*' \
    --ozone-platform=x11 --no-sandbox --disable-gpu --disable-workspace-trust \
    --new-window --disable-updates --skip-welcome --skip-release-notes \
    "$run/w" > "$run/out/vscode.stdout" 2> "$run/out/vscode.stderr" &
  VSC_PID=$!
}

# vsc_wait_cdp <port> <seconds>   0 when the CDP endpoint answers
vsc_wait_cdp() {
  local port=$1 secs=$2 i
  for i in $(seq 1 $((secs * 2))); do
    curl -s --max-time 2 "http://127.0.0.1:$port/json/version" | grep -q webSocketDebuggerUrl && return 0
    sleep 0.5
  done
  return 1
}

# vsc_stop_pid <pid> <seconds>   TERM, wait, KILL only if it will not go; echoes graceful|forced|gone
vsc_stop_pid() {
  local pid=$1 secs=$2 i
  kill -0 "$pid" 2>/dev/null || { echo gone; return 0; }
  kill "$pid" 2>/dev/null || true
  for i in $(seq 1 $((secs * 2))); do
    kill -0 "$pid" 2>/dev/null || { echo graceful; return 0; }
    sleep 0.5
  done
  kill -9 "$pid" 2>/dev/null || true
  echo forced
}

# ---- what the window and the lemming are given ------------------------------------------------

VSC_LIB_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
VSC_REPO=$(cd "$VSC_LIB_DIR/../../../.." && pwd)
VSC_DRIVE_DIR=$VSC_REPO/scripts/lemmings/ui/LemDrive
VSC_DRIVE_BIN=$VSC_DRIVE_DIR/bin/Release/net11.0
VSC_EXT_SRC=$VSC_REPO/sagefs-vscode
LEM_LOCK_DIR=${LEM_ROOT:-/tmp/lem}

# The compiled F# driver, built when it is missing or older than a source file. flock keeps
# two lemmings starting together from building at once.
vsc_ensure_driver() {
  local stale=
  if [ ! -f "$VSC_DRIVE_BIN/LemDrive.dll" ]; then stale=1
  elif [ -n "$(find "$VSC_DRIVE_DIR" -maxdepth 1 \( -name '*.fs' -o -name '*.fsproj' \) -newer "$VSC_DRIVE_BIN/LemDrive.dll" -print -quit)" ]; then stale=1
  fi
  [ -z "$stale" ] && return 0
  echo "lem: building LemDrive" >&2
  mkdir -p "$LEM_LOCK_DIR"
  flock "$LEM_LOCK_DIR/.lemdrive-build.lock" dotnet build "$VSC_DRIVE_DIR/LemDrive.fsproj" -c Release -nologo -v quiet >&2 \
    || { echo "lem: LemDrive failed to build" >&2; return 1; }
}

# The extension, built from this repo (npm run compile = Fable then esbuild) when dist is
# missing or older than its sources, then copied into the run directory so a rebuild cannot
# change a running trial. Only what a published extension ships is copied.
vsc_prepare_extension() {
  local run=$1 dest=$1/.lem/ext/sagefs
  local dist=$VSC_EXT_SRC/dist/Extension.js
  if [ ! -f "$dist" ] || [ -n "$(find "$VSC_EXT_SRC/src" "$VSC_EXT_SRC/package.json" -newer "$dist" -print -quit)" ]; then
    echo "lem: building the VS Code extension" >&2
    [ -d "$VSC_EXT_SRC/node_modules" ] || (cd "$VSC_EXT_SRC" && npm ci >&2) || return 1
    (cd "$VSC_EXT_SRC" && npm run compile >&2) || { echo "lem: the extension did not build" >&2; return 1; }
  fi
  mkdir -p "$dest"
  cp -r --reflink=auto "$VSC_EXT_SRC/package.json" "$VSC_EXT_SRC/dist" "$VSC_EXT_SRC/icon.png" "$VSC_EXT_SRC/README.md" \
    "$VSC_EXT_SRC/LICENSE" "$VSC_EXT_SRC/CHANGELOG.md" "$VSC_EXT_SRC/snippets" "$dest/"
}

# A fresh profile: no welcome page, no trust prompt, no updates, no telemetry, and the SageFs
# extension pointed at the shared daemon's default ports. autoStart is OFF on purpose: the
# extension must never start a daemon of its own, because the daemon belongs to Will.
vsc_write_profile() {
  local run=$1
  mkdir -p "$run/.lem/vsc/User" "$run/.lem/vsc-ext" "$run/.lem/home" "$run/.lem/tmp"
  cat > "$run/.lem/vsc/User/settings.json" <<JSON
{
  "security.workspace.trust.enabled": false,
  "workbench.startupEditor": "none",
  "update.mode": "none",
  "extensions.autoCheckUpdates": false,
  "telemetry.telemetryLevel": "off",
  "chat.disableAIFeatures": true,
  "workbench.secondarySideBar.defaultVisibility": "hidden",
  "workbench.tips.enabled": false,
  "sagefs.mcpPort": ${LEM_PORT:-37749},
  "sagefs.dashboardPort": ${LEM_DASH_PORT:-37750},
  "sagefs.autoStart": false,
  "terminal.integrated.profiles.linux": { "none": { "path": "/usr/bin/false" } },
  "terminal.integrated.defaultProfile.linux": "none",
  "terminal.integrated.automationProfile.linux": { "path": "/usr/bin/false" },
  "terminal.integrated.enablePersistentSessions": false,
  "task.allowAutomaticTasks": "off",
  "debug.openDebug": "neverOpen"
}
JSON
  # The keys that open a terminal or the developer tools are unbound, a second layer under the
  # driver's guard (Guard.fs). Both files are laid over read-only in the window's sandbox.
  cat > "$run/.lem/vsc/User/keybindings.json" <<'JSON'
[
  { "key": "ctrl+`", "command": "-workbench.action.terminal.toggleTerminal" },
  { "key": "ctrl+shift+`", "command": "-workbench.action.terminal.new" },
  { "key": "ctrl+shift+c", "command": "-workbench.action.terminal.openNativeConsole" },
  { "key": "ctrl+shift+i", "command": "-workbench.action.toggleDevTools" }
]
JSON
}

# The tools the lemming types: vsc-snapshot, vsc-click, ... Each is a one-line exec of the F#
# driver. Written into <run>/bin/tools, which the sandbox binds read-only.
vsc_install_tools() {
  local run=$1 verb
  mkdir -p "$run/bin/tools" "$run/bin/lemdrive" "$LEM_LOCK_DIR"
  # Copied under the build's own lock: another run's rebuild of the driver (it rebuilds when a
  # source is newer) must not be half written while this copy reads it.
  flock "$LEM_LOCK_DIR/.lemdrive-build.lock" cp -r --reflink=auto "$VSC_DRIVE_BIN/." "$run/bin/lemdrive/"
  for verb in snapshot click key type palette open wait shot; do
    printf '%s\n' '#!/bin/sh' "exec dotnet \"$run/bin/lemdrive/LemDrive.dll\" vsc $verb \"\$@\"" > "$run/bin/tools/vsc-$verb"
    chmod +x "$run/bin/tools/vsc-$verb"
  done
}

# The `sagefs` on PATH inside the window (the extension and a terminal call it, as for a real
# user). It runs the read-only verbs against the build this trial uses and refuses the ones
# that would stop, sweep or start a daemon: the daemon is Will's. Needs LEM_BRIDGE_CMD, which
# lem_prepare_bridge sets (`dotnet <copied SageFs.dll> mcp`, or `sagefs mcp` for the published tool).
vsc_install_shim() {
  local run=$1 real
  mkdir -p "$run/bin/vsc-path"
  if [ "${LEM_BRIDGE_CMD[0]}" = dotnet ]; then real="dotnet ${LEM_BRIDGE_CMD[1]}"; else real=$(command -v sagefs); fi
  printf '%s\n' '#!/bin/sh' "export LEM_SAGEFS_REAL='$real'" "exec dotnet \"$run/bin/lemdrive/LemDrive.dll\" shim sagefs \"\$@\"" > "$run/bin/vsc-path/sagefs"
  chmod +x "$run/bin/vsc-path/sagefs"
}

# The desktop check: the real windows, one line each, so a leak is a diff. Writes the list to $1.
vsc_desktop_windows() {
  hyprctl clients 2>/dev/null | grep -E '^Window ' | sed -E 's/^Window [0-9a-f]+ -> //' | sort > "$1" || true
}

# vsc_check_no_leak <before-file> <after-file>   0 when no window appeared on the real desktop
vsc_check_no_leak() {
  local new; new=$(comm -13 "$1" "$2")
  [ -z "$new" ] && return 0
  echo "lem: A WINDOW APPEARED ON THE REAL DESKTOP: $new" >&2
  return 1
}
