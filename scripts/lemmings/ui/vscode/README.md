# VS Code lemmings

A lemming here is Command Code (`cmdc`) on a free model, handed a real VS Code with the SageFs
extension and nothing else a new user would not have. It drives the window through a handful of
text commands, tries to do a small task, and I score what happened. The point is to find where
the extension and the daemon behind it fall over for someone who is not me.

```
scripts/lemmings/run-ui-lemming vscode <task> <model> [max-turns]
scripts/lemmings/ui/vscode/run-vscode-lemming <task> <model> [max-turns]     # same thing
```

`<model>` has to be marked FREE in the live `cmdc --list-models`, or nothing starts. The tasks
are the files in `tasks/`: `ui-eval`, `ui-edit-reeval`, `ui-live-tests`, `ui-hot-reload`,
`ui-find-help`. The Neovim runner uses the same five names.

Every lemming is a client of the one shared daemon on 37749, the one I watch in the dashboard.
Its run directory is `/tmp/lem/<model>-vsc-<task>-<nn>` and its working directory is the `w`
folder inside it, so you can tell which lemming made which session. Nothing here starts, stops
or restarts that daemon. The only thing the harness ever stops is a session under the lemming's
own run directory, by id, after the run.

## What the lemming sees

- The `demoenv` fixture, copied into `w`.
- The SageFs skill in `.commandcode/skills` and the MCP registration, written the way the README
  says, pointed at the shared daemon. Same as every cmdc lemming.
- A VS Code window, already open on `w`, with the extension loaded from this repo
  (`--extensionDevelopmentPath`, built from `sagefs-vscode` if `dist` is stale, then copied into the
  run directory so a rebuild cannot change a trial).
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

1. Refuse any model that is not FREE. Check the shared daemon is up, healthy and has room.
2. Build `LemDrive` and the extension if they are stale. Copy the fixture, the tools and the extension
   into the run directory.
3. Start Xvfb on a display from 80 up, then VS Code inside bubblewrap on that display, with its own
   profile, a cleared environment and only the run directory writable. Wait until the extension has
   shown itself, then the ten seconds the extension's tree views need to register.
4. Run the lemming (`lem_run_cmdc`, in `../../lib-cmd.sh`).
5. Read the window, run the oracle, write the fellOver list. All of that is outside the sandbox.
6. Stop the lemming's own sessions by id, end VS Code (TERM first, KILL only if it will not go) and Xvfb
   by exact pid, and compare `hyprctl clients` before and after.
7. Write `out/summary.json` (`LemScore score`).

## What is protected, and how

| Thing | How |
|---|---|
| The daemon on 37749 | Never started, stopped or restarted. The extension's `autoStart` is off. The driver refuses the extension's Stop Daemon, Restart Daemon, Start Daemon and Switch Project commands, whether they come from the palette, a click or a typed Enter. The `sagefs` on the window's PATH is a shim that runs `--version`, `check` and `status` and refuses everything that would stop, sweep or start a daemon. |
| My Neovim and VS Code, and the desktop | The window is a separate VS Code on its own Xvfb display (80 and up, never `:0`), under bubblewrap with its own pid namespace, a cleared environment and a tmpfs `HOME`. `hyprctl clients` is compared before and after, and a new window fails the run. |
| Other people's processes | Everything started is killed by exact pid. Nothing is killed by name. |

I learned the Xvfb rule the hard way. Asking Xvfb for a free display with `-displayfd` picked `:0`
on this machine and deleted the real desktop's X socket file, because Hyprland's Xwayland sits on
`:0` behind a socket in `/tmp/.X11-unix`. New X11 clients on the real desktop could not connect
after that. So the runner picks a number from a high range itself, and only if no socket and no lock
for it exist. `SageFs.Tests` still has a `-displayfd` Xvfb helper (`startVirtualDisplay`). It is worth
checking on this machine before anyone runs it.

What is not protected: the network is not isolated, because the model API and the daemon are on
localhost. A lemming that guessed the CDP port could talk to VS Code directly. Nothing in the prompt
or the workspace names it. A lemming can also stop or evaluate in any session on the shared daemon
through the extension's session picker, since the Sessions view lists all of them. The oracle records
evaluations that landed in sessions outside the run directory, so it shows up in `fellOver`.

## What comes out

`out/summary.json` is the same file the other cmdc lemmings write: id, model, harness
(`cmdc-vscode`), SageFs version, task, one outcome from the closed set, seconds, turns, tool calls
by name, SageFs MCP calls and errors, oracle result, residue, teardown, and `fellOver` entries of
`{stage, symptom, evidence}`. The editor driver adds its own fellOver entries (calls that failed or
were refused, a lemming that never touched the editor, a session that never reached the dashboard).
The rest of `out/`: `screens/NNN.txt` (every driver call), `timeline.ndjson`, `final.snapshot.txt`
(the window when the lemming finished), `oracle.out`, `residue.json`, `teardown.txt`.

## Things I found out about driving VS Code, so nobody has to again

- A long `--user-data-dir` makes VS Code throw `listen EINVAL` on its IPC socket (the limit is about
  107 characters). The profile lives at `<run>/.lem/vsc`.
- The extension's four tree views register about 8 to 10 seconds after its status item appears. Open
  the container earlier and VS Code remembers the partial list in `workspaceStorage`. The profile is
  fresh every run, and the harness waits.
- The command palette matches typed text against the command's title, never its id.
- The activity bar icon's name is an `aria-label`. `vsc-click SageFs` finds it.
- The editor's inline eval result is a CSS `::after` and clears after 30 seconds, so nothing reads it
  after the lemming is done. The oracle uses what the daemon recorded instead.
- `GET /api/sessions` is served on the MCP port (37749). The dashboard port answers 404 for it.

## Self-test

`dotnet run --project ../LemDrive.Tests` runs the pure parts: chords, the daemon-command guard, the
command parser, the snapshot renderer, the shim's read-only split.
