# VS Code lemmings

A lemming here is Command Code (`cmdc`) on a free model, handed a real VS Code with the SageFs
extension and nothing else a new user would not have. It drives the window through a handful of
text commands, tries to do a small task, and I score what happened. The point is to find where
the extension and the daemon behind it fall over for someone who is not me.

```
dotnet fsi scripts/lemmings/run-ui-lemming.fsx -- vscode <task> <model> [max-turns]
dotnet fsi scripts/lemmings/ui/vscode/run-vscode-lemming.fsx -- <task> <model> [max-turns]     # same thing
```

When a run ends, everything except `out/` (summary.json, timeline.ndjson, shots, screens, logs) is removed from `/tmp`, because `/tmp` is RAM. `LEM_KEEP_RUN=1` keeps it whole. The driver is built once into a store (`~/.local/share/sagefs-lemmings/drive`) and mounted read-only, not copied into each run; the `vsc-*` tools and the `sagefs` shim in `bin/` are one-line launchers (`#!/usr/bin/env -S dotnet <driver> ...`), not shell scripts.

`<model>` has to be marked FREE in the live `cmdc --list-models`, or nothing starts. The tasks
are the files in `tasks/`: `ui-eval`, `ui-edit-reeval`, `ui-live-tests`, `ui-hot-reload`,
`ui-find-help`. The Neovim runner uses the same five names.

Every lemming is a client of the one shared daemon on 37749, the one I watch in the dashboard.
Its run directory is `/tmp/lem/<model>-vsc-<task>-<nn>` and its working directory is the `w`
folder inside it, so you can tell which lemming made which session. Nothing here starts, stops
or restarts that daemon. The only thing the harness ever stops is a session under the lemming's
own run directory, by id, after the run.

## What you need on the machine

The runner checks the daemon, the model and the paths it needs before it starts anything, but it
does not install any of this:

- Linux with `bwrap` (bubblewrap) and `Xvfb` on the PATH, and `ss`, `flock`, `curl`.
- `/usr/share/code/code`, the real VS Code (`LEM_VSCODE_BIN` points elsewhere). It is the Electron binary, not
  the `code` wrapper: run by hand, even with `--version`, it starts VS Code on the real desktop. Only the
  harness runs it, inside the sandbox.
- `hyprctl` and a Hyprland desktop. The check that no window leaked onto the real desktop compares
  `hyprctl clients` before and after. On a machine without it `VscRun.desktopWindows` returns an empty
  list both times, so the check passes without checking anything.
- `cmdc` (Command Code) under `~/.local/share/mise`, which is the only toolchain directory the
  sandbox binds, and its login in `~/.commandcode/auth.json`.
- The .NET SDK in `~/.dotnet`, and `node` and `npm` for the extension build (`sagefs-vscode`, run
  with `npm ci` and `npm run compile` the first time or when its sources are newer than `dist`).
- A built `SageFs.dll` in the main checkout (`<main checkout>/SageFs/bin/Release/net11.0`), or
  `SAGEFS_LEMMING_BIN` pointing at one (`published` uses the installed `sagefs` tool instead).
  It is the bridge the lemming's MCP registration points at.
- A healthy SageFs daemon already running on 37749, with room on the machine (the gate wants
  `memoryPressure` normal and 8 GB free). The runner refuses to start without one. It never starts one.

## What the lemming sees

- The `demoenv` fixture, copied into `w`.
- The SageFs skill in `.commandcode/skills` and the MCP registration, written the way the README
  says, pointed at the shared daemon. Same as every cmdc lemming.
- A VS Code window, already open on `w`, with the extension loaded from this repo
  (`--extensionDevelopmentPath`, built from `sagefs-vscode` if `dist` is stale, then copied into the
  run directory so a rebuild cannot change a trial), already attached to a SageFs session of its own
  for `w`.
- `VSCODE-TOOLS.md` in `w`, and eight commands on its PATH.

| Command | What it does |
|---|---|
| `vsc-snapshot` | The window as text: tabs, editor lines with numbers, `[lens]` and `[deco]` marks, the side bar views and rows, the panel, notifications, the status bar. |
| `vsc-click <text>` | Click what has that text or accessibility label. |
| `vsc-key <chord>` | `ctrl+shift+p`, `alt+enter`, `escape`. |
| `vsc-type <text>` | Type into whatever has focus. A newline is Enter. |
| `vsc-palette <text>` | Open the palette, type, run the first match. |
| `vsc-open <path>` | Quick open a file in the workspace. |
| `vsc-wait <s>` | Wait up to 30 seconds. |
| `vsc-shot` | Save a PNG and print its path. |

Every command prints what it did and then a short brief of the window, so a model that cannot see
the screen still sees what its action caused. There is no verb that runs script in the window. The
driver is `LemDrive`, a small compiled F# program (`../LemDrive`) that talks to VS Code over CDP with
Playwright.NET, the package `SageFs.Tests` already uses. It writes every call and its output to
`out/screens/NNN.txt`, and one JSON line per call, with start and end in epoch milliseconds, to
`out/timeline.ndjson`.

## How a run goes

1. Refuse any model that is not FREE. Check the shared daemon is up, healthy and has room, and note
   its pid.
2. Build `LemDrive` and the extension if they are stale. Copy the fixture, the tools and the extension
   into the run directory. Read the main checkout's SageFs build and compare it with the daemon's
   (`VERSION SKEW`, below).
3. **Create the run's own session**: `POST /api/sessions/create` on the shared daemon for `w` and the
   fixture's tests project, then wait until it is Ready. The daemon builds the project first, so this
   is the slow step the first time (a minute or three). If it faults or does not come up, the run is
   scored `HarnessError` and no window or model is started.
4. Start Xvfb on a display from 80 up (under a lock), then VS Code inside bubblewrap on that display,
   with its own profile, a cleared environment and only the run directory writable. Wait until the
   extension has shown itself, then the ten seconds the extension's tree views need to register.
5. **Attach the window to the run's own session and prove it** (`host bind`): if the Sessions view's
   active row is not the run's session, pick it with the extension's own `SageFs: Switch Session`,
   then read the view again. If it still is not the active row, the run is scored `HarnessError`.
6. Run the lemming (`CmdRun.runCmdc`, in `LemRun/CmdRun.fs`).
7. Read the window, run the oracle, write the fellOver list. All of that is outside the sandbox.
8. Stop the run's sessions by id, end VS Code (TERM first, KILL only if it will not go) and Xvfb by
   exact pid, compare `hyprctl clients` before and after, list what of the run is still alive, and
   compare the daemon's pid with the one before.
9. Write `out/summary.json` (`LemScore.Program.scoreRun`), then prune the run to `out/`.

A run takes from about four minutes (`ui-eval`: the first session create, about 150 seconds of
lemming) to twenty and more (`ui-edit-reeval` restores and runs the fixture's tests, and a slow model
can use the whole `LEM_TIMEOUT_SECONDS`, 1500, before the timeout kills it). The turn cap is the third
argument, 60 by default.

## What is protected, and how

| Thing | How |
|---|---|
| The daemon on 37749 | The harness never starts, stops or restarts it, and the extension's `autoStart` is off. The daemon's pid is read before and after and written to `teardown.txt` (`daemon=same` or `CHANGED`, which also fails the run's exit code with 6). The driver refuses the extension's Stop Daemon, Restart Daemon, Start Daemon and Switch Project, whether they come from the palette, a click or a typed Enter. The `sagefs` first on the PATH of both the window and the lemming is a shim that runs `--version`, `check` and `status` and refuses everything that would stop, sweep or start a daemon; the global tools directory (the real `sagefs`) is masked out of the lemming's sandbox. |
| Other agents' sessions | The window is attached to the run's own session before the lemming starts, and the run does not start if that cannot be proved. The fellOver list still names any other session that printed eval output while the lemming's evaluation calls ran. |
| A shell or script console in the window | The driver refuses the palette entries, clicks and keys that open a terminal, the developer tools, the process explorer, tasks or debugging (`Guard.fs`). The profile points every terminal profile at `/usr/bin/false`, turns automatic tasks off and unbinds ctrl+`, ctrl+shift+`, ctrl+shift+c and ctrl+shift+i. In the window's sandbox `out/`, `bin/` and the profile's `settings.json` and `keybindings.json` are laid over read-only, so nothing in the window can rewrite the oracle's evidence or the tools. |
| My Neovim and VS Code, and the desktop | The window is a separate VS Code on its own Xvfb display (80 and up, never `:0`), under bubblewrap with its own pid namespace, a cleared environment and a tmpfs `HOME`. `hyprctl clients` is compared before and after, and a new window fails the run. |
| My NuGet cache and credentials | The lemming's restores go to a cache of the run's own (`NUGET_PACKAGES`); `~/.nuget/packages` is only a read-only fallback. The Command Code login (`~/.commandcode/auth.json`) is bound read-only because the model call is made with it. |
| Other people's processes | Everything started is killed by exact pid. Nothing is killed by name. The leftover check counts a process only on evidence it is the run's: a pid the runner started, the session that pid led, or a working directory inside the run. A command line that merely mentions the run path is not evidence. |

I learned the Xvfb rule the hard way. Asking Xvfb for a free display with `-displayfd` picked `:0`
on this machine and deleted the real desktop's X socket file, because Hyprland's Xwayland sits on
`:0` behind a socket in `/tmp/.X11-unix`. New X11 clients on the real desktop could not connect
after that. So the runner picks a number from a high range itself, and only if no socket and no lock
for it exist, and it takes a lock around choosing the number and starting Xvfb so two runs that start
together cannot pick the same one. `SageFs.Tests` still has a `-displayfd` Xvfb helper
(`startVirtualDisplay`). It is worth checking on this machine before anyone runs it.

What is not protected, said plainly: the network is not isolated, because the model API and the daemon
are on localhost. The guard, the shim, the unbound keys and the read-only mounts keep a good-faith model
from stopping the daemon or opening a shell by accident. They are not a boundary against one that goes
looking: the daemon's HTTP routes (shutdown included) are reachable from the lemming's own shell, as is
`dotnet <bridge>/SageFs.dll stop`, and `LEM_CDP_PORT` is in the lemming's environment because the
driver needs it, so it can talk to VS Code directly. The guard is a list of named commands, not an
allowlist. The credential is readable inside the sandbox. The daemon-pid check at the end turns "the
lemming stopped the daemon" into a line in `teardown.txt` instead of an assumption; it does not
prevent it. Closing this properly means a network namespace with a relay for the model API and the
daemon's allowed routes only, which this harness does not have.

## What comes out

`out/summary.json` is the same file the other cmdc lemmings write: id, model, harness
(`cmdc-vscode`), SageFs version, task, one outcome from the closed set, seconds, turns, tool calls
by name, SageFs MCP calls and errors, oracle result, residue, teardown, and `fellOver` entries of
`{stage, symptom, evidence}`. The editor driver adds its own fellOver entries (calls that failed or
were refused, a lemming that never touched the editor, evaluation output that landed in a session
outside the run). The rest of `out/`: `screens/NNN.txt` (every driver call), `timeline.ndjson`,
`final.snapshot.txt` (the window when the lemming finished), `oracle.out`, `residue.json`,
`teardown.txt`, `session.id` and `bind.out` (the run's own session and the proof the window uses it),
`leftovers.txt` (processes of the run still alive, if any).

The outcome is one of `Pass`, `PassWithRecovery`, `Fail`, `Blocked`, `Incomplete`, `ProviderQuota`,
`MaxTurns`, `HarnessError`:

| Outcome | Means |
|---|---|
| `Pass` | The oracle passed, no SageFs error, nothing left behind. |
| `PassWithRecovery` | The oracle passed, but a SageFs or tool call errored or was denied, the lemming left a session running, or it hit the turn cap on the way. |
| `Fail` | The lemming finished and the oracle failed. |
| `Blocked` | SageFs errored during session setup and no evaluation ever succeeded. |
| `Incomplete` | The run ended without a clean finish (the harness timeout, or no result event) and the oracle failed. |
| `ProviderQuota` | The model provider ended it (rate limit, credits, connection). Not a SageFs result. |
| `MaxTurns` | It hit the turn cap and the oracle failed. |
| `HarnessError` | The harness could not run or score it: no session for the run, the window could not be attached, no display, the extension never activated, no event stream. The reason is in `outcomeReason`. |

### Reading a run that did not pass

1. `summary.json`: `outcome`, `outcomeReason`, then `fellOver` (each entry names a stage and carries its
   evidence). `Registration` findings are not reported for editor lemmings: they never call a SageFs
   MCP tool, the editor does.
2. `oracle.out`: one line per check, `PASS` or `FAIL`, with what was looked for and where.
3. `teardown.txt` and `leftovers.txt`: how the window ended, `desktopLeak`, the cleanup verdict,
   `processesLeft`, and `daemon=same` or `CHANGED`.
4. `screens/NNN.txt` in order: every call the lemming made and what the window showed back; the last
   few say how it ended. `final.snapshot.txt` is the window at the end; `shots/` and `final/` hold PNGs.
5. `events.ndjson` is Command Code's own stream (what the model said and ran), `cmdc.stderr` its errors.
6. `daemon-evals.tsv` is the eval output the daemon's `/events` stream carried while the lemming ran.
   `sessions.before.tsv` and `sessions.after.tsv` are the shared daemon's sessions around the run.
   The sessions list is `GET http://localhost:37749/api/sessions` (the MCP port; the dashboard port
   37750 answers 404 for it).

The version string in `summary.json` names both builds: `daemon <version>+<commit>; bridge dev build:
<version>+<commit>`. They are two builds nothing keeps equal (the daemon is whatever was last started,
the bridge is the main checkout's last build). When they differ the runner warns and the string ends in
`VERSION SKEW: ...`.

## Things I found out about driving VS Code, so nobody has to again

- A long `--user-data-dir` makes VS Code throw `listen EINVAL` on its IPC socket (the limit is about
  107 characters). The profile lives at `<run>/.lem/vsc`.
- The extension's four tree views register about 8 to 10 seconds after its status item appears. Open
  the container earlier and VS Code remembers the partial list in `workspaceStorage`. The profile is
  fresh every run, and the harness waits.
- The command palette matches typed text against the command's title, never its id.
- The activity bar icon's name is an `aria-label`. `vsc-click SageFs` finds it.
- The editor's inline eval result is a CSS `::after` and clears after 30 seconds, so nothing reads it
  after the lemming is done. The oracle reads two places that keep it: the eval output the runner
  records from the daemon's `/events` stream (which carries eval output only for the session the
  daemon has active), and the extension's own `SageFs` Output channel. The harness reads the channel
  itself: it opens the Output panel through the palette (the chord is eaten by whatever has focus),
  picks the channel in the panel's native select, and reads the editor from its end upward a page at a
  time, because the editor draws about a dozen lines and the channel also carries every session's
  hot-reload chatter. `LemDrive host output --cdp-port P --pattern RE` does the same by hand.
- The extension binds a new window to the head of the daemon's session list, which is in id order, so
  on a busy shared daemon it is often another agent's session. Its session picker lists
  every session on the machine. That is why the harness makes the run's own session and attaches the
  window to it before the lemming starts.
- Playwright refuses to click an element another element is drawn over, such as the placeholder text
  of the Extensions search box. `vsc-click` tries the next match and then clicks where the element
  is anyway, which is what a mouse does.
- The status bar, the editor's lines and its margin are read in separate calls; the window can change
  between them. They are paired to the shorter list rather than throwing, because a thrown exception
  there aborted the whole driver process.
- `GET /api/sessions` is served on the MCP port (37749). The dashboard port answers 404 for it.

## Self-test

`dotnet run --project ../LemDrive.Tests` runs the pure parts: chords, the guard (daemon commands,
shells and developer tools, chords), the command parser, the snapshot renderer, the shim's read-only
split, the leftover-process classifier, the session binding checks, the build-skew and cleanup
verdicts. It needs no window and no daemon.
