# A notebook that is a window onto a live session

Roadmap item: `a-notebook-over-a-live-session` (Exploring, REPL). Plan written 2026-10-26 against master 59a8692c.

## Work order

Written 2026-11-11 against master 8975e898. Every `path:line` below was read at that commit. The plan below is sound; this section pins it to the tree and fixes two stale locations.

### 1. Outcome

Open the dashboard on a session, pick an `.fsx`, and see its cells as rows: source, a status chip (never run, fresh, edited since it ran, upstream edited, running, failed with the reason, session reset), the last output, and a value tree for each name the cell bound. Edit the file in any editor and the chips move without a reload; run a cell from the editor or from an agent and its tree updates in place, because it is one session. Slice 1 has no browser editing and no auto-run. Gate: a Playwright.NET journey `SageFs.Tests/DashboardNotebookBrowserTests.fs` over {editor buffer edit, save on disk, agent `send_fsharp_code`} x {the cell, a dependent, an unrelated cell} x {Ready, restarted mid-way}, zero console or Datastar errors, no cell skipped. It registers the way `DashboardLiveBindingsDockBrowserTests.fs:632` does: `Integration.register (Integration.Dedicated "--integration-browser")`. Headless Chromium on Linux is the only proven browser; nothing else is claimed.

### 2. Where the code is today

- Cells are evals, with no file position. `SageFs.Core/Features/CellDependencyGraph.fs:5-17` (`CellId = int`, `CellInfo`, `CellGraph`), `producedNames` at 41, identifier matching through `IdentifierScan` at 21. `SageFs.Core/Features/EvalStore.fs`: an entry has `Code` (47) and `Timestamp` (50), `StoredCell` 108, `Store` 138, `materializeGraph` 425. No file path anywhere in it.
- Staleness: `EvalProvenance.fs:8-10` (`Fresh | StaleUpstream`), computed by `Diagnostician.fs:69-77` and 201-215.
- MCP: `SageFs/McpTools.fs:2178` (`plan_ripple`), 2230 (`suggest_next_cell`), 2268 (`export_notebook`), 2481 (`get_cell_dependencies`); answers in `SageFs/McpAnalysis.fs:486-495`. SSE `cell_dependencies` at `SageFs.Core/SseWriter.fs:500`.
- A file position exists for one call: `/exec` at `SageFs/McpServer.fs:2756`, `block_start_line` read at 2773, stored in `LastEvalContext` at 2784 (type at 2531).
- New since the plan was written: `SageFs.Core/Features/Tweak/EvalWriteBack.fs`. Its `decide` maps `file_path` plus `block_start_line` to the declaration in the file that contains that line and writes the eval back (`McpTools.fs:789-791` documents the rule). That is a second, tested locator from a line to a declaration. The cell model should reuse it rather than add a third.
- Unsaved buffers reach the daemon: route `SageFs.Core/EndpointContracts.fs:57`, handler `McpServer.fs:3970`, dispatch at 3991, message `SageFs/SageFsApp.fs:191`, handled at 2121 (live testing only today).
- Value tree: `SageFs.Core/Features/LiveValueTree.fs` `WalkMode` 20, `LiveValueNode` 77, `LiveBindingValue` 91, `LiveValueSnapshot` 98, `MaxDepth = 6` 108, `MaxChildren = 50` 109, `MaxNodes = 10000` 117. Pushed as `live_bindings` by `SseWriter.fs:421-432`. `SageFs.Core/Features/LiveBindingsPane.fs` (new) holds the pane's walk-mode state. The dashboard renders in `SageFs/LiveBindingsDock.fs`.
- Panes: `SageFs/DockPanes.fs:15-17` (`LiveBindings | AppOutput`). `renderMainContent` is `SageFs/DashboardFragments.fs:2775` and `renderMainContentIn` 2539; the plan said `Dashboard.fs`, which only calls them.
- Cell boundaries, three client rules now: `sagefs-vscode/src/CodeBlocks.fs:17` (`getBlockBounds`), `sagefs.nvim/lua/sagefs/cells.lua:8` (`is_boundary_line`), and `sagefs.nvim/lua/sagefs/treesitter_cells.lua` (AST-based, new). Markers: `NotebookExport.fs:13,22` and `sagefs.nvim/lua/sagefs/notebook.lua:29`, different layouts.
- `samples/from-jupyter/notebook.fsx:22-23` still promises a pre-warmed swap and a 200 ms reset. Both are false (`docs/plans/README.md:190` lists it).
- Tests that exist: `EvalStoreTests.fs`, `LiveValueTreeTests.fs`, `DockPanesTests.fs`, `NotebookExportTests.fs`, `DiagnosticianTests.fs`, `EvalProvenanceTests.fs` (fsproj lines 652, 665, 1132, 644).

### 3. Steps

The order in "Steps" below holds. Registration, so nobody has to guess:

1. `SageFs.Tests/LiveNotebookTests.fs`, listed in `SageFs.Tests/SageFs.Tests.fsproj` right after `NotebookExportTests.fs` (line 644). RED: `CellSplitter.split` on `;;` and blank-line files, with fixtures in `SageFs.Tests/fixtures/notebook-cells/`. Then `SageFs.Core/Features/LiveNotebook.fs`, listed in `SageFs.Core/SageFs.Core.fsproj` right after `CellDependencyGraph.fs`. Domain. DECISION: one rule for cells. Options: (a) `;;` if present, else blank lines, the rule `CodeBlocks.fs` and `cells.lua` share; (b) the tree-sitter rule `treesitter_cells.lua` uses. Recommend (a) for slice 1, because the daemon has no parser for (b) and the fixtures make a later switch safe.
2. `CellAlign` properties. Same files. Domain.
3. `update` and `CellState`. Same files. Domain. Reuse `EvalWriteBack`'s locator for "which cell does `block_start_line` fall in".
4. Sim `SageFs.Simulation/LiveNotebookSim.fs` in `SageFs.Simulation.fsproj`, `SageFs.Tests/LiveNotebookSimTests.fs`. Domain.
5. Edge: `SageFs/LiveNotebookWiring.fs` subscribing to `BufferContentChanged` (`SageFsApp.fs:2121`), `/exec` completion (`McpServer.fs:2784`) and the `live_bindings` push; one call in the Elm loop.
6. Pane `SageFs/LiveNotebookPane.fs`, one line in `renderMainContent` (`DashboardFragments.fs:2775`). DECISION 2 below (main-area tab; recommended).
7. Browser journey, `--integration-browser`.
8. Docs and the sample fix (`notebook.fsx:22-23`).

### 4. How to check it

- Inner loop: a SageFs session on `SageFs.Tests`; eval the splitter on `samples/from-jupyter/notebook.fsx`, `samples/getting-started.fsx` and `samples/from-koans/*.fsx` (that is the first "Measure first" row, and it runs the moment step 1 compiles).
- `dotnet build SageFs.Tests -c Release`; `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Filtered: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary --filter-test-list "LiveNotebook"`.
- Acceptance: the unfiltered `--summary` (pure and sim), then `--integration-browser --summary` for step 7. Headless only; never a headed browser on this desktop.

### 5. Traps in this repo

- Falco.Markup and Falco.Datastar builders only in the pane, no attribute strings; one `#main` morph (the dashboard taste notes in `CLAUDE.md`).
- `DashboardTypes.fs` is on the `Plugin:` trailer list. Slice 1 adds no wire, so keep the model in new files and leave that file alone.
- No new MCP tool (`RatchetBudgets.toolList`, `SageFs.Tests/RatchetBudgets.fs:462`). If an agent should read the notebook, extend `get_cell_dependencies`.
- Durations: a debounce for re-splitting goes in `Timeouts` with a name, never a literal (`TimeoutLiteralsTests`).
- Relative units in the pane CSS (`DashboardTypeScaleTests`); monochrome glyphs only (`DashboardGlyphTests`).
- The browser tier is heavy: run it alone, check free memory first.

### 6. Size and order

M for slice 1, two to three weeks for one person. Steps 1 to 3 are serial and small (a day or two together). Step 4 and steps 5 to 6 run in parallel once step 3's types are fixed. First commit: step 1, `feat(notebook): CellSplitter splits an .fsx into cells the way the editors do, with fixtures`.

### 7. Not now

- Editor chips (`cell_status` SSE) and the Jupyter view: wire work that waits for slice 1 to be accepted; the plugin goes first when it comes.
- Auto-run of stale cells: slice 2, Off by default.
- Replacing the clients' cell rules with the daemon's: only after the fixtures run in all three repos.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L, M for the first slice |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: `CellSplitter.split` on `;;` and blank-line files. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

Open the dashboard on a session, pick an `.fsx` file, and see its cells as a list. Each cell shows its source, a status (never run, fresh, edited since it ran, upstream cell edited, running, failed with the reason), its last output as text, and a value tree for each name it bound. Edit the file in any editor and the statuses change as you type (unsaved buffers count) without a reload. Run a cell from the page or from the editor or from an agent and the tree for that cell updates in place, because all of them are one session. The first slice stops there: no editing in the browser, no auto-run.

Outcome gate: a Playwright.NET journey in the dashboard browser tier (Expecto, `DashboardLiveBindingsDockBrowserTests.fs` is the model) across a matrix of who edits and who runs: {editor buffer edit, file save on disk, agent `send_fsharp_code`} x {the cell itself, a cell it depends on, an unrelated cell} x {session Ready, session restarted mid-way}. Every cell asserts the chip text and the tree rows on the page and zero Datastar or console errors. No cell is skipped.

## What exists today

The pieces exist separately. What is missing is the thing that ties a cell to a place in a file.

- A cell in the daemon is an eval, not a file range. `SageFs.Core/Features/CellDependencyGraph.fs` defines `CellInfo { Id: CellId (int); Source; Produces; Consumes }`, built from eval history (`EvalStore.materializeGraph`). `EvalStore.fs` records code and output and keeps no file path or line (I grepped it for `FilePath`: no match). `producedNames` reads the `val` lines of FSI output.
- Staleness exists for eval cells: `EvalProvenance.Staleness = Fresh | StaleUpstream of CellId list` (`Features/EvalProvenance.fs`), computed by `Diagnostician` (`Diagnostician.fs:76, 204`) from the graph and a set of changed cells. `EvalRipple.RipplePlan` orders a cascade re-eval. MCP exposes them as `get_cell_dependencies`, `plan_ripple`, `suggest_next_cell`, `export_notebook` (`SageFs/McpTools.fs:2178-2481` at 8975e898); the `cell_dependencies` SSE event carries the graph.
- A place in a file is known only for a moment: `/exec` accepts `file_path` and `block_start_line` (`McpServer.fs:2756`, read at 2773), emits `eval_started` / `eval_result` SSE with them, stores them in `rctx.LastEvalContext`, and nothing keeps them afterwards.
- Cell boundaries are decided twice by clients, never by the daemon: `sagefs-vscode/src/CodeBlocks.fs` (`getBlockBounds`: `;;` delimiters if the file has any, else blank-line blocks) and `sagefs.nvim/lua/sagefs/cells.lua` (`is_boundary_line` and friends). `NotebookExport.fs` (F#) and `sagefs.nvim/lua/sagefs/notebook.lua` both read and write `(* @sagefs-cell[...] *)` markers, with different layouts (`@sagefs-cell[0] label:x deps:1` against `@sagefs-cell[1]: duration_ms=..`). I did not test whether one parses the other's output.
- Unsaved edits reach the daemon: `POST /api/sessions/{sid}/buffer-changed` dispatches `SageFsMsg.BufferContentChanged (Some sid, filePath, content)` into the Elm loop (`McpServer.fs:3970-3991`). VS Code and Neovim post it for live testing as you type.
- The value tree: `Features/LiveValueTree.fs` (`LiveValueSnapshot`, `LiveBindingValue`, `LiveValueNode`, walk modes Safe / Everything / Off, caps `MaxDepth` 6, `MaxChildren` 50, `MaxNodes` 10000) pushed as the `live_bindings` SSE event after every eval; there is no GET. The dashboard renders it in `SageFs/LiveBindingsDock.fs` (pinning, filter by name, a flash on the value that changed, click-to-run for held getters). VS Code consumes the event (`LiveTestingListener.fs:626`), Neovim renders it in `bindings_view.lua`. Jupyter does not.
- Panes: `SageFs/DockPanes.fs` has `DockPane = LiveBindings | AppOutput` with a pure decision; the dashboard has a single `#main` morph (AGENTS taste: every state is a full `renderMainContent`).
- `samples/from-jupyter/notebook.fsx` says a crashed session swaps for "a pre-warmed one". The standby pool was removed (`docs/decisions.md`, "No standby worker pool"), so that sample line is false and `hard_reset_fsi_session in 200ms` is unmeasured. Fix the sample when this ships its own copy of the pitch.

## Design

What it builds on. Three candidates, one choice.

| Base | Cells come from | Value tree | Live update | Cost / problem |
|---|---|---|---|---|
| Jupyter kernel (`jupyter-kernel-that-works` Phase B) | the `.ipynb` the frontend owns | `application/json` MIME | `update_display_data` | Source of truth becomes an `.ipynb`, the thing `coming-from-jupyter.md` argues against; edits made in a normal editor are invisible; needs a JS renderer in VS Code if it lacks a JSON tree |
| Neovim buffer (`cells.lua`, `bindings_view.lua`, `cell_highlight.lua`) | the `.fsx` | existing pane | existing SSE | Neovim only, and it already half exists |
| Dashboard pane over the `.fsx` | the `.fsx`, read by the daemon | reuse `LiveBindingsDock` rows | the SSE morph | New daemon-side cell model; every editor and the agent get it for free because it is a page |

Choice: the dashboard, as the first slice. The `.fsx` stays the notebook (what the Jupyter guide promises), the daemon owns cell identity once instead of three clients owning it separately, F# and the Playwright.NET suite cover it, and an agent's eval shows up in it because it is the same session. The Jupyter kernel is a second view of the same model later, and Neovim and VS Code can show the chips once the model exists (that is wire work, deliberately not in slice 1).

Types, in a new vertical slice `SageFs.Core/Features/LiveNotebook.fs` (pure, no Falco):

```fsharp
type Cell =
  { Index: int; StartLine: int; EndLine: int; Source: string; Hash: SourceHash }

type CellMatch =            // result of aligning the file's cells before and after an edit
  | Same of Cell
  | Edited of before: Cell * after: Cell
  | Added of Cell
  | Removed of Cell

type CellState =
  | NeverRun
  | Running
  | Fresh of EvalReceipt              // output text, duration, names produced, the generation it ran in
  | EditedSinceRun of EvalReceipt
  | UpstreamEdited of EvalReceipt * upstream: SourceHash list
  | Failed of reason: string          // the text a user can act on, never an empty string
  | SessionReset of EvalReceipt       // the session restarted; the receipt is history

type NotebookMsg =
  | FileText of path: string * text: string * origin: TextOrigin   // TextOrigin = Disk | EditorBuffer
  | CellStarted of SourceHash
  | CellFinished of SourceHash * Result<EvalReceipt, string>
  | BindingsPushed of LiveValueSnapshot
  | SessionRestarted
```

`CellSplitter.split : string -> Cell list` applies the same rule as the two clients. `CellAlign.align : Cell list -> Cell list -> CellMatch list` matches cells by hash first (an unchanged cell keeps its identity when lines are inserted above it) and by position second (a changed cell in the same slot is `Edited`). `LiveNotebook.update : Model -> NotebookMsg -> Model` is a total fold; upstream edits use `CellDependencyGraph.analyzeCell` on cell source against names produced by earlier cells.

Edge. A small subscriber in the daemon (the Elm loop already receives `BufferContentChanged`; a `.fsx` watcher is the same `FileWatcher` the hot reload path uses) turns buffer and file events into `FileText`, `/exec` completions into `CellFinished` (the call already knows `file_path` and `block_start_line`, which map to a cell by the line), and the `live_bindings` push into `BindingsPushed`. The dashboard fragment is `SageFs/LiveNotebookPane.fs`: Falco.Markup plus Falco.Datastar builders only (no hand-written attribute strings), rows rendered by the existing `LiveBindingsDock` row function filtered to the names a cell produced, the selected file held in a Datastar signal like the other panes. One morph, one `#main`: the pane is part of `renderMainContent`, never a second render path. `DockPanes.DockPane` gets a case only if the notebook sits in the dock; otherwise it is a main-area view and `DockPanes.fs` is untouched. Decide in step 6 by looking at both (see Decisions).

Wire and clients. Slice 1 adds no HTTP route, MCP tool or SSE event: the model is daemon state drawn by the dashboard. `apiVersion` stays 4 and no `Plugin:` trailer is needed unless a commit touches `DashboardTypes.fs` (it is on the trailer list; use a new file instead). The follow-up that sends `cell_status` to editors is an additive SSE event: `sagefs.nvim` first (it has `cell_highlight.lua`), then `sagefs-vscode` (it has CodeLens and decorations). Do not start it before slice 1 is accepted. Token cost: no MCP tool is added; if an agent should read the notebook, extend `get_cell_dependencies` with the file range rather than adding a tool.

Persistence. None in slice 1: the model is rebuilt from the file and the eval history. A restarted daemon shows every cell as `SessionReset` or `NeverRun`, which is the truth.

## Tests

RED tests, in order.
1. `LiveNotebookTests.fs` (new): `CellSplitter.split` on a file with `;;` delimiters gives cells with the right `StartLine`/`EndLine`; on a file with none, blank lines split; a `;;` inside a string or comment does not split (the rule `cells.lua` implements with a quote count). Compile-red until the module exists.
2. Same file: properties with FsCheck. `align xs xs` is all `Same`; inserting a cell at any index leaves every other cell `Same`; editing one cell's text gives exactly one `Edited`; `Added` plus `Removed` plus matched cover both inputs exactly once.
3. `update`: "editing a cell marks it EditedSinceRun and its dependents UpstreamEdited"; "running it again makes it Fresh and dependents still UpstreamEdited until they run"; "SessionRestarted turns every Fresh into SessionReset"; "a result for a cell that no longer exists changes nothing".
4. Conformance fixtures: put 6 to 10 `.fsx` files with expected cell ranges in `SageFs.Tests/fixtures/notebook-cells/` and read them from the F# splitter test now. The Lua and Fable clients run the same fixtures in their own repos in a follow-up, so three implementations of one rule stop drifting. (A busted spec belongs in `sagefs.nvim`, not here.)

Simulation (`SageFs.Simulation/LiveNotebookSim.fs` plus Generators and Invariants, same shape as `FileReloadRoutingSim`). Pool: one file of 1..12 cells with a dependency graph; ops as seeded data: EditCell, InsertCell, DeleteCell, RunCell, RunAll, RestartSession, BufferVsDisk (the same text arrives from both origins, in either order), late result (a `CellFinished` for an old hash). Faults from `FaultInjection.fs`: drop, duplicate and reorder messages. Invariants: (N1) a cell is `Fresh` only if its current hash equals the hash it ran with and no upstream hash changed since; (N2) no message ever produces two cells with one index; (N3) a late result for a stale hash never makes a current cell `Fresh`; (N4) after `RunAll` in dependency order with no edits, every cell is `Fresh` or `Failed`; (N5) buffer text newer than disk wins, and the same text from both origins is idempotent. Twin: a model that keys state by line number instead of hash (inserting above shifts every status) must violate N1 or N2 so the invariants show teeth.

Outcome gate: the browser journey above, in `SageFs.Tests/DashboardNotebookBrowserTests.fs`, registered as a Dedicated browser-tier suite next to `DashboardLiveBindingsDockBrowserTests.fs` (read how that file registers with `Integration.register (Dedicated ...)` before writing). The page must also be correct with no file picked: a first-class empty state that says what to do (pick a file, or run a block from your editor), never a blank (the "optional parameter must not break the page" rule). Waiting: assertions wait on the SSE-driven DOM change (`WaitForSelector` on the chip text), never on a sleep.

## Steps

1. `CellSplitter` with RED test 1 and fixtures (test 4). Owns `Features/LiveNotebook.fs` (splitter part) and the tests file.
2. `CellAlign` with the properties of test 2. Same files; serial after 1.
3. `update` and `CellState` with test 3. Same files; serial after 2.
4. Sim files (`SageFs.Simulation/LiveNotebook*.fs`, registered in `SageFs.Simulation.fsproj`) and `LiveNotebookSimTests.fs`. File-disjoint from 5 and 6; can start once step 3's types are fixed.
5. The edge: subscribe to `BufferContentChanged`, `.fsx` file changes, `/exec` completion and `live_bindings` pushes. Owns the daemon wiring file (new, `SageFs/LiveNotebookWiring.fs`) and the one call site in the Elm loop. Needs 3.
6. The pane `LiveNotebookPane.fs` and its registration in `renderMainContent`. Owns the pane file and one edit in `DashboardFragments.fs`. Needs 5 for live data; can be built against a fake model in parallel with 5.
7. Browser journey. Needs 5 and 6.
8. Docs: `docs/coming-from-jupyter.md` gets a short section and a screenshot recorded the way `scripts/record-demos.fsx` does; fix the false "pre-warmed" line in `samples/from-jupyter/notebook.fsx`.

## Measure first

- Is the cell the right grain? Take `samples/from-jupyter/notebook.fsx`, `samples/getting-started.fsx` and three of the `samples/from-koans` files and run `CellSplitter` over them by hand (a REPL session, no build). Count cells, count cells per `val` produced. If most cells bind nothing (they are expressions), the value tree is mostly empty and the default output must be the printed value, with the tree for bindings only.
- Cost of the tree per eval. `LiveValueTree` has a one-second walk budget; time the push on `SageFs.Core`'s session with 200 bindings (`MaxBindings`). If it exceeds a few hundred milliseconds the pane must show the last snapshot and say so rather than block the chip updates. I have no number; the budget in the code is the only figure.
- Does `BufferContentChanged` fire often enough to feel live without a rate limit, and does re-splitting a 2,000-line file per keystroke show up in the Elm loop's `update_ms` meter (`Instrumentation.fs`)? If so, debounce at the subscriber, with the debounce a `Timeouts` constant named for what it waits for.

## Risks and dependencies

- Three-way drift of the cell rule until the fixtures run in all three repos. Slice 1 reduces it to two; the follow-up closes it.
- Cell identity by hash breaks when two cells have identical text; `Index` breaks the tie and N2 pins it.
- Dependency detection is textual (`IdentifierScan`), so a shadowed name can give a wrong `UpstreamEdited`. It errs toward marking stale, which is the safe side.
- The dashboard is edited by several workstreams (`move-and-dock-dashboard-panes`, `command-palette-and-one-timeline`). Keep the pane in its own file and the `DashboardFragments.fs` edit to one line.
- Depends on nothing for slice 1. The Jupyter view depends on `jupyter-kernel-that-works` Phase B. Editor chips depend on this model and need a wire change landed in `sagefs.nvim` first.

## Size

L overall, M for slice 1 (two to three weeks): a new pure model with a sim, a daemon subscriber, a dashboard pane and a browser journey are four separate pieces, each small, none of them borrowed whole.

## Decisions for Will

1. Dashboard first, or Neovim first? Recommendation: dashboard. It serves every editor and the agent, and it is F# with an existing browser tier. Neovim has `cells.lua` already and is where you work; if you would rather see it there first, the same pure model still comes first and the Lua side only adds a renderer.
2. Main-area view or a dock pane? Recommendation: main-area tab beside the session output. A cell list is tall and wide; the dock's ceiling is short, and the bindings dock stays where it is.
3. Auto-run of stale cells is slice 2. Policy options: Off, Dependents, All. Recommendation: Off by default, per-file switch, because a cell that deletes a table must never re-run on an edit.
