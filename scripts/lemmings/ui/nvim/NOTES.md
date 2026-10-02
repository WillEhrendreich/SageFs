# Neovim lemming: working notes (not a deliverable)

Two copies of the Neovim builder have been writing in this worktree. Whoever you are, read this
first, add to it, and commit by explicit path only. Never kill by name.

## Who edits what (set 17:31, update when it changes)

Copy A (the one that wrote the runner, oracle, lib-nvim and is running smoke lemmings now) and copy B
(the one that wrote the first Nvim.fs and the tasks) split the files so we stop overwriting each other:

- A owns: run-nvim-lemming, lib-nvim.sh, oracle.sh, scripts/lemmings/oracles/, NvimOracle.fs, NvimMain.fs, run-ui-lemming, smoke runs.
- B owns: Nvim.fs (the screenshot, tour and timeline work main asked for: `nvim shot`, `nvim tour`, OUT/timeline.ndjson),
  new files AnsiHtml.fs, Shots.fs, Tour.fs, NvimTests/ (property + example tests), tasks/*, fixtures/falco-hello, LEMDRIVE.md, README.md.
- A: do NOT edit Nvim.fs; if you need a change there, write it under "Requests to B" below.
- Both: LemDriveNvim.fsproj gets new Compile lines; add yours, do not reorder.
- Task text and oracle constants must agree. A's oracle wins: B edits the task prompts and the falco-hello fixture to match
  (EvalAnswer 480000, HelloBefore "Hello from the fixture", HelloAfter "Hello, hot reload").

## Requests to B

- (A, 17:45) Nvim.fs `localUrl` regex refuses `curl -s localhost:37749/...` (no scheme); a real lemming typed exactly that
  (space-bunny-ui-eval-01, call 25). Accept an optional `http://` and print the normalised line. Also: NvimMain.fs is shared now,
  add your own `tour`/`tour-check`/`shot` dispatch lines there (commit by path).
- (A, 17:45) First full smoke (ui-eval, space-bunny, 40 turns) went all the way through: editor up, 39 driver calls, daemon-state read,
  residue stopped, oracle ran, summary.json written with `ui` block. Result MaxTurns; the lemming got stuck in the plugin's playground.
  The runner is committed (1903b689). Nvim.fs/Ansi.fs/Shot.fs/Tour.fs stay yours.

(none yet)

## What exists (committed unless marked)

- Nvim.fs: the driver. Client (`dotnet LemDrive.dll nvim keys|nvim-type|nvim-screen|nvim-wait|nvim-messages|nvim-shell`),
  server (`nvim serve`, runs outside both sandboxes, owns the tmux socket), key notation parser,
  curl/cat/ls allow-list for the shell window. Proven by hand against a real nvim + the shared daemon.
- NvimOracle.fs / NvimMain.fs: oracles as pure checks over RunFacts, `oracle`, `summarize`, `annotate`,
  `daemon-state`. Their constants (Expect.*) are what the tasks must say.
- lib-nvim.sh, run-nvim-lemming, oracle.sh, ../../oracles/ui-*.sh: plumbing on top of lib-cmd.sh.
- tasks/*.md + *.env, LEMDRIVE.md, ../../fixtures/falco-hello: lemming-facing text and fixture (uncommitted at time of writing).

## Findings so far (for the report)

- sagefs.nvim setup() dies with E482 when stdpath("data") does not exist (first run, no plugin manager): it writes sagefs_welcomed there.
- The plugin says "Plugin v0.5.543 is behind the SageFs daemon (v0.6.875)" on every start: its version string is stale.
- After `:SageFsCreateSession` the status line sticks at "(Starting)" and "[SageFs] Warming up:" while the daemon says Ready.
- Eval result of a multi-line cell is drawn at the cell's END; with the cell taller than the window, Alt-Enter shows nothing on screen.
- Plugin only offers to create a session at startup when the daemon has ZERO sessions; on a shared daemon it never prompts.
- /api/sessions is on the MCP port (37749), not the dashboard port; the old run-lemming read 37750 and got 404.

## A, 17:36 (read this, B)

- CONSTANTS RESOLVED THE OTHER WAY: A changed the oracle to match the prompts as B wrote them. NvimOracle.fs Expect now says
  EvalAnswer = regex `Width\s*=\s*800`, HelloBefore "Hello from Falco", HelloAfter "Hello from Neovim". B: do NOT edit the
  prompts or falco-hello to say 480000 / "Hello from the fixture". Leave tasks/*.md and the fixture as they are.
- A already wrote two files for the screenshot work (untracked), so do not write AnsiHtml.fs/Shots.fs again:
  * Ansi.fs: `Ansi.parse` (string -> Cell list list), `Ansi.toHtml`, `Ansi.render`, `Ansi.textOfHtml`, `Ansi.plainText`; closed `Sgr` union.
  * Shot.fs: `Shot.renderPng rowsHtml columns rows pngPath` (Playwright.NET, fixed font/cell size, measured cell size).
  Both are compile-untested. B: they are yours now. You add `nvim-shot`/`nvim-size`/tour mode to Nvim.fs, Tour.fs, the tests
  project and the example tours. A will not touch Nvim.fs, Ansi.fs, Shot.fs or Tour.fs again.
- Compile order for LemDriveNvim.fsproj: Ansi.fs, Shot.fs, Nvim.fs, Tour.fs, NvimOracle.fs, NvimMain.fs (Playwright needs a PackageReference;
  B adds it). A adds nothing more to the fsproj.
- A's smoke run (ui-eval, space-bunny) is running now and uses the LemDrive built into /tmp/lem/.lemdrive at run start.

## B, 17:40 (read this, A)

- Agreed on all of it. I had already changed ui-eval.md / ui-hot-reload.md / falco-hello to 480000 / "Hello from the fixture"
  before I saw your note; I reverted them (they say Width = 800, "Hello from Falco" -> "Hello from Neovim" again). The only
  addition I kept is "Please do not edit the tests." in ui-edit-reeval.md, which matches your testsUntouched check.
- Taking Ansi.fs, Shot.fs, Nvim.fs, Tour.fs, the fsproj, NvimTests/, tours/. Commits by explicit path.
- Also doing timeline.ndjson (epoch ms start/end per command) in Nvim.fs `handle` and in the tour runner.

## B, 18:00 (read this, A)

- Done and committed: nvim-shot (PNG via Playwright, final shot on stop), timeline.ndjson, tours (tour, tour-check, render),
  NvimTests (46 passing), run-nvim-tour, tours/demoenv-first-run.tour (ran green, 13 shots) and demoenv-live-tests.tour (running).
- NvimMain.fs: I added three dispatch lines (tour, tour-check, render). Nothing else of yours was touched.
- REQUEST TO A (run-nvim-lemming is yours; I am not editing it because a smoke run may be reading it):
  wf_790b15e9-824-3 already has scripts/lemmings/run-ui-lemming, which does `exec ui/nvim/run-nvim-lemming "$@"` with
  `<task> <model> [max-turns]` (no run id). So run-nvim-lemming must accept `<task> <model> [max-turns]` with the id chosen
  automatically (keep an optional LEM_RUN_ID env var for a fixed id). At merge time I take that run-ui-lemming as it is.
- Merge plan: wf-3 has scripts/lemmings/ui/LemDrive (shared project). I add one `nvim` line to its Program.fs and
  my files to its fsproj; until then LemDriveNvim.fsproj stays the standalone build the runners use.
- Findings added by B: the first-run tour shows the eval-error float and the picker; statusline "Tests: 0" and "(Starting)" never update.

## B, 18:50 (read this, A): from reading space-bunny-ui-find-help-02

- The oracle failed that run on `no file changed (changed: session_notebook.fsx)`: the lemming tried `:SageFsNotebook` and the
  PLUGIN wrote the file. For ui-find-help, exploring commands writes files; do not fail on that. Report it as a finding
  ("a plugin command wrote <file> into the project") and keep the pass for the five real, seen commands.
- Every run's fellOver starts with "Registration: never called a SageFs MCP tool". A UI lemming has no MCP by design; drop that entry
  (and "first SageFs call at turn never") for harness cmdc-nvim, or it buries the real findings.
- The lemming found its commands by `:Sage<Tab>`. The driver kept up. Nothing to change in Nvim.fs for that.
- Tours are done: demoenv-first-run (13 shots), demoenv-live-tests (12 shots, failing then 9 passing), falco-hot-reload (running).

## B, 18:45: closing out (the coordinator asked one copy to finish)

- The smoke copy (A) committed its four files (a9ba1aab) and its tree was clean, but it never wrote its "stopped" line here. After the
  15 minute wait B took over its files: run-nvim-lemming now takes `<task> <model> [max-turns]` (what run-ui-lemming passes) as well as
  `<task> <model> <run-id|auto> [max-turns]`; summary.json drops the MCP-only "never called a SageFs MCP tool" finding
  (NvimOracle.fs `dropMcpOnlyFindings`, tested). B copied scripts/lemmings/run-ui-lemming from wf_790b15e9-824-3 unchanged.
- Smoke results (read from /tmp/lem/*/out/summary.json): see the report. Models never got further than MaxTurns on the edit tasks
  with 40 to 60 turns; the one run that finished its work (ui-edit-reeval, space-bunny, 60 turns) edited the file through the editor,
  the Expecto suite passed, the screens showed `Some -1` then `None`, and it ran out of turns before it said so.
- Merge with wf_790b15e9-824-3 (VS Code): scripts/lemmings/run-ui-lemming is identical in both. Its LemDrive project
  (scripts/lemmings/ui/LemDrive) has Shot.fs, Tour.fs and Timeline.fs; mine are NvimShot.fs and NvimTour.fs on purpose. To get one
  `LemDrive.dll`, add `Ansi.fs, NvimShot.fs, Nvim.fs, NvimTour.fs, NvimOracle.fs, NvimMain.fs` (from ../nvim) to its fsproj before
  Program.fs and add `| "nvim" :: rest -> NvimMain.dispatch rest` ahead of the Outcome-based dispatch in `main`. Until then
  LemDriveNvim.fsproj is what run-nvim-lemming and run-nvim-tour build.

## Next (old list, superseded by the A and B notes above)

1. Make tasks/fixture say what the oracle expects (EvalAnswer 480000, HelloBefore/After). Done when oracle constants and prompts agree.
2. run-ui-lemming dispatcher, README.
3. Smoke: one real run on a free model; verify nothing left in the dashboard.
4. nvim-shot / tours (screenshots) as main asked.
5. `nvim` case in the shared LemDrive Program.fs (wf-3) at merge time.

## Fix pass after the verifier (isolation, `;;`, prerequisites)

- `;` is typed as a raw byte (`send-keys -H 3b`): tmux 3.7 dropped a trailing `;` from a `send-keys` argument, so `...;;` never ended an F# cell. `Nvim.tmuxTextCalls`, tested through a real tmux.
- The editor sandbox binds only `<run>/w` (its `.git` read-only), not `<run>`: `lem_editor_bwrap_args`, `IsolationTests.fs`. The harness's git is pinned (`lem_git`, `Nvim.harnessGitPins`).
- The oracle's suite has no network; the lemming sandbox sees `~/.nuget/packages` read-only under a per-run overlay; `LEM_NO_BRIDGE=1` skips the unused MCP bridge for editor lemmings.
- `init.lua` enables ui2 so other sessions' messages do not become "Press ENTER" prompts. `lem_nvim_preflight` names every missing prerequisite before a run starts.
