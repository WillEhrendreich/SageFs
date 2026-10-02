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
| `other-session` | Start a second session in `<run>/other`, a copy of the DemoEnv fixture next to the workspace and outside it. It stands in for another agent on the shared daemon. The run script stops it by id with the run's own session. |
| `replace-other <path> "<find>" "<replacement>"` | `replace`, in that other folder. |
| `expect-absent [--for N] <text>` | Fail if the window shows the text at any moment during N seconds (default 15, at most 120). It is the way to check what a window does not say. |

`{session}` in a `command`, `click`, `type` or `expect-text` text is this run's session id, read from
the daemon. The status bar's tooltip names the window's session, with an em dash on each side of
`session <id>`, so `expect-text --within 60` on that text (dashes included, as the example tours write it)
waits until the window has taken the session up and is ready for commands. Do not wait on `session {session}`
alone: the "Session <id> created" message matches it too, a moment before the session is Ready.

`LEM_VSC_EXTRA_SETTINGS` adds settings to the window's profile (one or more `"key": value` pairs). A positive
control for `expect-absent` is the same tour with the setting that lets the text through: it has to fail.

A step that fails does not stop the tour, because the later shots still matter. The log marks it, and
the call exits 1 at the end. An unknown step or a bad line is refused before anything starts, with the
line number of every bad line.

## What `resize` is, and what it is not

`resize` sets the viewport the workbench lays itself out in (Playwright's viewport emulation over CDP),
and reads the size back to check it. The Xvfb screen is 1600x1000, so a 1920x1080 shot is a layout at
that size, rendered into a larger emulated viewport, which is what a reviewer needs. It does not move
the sash between the side bar and the editor, so there is no step for a narrow side bar; the narrow
sizes (1024x700) are where a narrow side bar shows up.

A tour can start from another fixture: a first comment line `# fixture: <name>` picks a directory
under `scripts/lemmings/fixtures`. The default is demoenv.

## What the tours used to work around

Two defects once made the tours switch session by id, and set a workflow through the harness. Both are
fixed in the extension, and the tours now do what a user does.

- On a daemon that already had an active session, `SageFs: Create Session` made a session but the window
  kept naming another one, and `Enable Live Testing` acted on that one. Create Session now selects what it
  made, and every session command carries the session id. `session-ready.tour` and the live testing tours
  assert it with no Switch Session in between.
- `SageFs: Switch Workflow` failed with `unknown workflow ''`: the extension posted the choice chunked,
  with no Content-Length, and `tryReadWorkflowRequest` in `SageFs/McpServer.fs` only read a body that had
  one. The extension sends Content-Length now, and the daemon reads a chunked body.
  `switch-workflow.tour` (it was `error-switch-workflow.tour`) asserts the switch and the new label.
  `set-workflow` stays for a tour that only needs a session in a workflow and not the picker.

## The tours

| Tour | State |
|---|---|
| `first-run.tour` | The extension has just activated, no session of its own. Three sizes. |
| `session-ready.tour` | A session from the picker to Ready, then active. |
| `eval-inline.tour` | Alt+Enter on a line, with the CodeLens above it. |
| `live-testing-failing.tour` | Live testing on, the fixture's failing test: gutter, inline failure, Test Explorer, SageFs views. |
| `live-testing-passing.tour` | The same, with the source fixed and everything passing. The failure text that was beside the green check must be gone. |
| `hot-reload-patched.tour` | Hot Reload workflow, every file watched, then a save. DemoEnv has no running app, so there is no patched line to wait for. |
| `hot-reload-app.tour` | The falco-hello fixture (`# fixture: falco-hello` on the first line picks it): switch to Hot Reload with the real command, Run App from the editor, watch the file, change a route's text and save. It waits for the window to say `Hot reload:` (the status bar item and the message), which it did not before. |
| `error-eval.tour` | Alt+Enter on a line that does not compile: the error and its diagnostics inline. |
| `switch-workflow.tour` | Switch Workflow from the picker: the message and the `[Hot Reload]` label in the status bar tooltip. |
| `other-session-events.tour` | A second session outside the workspace: this window's own save is reported in the Output channel, the other session's is not. |

Every example tour has a test that it parses and that its shot names are unique
(`dotnet run --project scripts/lemmings/ui/LemDrive.Tests`).
