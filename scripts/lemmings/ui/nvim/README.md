# Neovim lemmings

A lemming is a cheap model that gets only what a new user has and does a small task, so I can
see where SageFs falls over. The ones in `scripts/lemmings/run-lemming` call SageFs over MCP.
These ones drive Neovim with the sagefs.nvim plugin, the way I would: they send keys and read
the screen. The driver they talk to has no command for MCP or Lua. The keys themselves are a different
matter: Neovim will run `:!cmd`, `:terminal` or `:lua os.execute(...)` when they are typed, and no key
parser can allow-list that away. So the editor runs in a sandbox that decides what such a shell can reach
(see "Why it is built this way" for exactly what, and for what it does not cut).

The lemming is Command Code (`cmdc`) on a model the live `cmdc --list-models` marks FREE. Anything
else is refused before a single call is made. A provider quota error is recorded as its own
outcome (`ProviderQuota`), never retried in a loop, and never counted as a SageFs failure.

Every lemming is a client of the one shared daemon on 37749, the one I have open at
http://localhost:37750/dashboard. Nothing here starts, stops or restarts it. A run directory is named
`<model-short>-<task>-<nn>` (for example `space-bunny-ui-eval-01`) and its `w` directory is the working
directory, so each lemming shows up in the dashboard under that name.

## Prerequisites

The runners check all of this before they create a run directory, call the daemon or call a model, and
print every missing item at once with how to get it (`lem_nvim_preflight` in `lib-nvim.sh`, exit 4). A
tour needs the same list minus `cmdc`, its login and `~/.dotnet`.

| Need | Why | If it is somewhere else |
|---|---|---|
| Linux with `bwrap` (bubblewrap), `tmux`, `git`, `jq`, `flock`, `curl` | Both sandboxes, the editor's terminal, the baseline commit, the summary | Install them with your package manager. |
| .NET 11 SDK and the .NET 10 runtime | `LemScore` (scoring, daemon checks) targets net11.0, `LemDrive.dll` targets net10.0. Both are built on first use. | `dotnet --list-sdks`, `dotnet --list-runtimes` |
| `~/.dotnet`, `~/.local/share/mise`, `/run/systemd/resolve` | The lemming sandbox binds the SDK and `cmdc`'s toolchain from the first two, DNS from the third. `bwrap` refuses to start when one is missing. | These paths are fixed. |
| Neovim (0.10+; the runs were made with a 0.13 nightly) at `~/.local/share/bob/nvim-bin/nvim` | The editor | `SAGEFS_LEMMING_NVIM=/path/to/nvim` |
| The F# tree-sitter parser at `~/.local/share/nvim/site/parser/fsharp.so`, with `queries/fsharp` beside it | Syntax highlighting, and the plugin finds cells with it | `LEM_TS_PARSER_DIR=<a site directory>` |
| A `sagefs.nvim` checkout at `~/Work/sagefs.nvim` | The plugin, mounted read-only. I never modify it from here. | `SAGEFS_LEMMING_NVIM_PLUGIN=/path/to/checkout` |
| `cmdc` (Command Code) installed with mise, so it lives under `~/.local/share/mise`, and logged in once (`~/.commandcode/auth.json`) | The lemming itself | The sandbox binds only that toolchain directory. |
| A dev SageFs daemon already running on 37749 | Every run is a client of it. The harness never starts, stops or restarts it. | Start it yourself, in its own terminal, from a SageFs checkout: `dotnet build SageFs -c Release`, then `dotnet SageFs/bin/Release/net11.0/SageFs.dll --no-resume` (the dashboard is then at http://localhost:37750/dashboard). |

## Run one

The scripts find each other from their own location, so they work from any directory. The paths below are
relative to the repository root.

```
scripts/lemmings/run-ui-lemming nvim ui-eval stealth/space-bunny-alpha 60
scripts/lemmings/ui/nvim/run-nvim-lemming ui-eval stealth/space-bunny-alpha     # the same, directly
```

Free models today: `poolside/laguna-s-2.1-free`, `inclusionai/ling-3.1-flash:free`,
`inclusionai/ling-3.0-flash-sante:free`, `stealth/space-bunny-alpha`, `stealth/pixel-canary`. The
runner reads the live list, so a model that stops being free stops being accepted.

`SAGEFS_LEMMING_NVIM` and `SAGEFS_LEMMING_NVIM_PLUGIN` override the Neovim binary and the plugin checkout
(default `~/Work/sagefs.nvim`). The plugin is mounted read-only. I never modify it from here.

## What a run does

```
 host (outside every sandbox)                      editor sandbox (bwrap)            lemming sandbox (bwrap)
 ----------------------------                      ----------------------            -----------------------
 run-nvim-lemming                                  tmux -D (no session yet)           cmdc -p <task> --yolo ...
 LemDrive nvim serve  --tmux socket-->  creates    window "nvim": Neovim + plugin     dotnet LemDrive.dll nvim keys ...
   owns the tmux socket, runs the          >       window "shell": bash               |
   closed command set, writes screens,                                                | unix socket (out/ipc)
   shots, ui-calls.jsonl, timeline.ndjson  <------------------------------------------+
 oracle, cleanup, scoring (after the lemming)
```

1. Check the model is FREE, check the shared daemon is up and has room (it never starts one).
2. Copy the fixture to `<run>/w`, commit it as a baseline, so `git diff` is the evidence of what changed.
3. Start the editor sandbox and the driver server. The server opens Neovim on the fixture with the
   config in `init.lua` and waits until the plugin has put its status text on the status line.
4. Run `cmdc` in the lemming sandbox with the task text as the prompt. The workspace is READ-ONLY there,
   the MCP file is empty, and the only way to change a file is through the editor.
5. After the lemming stops, outside the sandbox: take the last screen, read the shared daemon's sessions,
   stop exactly the sessions under the run directory (by id), run the oracle, write `out/summary.json`.

### Why it is built this way

| Thing | How it is kept apart |
|---|---|
| tmux socket | Lives in `out/tmux`, which the lemming sandbox hides. A client that could reach it could open a window that runs commands, so the lemming only ever reaches a unix socket whose server speaks seven commands. |
| Files, lemming | The lemming's view of the workspace is read-only. |
| Files, editor | Only `<run>/w` is writable in the editor sandbox, and its `.git` is read-only on top of that. `<run>/bin` (the driver the harness runs later, outside every sandbox), `out/` (the evidence, covered by a tmpfs with `tmux/` and `ui/` put back) and the rest of the run directory are not mounted at all, and `HOME` is a tmpfs. So `:!touch ../bin/lemdrive/X` and `:!touch .git/hooks/X` write into nothing the harness reads. `IsolationTests.fs` starts the real sandbox arguments, runs those writes and reads back what reached the host. |
| Harness git | The harness's own `git diff` and `ls-files` on the workspace run outside the sandboxes with `core.fsmonitor`, `core.hooksPath`, `core.pager` and `diff.external` pinned off on the command line (`lem_git`, `Nvim.harnessGitPins`), on top of the read-only `.git`. |
| Credentials | `~/.commandcode/auth.json` is bound read-only into the lemming sandbox only. |
| NuGet | The lemming sandbox sees `~/.nuget/packages` read-only with a per-run layer on top (`<run>/nuget`), so a restore finds what I already have and nothing a lemming adds lands in the cache my other projects use. NuGet's own config is not mounted. |
| MCP bridge | None. `LEM_NO_BRIDGE=1` makes `lem_run_cmdc` skip the 380 MB bridge copy and write an empty `.mcp.json`, so `summary.json` carries one SageFs version, the daemon's. |
| The suite the oracle runs | In the lemming sandbox with `--unshare-net` (the daemon already restored the project, so there is nothing to download). |
| Daemon | Reachable over localhost (the model API needs the network, so it is not isolated). The prompt never mentions it. The summary lists any shell command that reached for a port or a tool it should not have. |
| Processes | Both sandboxes have their own pid namespace. Teardown is graceful first (`tmux kill-server`), forced only if that fails, and both are recorded. |

The limits, stated plainly:

- A shell inside the editor (`:!`, `:terminal`, `:lua`) is not stopped, because a key list cannot be
  allow-listed down to "edits only". It runs as me, inside the editor sandbox, with the workspace
  writable and nothing else of mine in sight.
- The editor sandbox has the network, because the plugin has to reach the daemon on localhost and a task
  may curl the app the daemon runs on another localhost port. So a shell there can reach the daemon's
  HTTP API with any verb, which bypasses the `nvim-shell` allow-list. That is no more than the plugin
  itself can do: the daemon evaluates F# and builds projects for whoever asks it, outside every sandbox,
  and an edited `.fsproj` is built by it. The shared dev daemon is the accepted trust boundary of this
  harness, the same as for the MCP lemmings. The lemming's own `shell_command` can `curl` it too, and the
  summary flags that.
- The `nvim-shell` allow-list (curl on localhost, cat, ls) is a convenience for the tour and the task
  prompts, not a security boundary.

## What the lemming can do

It reads `LEMDRIVE.md` (copied into its workspace) and runs `dotnet /lem/drive/LemDrive.dll nvim <command>`:

| Command | What it does |
|---|---|
| `keys <keys>` | Vim notation: `ihello<Esc>`, `:w<CR>`, `<C-w>l`, `<M-CR>`. `<lt>` is a literal `<`. An unknown `<name>` is refused with the fix, never typed. Prints the screen afterwards. |
| `nvim-type <text>` | Types text as it is. A newline is an Enter. |
| `nvim-screen` | The screen, headed by mode, cursor position and status line. |
| `nvim-wait <s>` | Waits at most 30 seconds, then prints the screen. |
| `nvim-messages` | What `:messages` shows (it pages through "-- More --"). |
| `nvim-shell <cmd>` | One `curl` (localhost only), `cat` or `ls` in a second tmux window. No quotes, pipes, `;`, `&`, `$`, backticks or redirection. `cat` and `ls` stay inside the project. |
| `nvim-shot <name>` | Saves a coloured PNG for the design review. The lemming gets the screen as text back, because free models cannot read the image. |

The tool has no command that runs Lua, calls the plugin, or reaches MCP. `NvimTests` pins that a handful of
obvious names (`lua`, `eval`, `exec`, `call`, `mcp`, `server`, `rpc`) are refused. That is a statement about
the tool's command set only. Keys sent with `keys` and `nvim-type` go to a real Neovim, which can run a shell
or Lua, and what that can reach is the editor sandbox's job (above).

Typed text goes to tmux a piece at a time. tmux cuts a `send-keys` argument at a trailing `;` and drops it
(`abc;;` arrived as `abc;`, which is how the first F# smoke run could never end a cell), so every `;` is
sent as a raw byte (`send-keys -H 3b`). `NvimTests` types `...;;` through a real tmux into `cat` and reads
the bytes back.

## The tasks

Each is written the way a new user would ask, in `tasks/<name>.md`, with `tasks/<name>.env` naming the
fixture, the file the editor opens on and whether the plugin README is visible.

| Task | Fixture | What the lemming is asked | What the oracle checks |
|---|---|---|---|
| `ui-eval` | demoenv | Evaluate `parseWindow (Some "10,20,800,600")` and say what the editor shows. | It used the editor, its answer has `Width = 800`, a screen showed it, a session appeared in the shared daemon, no file changed. |
| `ui-edit-reeval` | demoenv | Evaluate `parseSeed (Some "-1")`, fix `parseSeed` so negatives give `None`, save, evaluate again. | `DemoEnv.fs` changed, the tests were not touched, the Expecto suite passes (run by the harness in a sandbox with no network), screens show `Some -1` then `None`. |
| `ui-live-tests` | demoenv | Turn live testing on, find the failing test, fix the code, watch it go green. | `DemoEnv.fs` changed, the suite passes, and the daemon's own `/api/live-testing/status?session=` says tests are enabled with none failing. |
| `ui-hot-reload` | falco-hello | Run the app from the editor, change `/hello` from "Hello from Falco" to "Hello from Neovim", see it served. | `Program.fs` changed, and curl calls in the shell window showed the old text and then the new one. |
| `ui-find-help` | demoenv, README hidden | Find out from inside the editor what the plugin can do and name five commands. | At least five names that exist in the plugin's own `commands.lua`. Invented names are reported. |

The oracle runs outside the sandbox and never takes the model's word. Where the task asks "what did you
see", the answer is compared with a screen the harness recorded.

## What comes out of a run

Everything is under `<run>/out`:

| File | What |
|---|---|
| `summary.json` | The closed outcome (`Pass`, `PassWithRecovery`, `Fail`, `Blocked`, `Incomplete`, `ProviderQuota`, `MaxTurns`, `HarnessError`), model, version of SageFs, seconds, turns, tool calls by name, residue, graceful or forced teardown, the oracle verdict and a `fellOver` list of `{stage, symptom, evidence}`. A `ui` block adds driver call counts, screens recorded and the dashboard URL. |
| `events.ndjson` | The raw `cmdc -p --output-format json` stream. |
| `screens/NNN.txt` | One file per driver call: the call, then the screen it returned. Replay a run by reading them in order. |
| `ui-calls.jsonl` | One line per driver call: number, command, argument, ok, milliseconds, the screen file. |
| `timeline.ndjson` | One line per driver command or tour step with epoch-millisecond `startMs` and `endMs`, `source` (`lemming` or `tour`), `command`, `args`, `ok`. A later recording step lines it up with video. |
| `shots/NNN-name.png` | Coloured screenshots. Beside each: `.txt` (the raw ANSI) and `.json` (terminal size, cell size, font). The driver takes a `final` shot when it stops. |
| `daemon-state.json` | The sessions under the run directory and their live-test counts, read before cleanup. |
| `residue.json` | What the lemming left running, read before anything was stopped. |
| `oracle.out`, `changed.txt`, `files.diff` | The oracle's report and the evidence for the files. |

## Screenshots and tours (for the design review)

`nvim-shot <name>` draws the pane exactly as it is: `tmux capture-pane -e` gives ANSI text, `Ansi.fs`
turns it into HTML (a closed set of SGR cases, total, never changes the visible text), and `NvimShot.fs`
draws that with headless Chromium through Playwright.NET, the package SageFs.Tests already uses. The
font stack, the 14px/18px cell and the dark background are fixed and written beside each PNG. The browser
is started with no `DISPLAY`, `WAYLAND_DISPLAY` or `XDG_RUNTIME_DIR`, so it cannot reach the desktop.

A tour puts the editor in the same states every time, with no lemming:

```
scripts/lemmings/ui/nvim/run-nvim-tour scripts/lemmings/ui/nvim/tours/demoenv-first-run.tour
scripts/lemmings/ui/nvim/run-nvim-tour scripts/lemmings/ui/nvim/tours/demoenv-live-tests.tour
scripts/lemmings/ui/nvim/run-nvim-tour scripts/lemmings/ui/nvim/tours/falco-hot-reload.tour falco-hello Program.fs
scripts/lemmings/ui/nvim/run-nvim-tour scripts/lemmings/ui/nvim/tours/fsharp-cell-terminator.tour   # types `...;;` into a real Neovim and expects both semicolons on screen
dotnet LemDrive.dll nvim tour-check some.tour      # parse only, report every bad line
dotnet LemDrive.dll nvim render shot.txt out.png   # draw a saved shot again
```

A tour file is one step per line (`#` starts a comment):

| Step | Meaning |
|---|---|
| `keys <vim keys>` | Send keys. |
| `type <text>` | Type text as it is. |
| `wait <seconds>` | 0 to 30. |
| `shot <name>` | Save the next numbered shot. |
| `expect "<text>" [within N]` | The screen must show the text within N seconds (default 10), or the tour stops and prints the screen. |
| `resize <cols>x<rows>` | Resize the terminal, 20x8 up to 300x100. |
| `shell <curl\|cat\|ls ...>` | The same read-only shell the lemming has. |
| `activate <project text>` | Makes this run's session (the one whose project path contains the text) the shared daemon's active session, the way picking it in `:SageFsSessions` would. The tour remembers the previous active session and puts it back when it ends. The only step that writes to the daemon. |

`demoenv-first-run.tour` covers the startup message, the file with no session, an empty buffer, the
status float, the project picker, a session warming up, a definition evaluated, a value evaluated, an
error, and the 80x24, 200x50 and 140x40 terminals. `demoenv-live-tests.tour` covers live testing enabled, the gutter
marker on a failing test, the test panel failing, the fix, the gutter and panel passing, and the narrow and
wide panel (it ends on `8 ✓ 1 ✖` and then `9 ✓` in the status line). `falco-hot-reload.tour` (fixture
`falco-hello`) covers running the app from the editor, an edit, the save, the hot reload file picker and a
narrow terminal. All three ran green against the shared daemon and left nothing in the dashboard. The
three together are 13, 12 and 9 shots.

Things a reviewer will see in them: after a save there is no message that the app was reloaded (the
status line keeps `(Starting)`, the message line keeps `[SageFs] Warming up:`), and the pink gutter blocks on the
`[<Tests>]` lines are still there after the status line says `9 ✓`.

## Files

| File | What |
|---|---|
| `Nvim.fs` | The driver: key notation, shell allow-list, tmux, the server and client, shots, timeline. |
| `Ansi.fs`, `NvimShot.fs` | ANSI to HTML, HTML to PNG. |
| `NvimTour.fs` | The tour parser and runner, `tour`, `tour-check`, `render`. |
| `NvimOracle.fs`, `NvimMain.fs` | The oracles and the summary, and the command dispatch. |
| `LemDriveNvim.fsproj` | Builds `LemDrive.dll` for the Neovim half. The shared LemDrive project takes the same files. |
| `lib-nvim.sh`, `run-nvim-lemming`, `run-nvim-tour`, `oracle.sh` | The process plumbing: the preflight, bubblewrap, tmux, port and file setup. No logic. |
| `init.lua` | The Neovim config the lemming starts with: the plugin on the runtimepath, the F# filetype and parser, a status line that includes the plugin's own component, and Neovim's message UI (`ui2`) so another session's "[SageFs] Warming up:" lines never become a "Press ENTER" prompt that costs the lemming a turn. Configuration, not logic. |
| `tasks/`, `tours/`, `LEMDRIVE.md` | What the lemming is asked, what a tour does, and the driver's README the lemming reads. |
| `NvimTests/` | 59 tests: properties for ANSI to HTML, examples for the key notation, shell allow-list, command set, timeline line and tour parser, a round trip for tours, the summary filter that drops MCP-only findings, text ending in `;;` typed through a real tmux into `cat`, the editor sandbox's mounts (writes to the driver, `.git` and `out/` never reach the host), and the pinned git. The tmux and bubblewrap tests skip, saying so, where those are not installed. |

`dotnet run --project scripts/lemmings/ui/nvim/NvimTests` runs the tests.

## What the lemmings and the tours have found in the plugin so far

These are notes for the sagefs.nvim repo. I did not change the plugin.

- `setup()` fails with `E482` when `stdpath("data")` does not exist, because it writes the
  `sagefs_welcomed` marker there. A plugin manager hides this; a clean config hits it on the first start.
- Every start prints "Plugin v0.5.543 is behind the SageFs daemon (v0.6.875.0)". The plugin's own version string is stale.
- After `:SageFsCreateSession` the status line stays on `(Starting)` and the message line on
  `[SageFs] Warming up:` while the daemon says the session is Ready. `Tests: 0` stays too.
- `:SageFsEnableTesting` (and disable) post to `/api/live-testing/enable` with no session id, so the daemon
  applies it to its ACTIVE session. Creating a session from the editor does not make it active. On a
  shared daemon the command lands on someone else's session and the editor's own tests are never
  discovered. Reproduced by `demoenv-live-tests.tour` (shot `live-testing-enabled-wrong-session`): the
  daemon reported the editor's session with 9 tests, all stale, and `Enabled: false`.
- `:SageFsSessions` lists `DemoEnv.Tests/DemoEnv.Tests.fsproj  Ready` with no working directory or id, so
  with several sessions of the same project nobody can tell which line is theirs (shot
  `session-picker-no-directories`).
- The startup message says "Run :SageFsStart to begin". Without `sagefs` on PATH that command ends in a raw Lua
  error and traceback (`E475: 'sagefs' is not executable`, `commands.lua:876 try_start`) instead of a sentence
  that says SageFs is not installed. The editor sandbox has no `sagefs` on PATH on purpose, so a lemming can
  never start a second daemon from the editor; `space-bunny-ui-eval-03` spent its 60 turns around that error and
  never evaluated anything.
- A cell's result is drawn at the end of the cell. When the cell is taller than the window, Alt-Enter
  shows nothing on screen at first.
- On startup the plugin only offers to create a session when the daemon has none. On a shared daemon with
  other sessions it never asks, so a new user has to know `:SageFsCreateSession`.
- An evaluation error shows a long float of diagnostics whose line numbers (`(13,4)`) are not the lines
  in the buffer.
- `/api/sessions` is on the MCP port (37749), not the dashboard port. The old `run-lemming` read 37750 and got 404.
