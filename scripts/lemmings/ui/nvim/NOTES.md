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

## Next

1. Make tasks/fixture say what the oracle expects (EvalAnswer 480000, HelloBefore/After). Done when oracle constants and prompts agree.
2. run-ui-lemming dispatcher, README.
3. Smoke: one real run on a free model; verify nothing left in the dashboard.
4. nvim-shot / tours (screenshots) as main asked.
5. `nvim` case in the shared LemDrive Program.fs (wf-3) at merge time.
