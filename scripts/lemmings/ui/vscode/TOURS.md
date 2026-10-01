# Tours

A tour is a text file that drives the real VS Code with the SageFs extension into the same states
every time and takes numbered screenshots on the way. No model is involved. They feed the design
review: a reviewer looks at the images and cross-checks them against the text beside each one.

```
scripts/lemmings/ui/vscode/run-vscode-tour tours/live-testing-failing.tour
```

That starts a fresh Xvfb and a fresh VS Code in a bubblewrap sandbox (the same isolation as the
lemming runner), copies the demoenv fixture to `/tmp/lem/tour-<name>-<nn>/w`, waits for the extension
to activate, runs the tour, stops this run's sessions on the shared daemon by id, and ends the window.
It then checks `hyprctl clients` for a leaked window and the dashboard for a leftover session. The
sessions show up in the dashboard under the run directory's name while the tour runs.

You can also run a tour against a window you already have: `LemDrive vsc tour <file>` with
`LEM_CDP_PORT`, `LEM_SCREENS_DIR`, `LEM_SHOTS_DIR` and `LEM_RUN_DIR` set.

## What a run leaves

```
out/shots/NNN-<name>.png                full window
out/shots/NNN-<name>.<part>.png         a crop of one part, for each --region asked for
out/shots/NNN-<name>.txt                the sidecar
out/screens/NNN.txt                     the tour's own log, step by step
out/tour.out                            the same log, as printed
out/teardown.txt                        how the window ended, desktop leak, sessions left
```

The sidecar starts with what a reviewer cross-checks the image against: the window size, the extension
host's activation time, every status bar item, every SageFs view with its rows, and the notifications.
Then comes the accessibility text of the same moment (editor lines with their numbers, `[lens]` for a
CodeLens, `[deco]` for text drawn after a line, `[gutter]` for a gutter mark).

The activation time is read from the extension host log: how long after the host started it asked the
SageFs extension to activate. The extension's own work inside `activate` is not in that log.

## The format

One step per line. `#` starts a comment, blank lines are ignored.

| Step | What it does |
|---|---|
| `command <title>` | Open the command palette and run the command with exactly this title, for example `SageFs: Create Session`. The id does not match. If the palette does not offer that title right now (a when-clause hides `Enable Live Testing` while live testing is on) the step fails and nothing is run, rather than running the first fuzzy match, which was `Disable Live Testing` once. |
| `key <chord> [<chord>...]` | Press keys: `alt+enter`, `ctrl+shift+p`, `escape`, `down`, `enter`. |
| `click <text>` | Click what has that text, label or title. Exact label first, then looser. |
| `type <text>` | Type text where the keyboard focus is. |
| `open <path>` | Open a workspace file with quick open. Relative, no `..`. |
| `wait <seconds>` | Wait 1 to 30 seconds. |
| `shot <name> [--region <part>]...` | A numbered shot with its sidecar. Parts: `activitybar`, `sidebar`, `editor`, `panel`, `statusbar`. A part that is not showing is said so in the sidecar. |
| `resize <w> <h>` | Set the window size, 400 to 4000 pixels each way, and check it by reading it back. |
| `expect-text [--within N] <text>` | Wait until the window's text shows it (any case), default 20 s, at most 300 s. |
| `expect-session [--within N]` | Wait until this run's own session on the daemon is Ready. |
| `set-workflow <name>` | The harness puts this run's session in `Interactive`, `LiveTesting` or `HotReload`. |
| `replace <path> "<find>" "<replacement>"` | Change a workspace file on disk. The find text must occur exactly once. Escapes: `\"`, `\\`, `\n`, `\t`. |

`{session}` in a `command`, `click`, `type` or `expect-text` text is this run's session id, read from
the daemon. The Switch Session picker lists sessions by id, so `type {session}` then `key enter`
switches to this run's session.

A step that fails does not stop the tour, because the later shots still matter. The log marks it, and
the call exits 1 at the end. An unknown step or a bad line is refused before anything starts, with the
line number of every bad line.

## Why these tours switch session

On a daemon that already has an active session, `SageFs: Create Session` makes a session but does not
make it the active one. The status bar keeps naming the other session, and `Enable Live Testing` acts on
that one. A tour that wants live testing on its own project has to switch first. `session-ready.tour`
shows that gap on its own.

## Why `set-workflow` exists

`SageFs: Switch Workflow` fails in the extension today: the daemon answers `unknown workflow ''`. The
extension sends the choice as a chunked POST, and `tryReadWorkflowRequest` in `SageFs/McpServer.fs`
only reads a body that has a Content-Length. `error-switch-workflow.tour` keeps that on record. The
hot reload tour uses `set-workflow` so the rest of the flow can be looked at.

## The tours

| Tour | State |
|---|---|
| `first-run.tour` | The extension has just activated, no session of its own. Three sizes. |
| `session-ready.tour` | A session from the picker to Ready, then active. |
| `eval-inline.tour` | Alt+Enter on a line, with the CodeLens above it. |
| `live-testing-failing.tour` | Live testing on, the fixture's failing test: gutter, inline failure, Test Explorer, SageFs views. |
| `live-testing-passing.tour` | The same, with the source fixed and everything passing. |
| `hot-reload-patched.tour` | Hot Reload workflow, every file watched, then a save. DemoEnv has no running app, so there is no patched line to wait for. |
| `error-eval.tour` | Alt+Enter on a line that does not compile: the error and its diagnostics inline. |
| `error-switch-workflow.tour` | The Switch Workflow defect above. |

Every example tour has a test that it parses and that its shot names are unique
(`dotnet run --project scripts/lemmings/ui/LemDrive.Tests`).
