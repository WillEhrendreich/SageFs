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
vsc_start_xvfb() {
  local run=$1 n i
  n=$(vsc_free_display) || return 1
  [ "$n" -ge 1 ] || { echo "refusing display :$n" >&2; return 1; }
  setsid Xvfb ":$n" -screen 0 "$LEM_SCREEN_GEOMETRY" -nolisten tcp > "$run/out/xvfb.log" 2>&1 &
  VSC_XVFB_PID=$!
  VSC_DISPLAY=":$n"
  for i in $(seq 1 $((LEM_X_START_SECONDS * 10))); do
    [ -S "/tmp/.X11-unix/X$n" ] && return 0
    kill -0 "$VSC_XVFB_PID" 2>/dev/null || { echo "Xvfb exited early; see $run/out/xvfb.log" >&2; return 1; }
    sleep 0.1
  done
  echo "Xvfb :$n did not open its socket in ${LEM_X_START_SECONDS}s" >&2
  return 1
}

# vsc_bwrap_args <run-dir>   the sandbox VS Code (and nothing else) runs in, one word per line
vsc_bwrap_args() {
  local run=$1
  printf '%s\n' \
    --die-with-parent --unshare-pid --unshare-ipc --unshare-uts \
    --ro-bind /usr /usr --ro-bind /etc /etc \
    --symlink usr/bin /bin --symlink usr/sbin /sbin --symlink usr/lib /lib --symlink usr/lib64 /lib64 \
    --proc /proc --dev /dev --tmpfs /tmp \
    --ro-bind "/tmp/.X11-unix/X${VSC_DISPLAY#:}" "/tmp/.X11-unix/X${VSC_DISPLAY#:}" \
    --bind "$run" "$run" \
    --clearenv \
    --setenv HOME "$run/.lem/home" \
    --setenv XDG_CONFIG_HOME "$run/.lem/home/.config" \
    --setenv XDG_CACHE_HOME "$run/.lem/home/.cache" \
    --setenv XDG_DATA_HOME "$run/.lem/home/.local/share" \
    --setenv XDG_STATE_HOME "$run/.lem/home/.local/state" \
    --setenv TMPDIR "$run/.lem/tmp" \
    --setenv DISPLAY "$VSC_DISPLAY" \
    --setenv PATH /usr/bin:/bin \
    --chdir "$run"
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
    "$run" > "$run/out/vscode.stdout" 2> "$run/out/vscode.stderr" &
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
