/// The Neovim client's scenarios (demo-actors-plan.md §1.3/§2.2): `repl-
/// neovim` (hero), `lt-neovim`, and `hr-neovim-neovim-{web,raylib,console}`
/// (joint with the App co-actor). Built the same way the Dashboard scenarios
/// are — a plain `Scenario` value, a caption/action/expectation/dwell per
/// step — reusing `Target.NvimCommandLine`/`Expectation.NvimBufferContains`
/// (the two Neovim-specific `Domain` cases Wave 1 already reserved) plus the
/// actor-agnostic `Target.WindowCenter`/`Action.Await`/`Action.Chord`.
///
/// STATUS: `Runtime.fs`'s `wireStepOf`/`CellAgent.fs`'s `assembleActors` are
/// now genuinely wired for the Neovim actor (the cross-actor completion gap
/// this file's STATUS note used to describe is closed) — every step below
/// resolves to a real click/type/observe against a real kitty+nvim. Session
/// creation goes through the pinned plugin's own non-interactive
/// `:SageFsCreateSession <project>` (`ClientCommand.CreateSession`,
/// `Runtime.Neovim.fs`'s pinned commit 90bc3f41) rather than typing the bare
/// command, which opens an interactive picker synthetic keystrokes cannot
/// answer. Readiness/scan/live-testing-toggle checks observe the SAME shared
/// Dashboard narrator pane every joint scenario co-launches (`Expectation.
/// PageTextContains`, `Runtime.fs`'s `expectationWire` already routes this
/// for `Client.Neovim`) rather than `NvimBufferContains` — that daemon-side
/// status text is never written into nvim's own buffer/extmarks (confirmed
/// against `sagefs.nvim`'s own source: `notify()`/`statusline()` are not
/// buffer content `Actors/Neovim.fs`'s `visibleTextExpr` can ever see), so a
/// buffer-content check for it could never genuinely pass.
/// `NvimBufferContains` stays exactly where it belongs: real typed
/// expressions and real eval results actually rendered into nvim's own
/// buffer/extmarks.
module SageFs.Demos.Scenarios.Neovim

open SageFs.Demos.Domain

let private webappProgram = { Sample = Sample.WebappDatastar; RelativePath = "Program.fs" }
let private raylibProgram = { Sample = Sample.RaylibGame; RelativePath = "Program.fs" }
let private consoleTicker = { Sample = Sample.ConsoleTicker; RelativePath = "Ticker.fs" }

/// The daemon's own session-status label, scoped to the ACTUAL badge
/// element rather than the whole `body` — mirrors `Scenarios.VsCode.fs`'s
/// identically-named/identically-justified private binding (same real,
/// snapshot-verified DOM: `SageFs.Tests/snapshots/DashboardSnapshotTests.
/// dashboard_sessionStatus_ready.verified.html` —
/// `<div id="session-status"><span class="status status-ready">Ready</span>
/// ...`). Never the unscoped `body:has-text("Ready")` `Scenarios.fs`'s own
/// dashboard scenarios deliberately use for a DIFFERENT, unrelated reason
/// (that file's own doc) — an unscoped check here would false-positive on
/// the cmdline area's idle `"SageFs -- ready"` label the instant the SSE
/// connection comes up, well before the SESSION itself is actually Ready
/// (`DashboardTypes.fs`'s `cmdlineLabel`).
let private sessionStatusSelector = "#session-status .status"

let private outputPanelSelector = "[data-testid=session-output]"

let private liveTestingPanelSelector = "#live-testing-panel"

/// `repl-neovim` (§6's matrix; hero, §3/§4). A real back-and-forth at nvim's
/// own command line against a genuinely opened project: create a session for
/// the real `WebappDatastar` project (the plugin's `:SageFsCreateSession`
/// discovers it from nvim's own cwd — the same real-project doctrine
/// `hello-dashboard`'s own doc argues for: a Quick Start temp session has
/// nothing real to evaluate against), wait for it to reach Ready, then
/// evaluate two expressions against the SAME live session — one general,
/// one reading the project's own mutable state — proving the REPL keeps
/// state across evaluations, exactly `repl-dashboard`'s own narrative.
///
/// Typing and running a command are deliberately SEPARATE steps, never one
/// `Text` containing both: `Cadence.charToKey` only maps `'\n'`/`'\t'`
/// inside typed text to `Key.Return`/`Key.Tab` — there is no keysym for a
/// raw ASCII ESC byte embedded in a string, so `Keymap.resolve` would
/// silently drop it (`resolve` returns `[]` for an unmapped keysym, not a
/// loud failure) and the following `:SageFsEvalLine` text would land as
/// literal buffer content instead of running, while still in insert mode.
/// Leaving insert mode is its own `Action.Chord [ Key.Escape ]` step.
let replNeovim: Scenario =
  { Id = ScenarioId.derive Capability.Repl Client.Neovim AppKind.NoApp
    Capability = Capability.Repl
    Client = Client.Neovim
    App = AppKind.NoApp
    Sample = Sample.WebappDatastar
    Layout = LayoutTemplate.EditorFull
    Steps =
      [ { Caption = Caption.mk "1/8 · Create a session for the real project"
          // `:SageFsCreateSession <project>` (pinned commit 90bc3f41) — the
          // non-interactive path; the bare command would open a
          // `vim.ui.select` picker no scripted actor can answer.
          Action = Action.Setup(ClientCommand.CreateSession Sample.WebappDatastar)
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "2/8 · It warms up and goes green"
          Action = Action.Await Signal.sessionReady
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/8 · Type a plain expression"
          Action = Action.Type(Target.WindowCenter ActorId.Neovim, Text.mk "oList.sum [ 1 .. 10 ]", CadenceSeed.ofId "repl-neovim-eval-1-type")
          Expect = Expectation.NvimBufferContains(Text.mk "List.sum")
          Dwell = Dwell.short }
        { Caption = Caption.mk "4/8 · Leave insert mode"
          Action = Action.Chord [ Key.Escape ]
          Expect = Expectation.NvimBufferContains(Text.mk "List.sum")
          Dwell = Dwell.short }
        { Caption = Caption.mk "5/8 · Evaluate it — instant result"
          Action = Action.Type(Target.NvimCommandLine, Text.mk ":SageFsEvalLine\n", CadenceSeed.ofId "repl-neovim-eval-1-run")
          // "int = 55" (not a bare "55") — deliberately specific, mirroring
          // `Scenarios.fs`'s own identically-reasoned "int = 3" check: a
          // bare digit substring can trivially, falsely match unrelated
          // screen furniture (line/column numbers, warmup progress
          // counters). "int = 55" only ever appears in FSI's own real
          // "val it: int = 55" result line (`sagefs.nvim`'s own
          // `format.format_inline` renders that line's own output verbatim
          // into the extmark this step's buffer-contains check reads).
          Expect = Expectation.NvimBufferContains(Text.mk "int = 55")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "6/8 · Type an expression on the project's own state"
          Action =
            Action.Type(
              Target.WindowCenter ActorId.Neovim,
              Text.mk "oSageFs.Samples.WebappDatastar.Program.todos.Length",
              CadenceSeed.ofId "repl-neovim-eval-2-type"
            )
          Expect = Expectation.NvimBufferContains(Text.mk "todos.Length")
          Dwell = Dwell.short }
        { Caption = Caption.mk "7/8 · Leave insert mode"
          Action = Action.Chord [ Key.Escape ]
          Expect = Expectation.NvimBufferContains(Text.mk "todos.Length")
          Dwell = Dwell.short }
        // Same live session, second eval — the REPL keeps state, so this
        // reads the project's own mutable state right after a totally
        // unrelated expression, proving the session never restarted between
        // the two evals (exactly `repl-dashboard`'s own proof shape).
        { Caption = Caption.mk "8/8 · Evaluate the project's own state"
          Action = Action.Type(Target.NvimCommandLine, Text.mk ":SageFsEvalLine\n", CadenceSeed.ofId "repl-neovim-eval-2-run")
          // "int = 3" (not a bare "3") — same false-positive risk as the
          // "55" check above, an even sharper one here: the warmup output
          // this same buffer/dashboard already showed earlier in the run
          // literally contains "[3/4]" (`AppState.fs`'s own progress
          // logging) — a bare "3" would trivially match THAT, not this
          // eval's own result line.
          Expect = Expectation.NvimBufferContains(Text.mk "int = 3")
          Dwell = Dwell.long } ]
    Cost = CostClass.web
    Masks = [] }

/// `lt-neovim` (§6's matrix; step 4 blocked — see the LT blocker note below).
/// Mirrors `lt-dashboard`'s exact narrative and exact blocker
/// (`Scenarios.fs`'s `ltDashboard` doc): opens the real `FromCSharp` test
/// project, turns live testing on via `:SageFsEnableTesting`, then waits for
/// a real passed-test signal. Steps 1-3 are the genuine, real workflow;
/// step 4 is wired but honestly left NOT YET PASSING, per this task's own
/// instruction — the blocker is `SageFs.Core`'s live-testing discovery never
/// completing for a project opened this way, a product bug out of a demo
/// island's scope (AGENTS.md), identical to `lt-dashboard`'s own documented
/// blocker, not chased here.
let ltNeovim: Scenario =
  { Id = ScenarioId.derive Capability.LiveTesting Client.Neovim AppKind.NoApp
    Capability = Capability.LiveTesting
    Client = Client.Neovim
    App = AppKind.NoApp
    Sample = Sample.FromCSharp
    Layout = LayoutTemplate.EditorFull
    Steps =
      [ { Caption = Caption.mk "1/4 · Create a session for the real test project"
          Action = Action.Setup(ClientCommand.CreateSession Sample.FromCSharp)
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/4 · It warms up and goes green"
          Action = Action.Await Signal.sessionReady
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/4 · Turn live testing on"
          Action = Action.Type(Target.NvimCommandLine, Text.mk ":SageFsEnableTesting\n", CadenceSeed.ofId "lt-neovim-enable-testing")
          // "Live Testing: ON" (not a bare "ON") — matches `Scenarios.
          // VsCode.fs`'s `ltVsCode` step 3 exactly (the SAME shared
          // dashboard panel both clients' toggles observe).
          Expect = Expectation.PageTextContains(liveTestingPanelSelector, "Live Testing: ON")
          Dwell = Dwell.medium }
        // NOT YET PASSING — see the doc comment above. Wired, never faked.
        // The observation channel is corrected to the real one
        // (`lt-dashboard`'s own step 4) even though this step cannot
        // reach it today — never left pointed at a channel that could
        // never observe it either way.
        { Caption = Caption.mk "4/4 · SageFs runs the real tests — they pass"
          Action = Action.Await Signal.testRunCompleted
          Expect = Expectation.PageTextContains(liveTestingPanelSelector, "✓")
          Dwell = Dwell.long } ]
    Cost = CostClass.console
    Masks = [] }

/// The three hot-reload scenarios (§6's matrix items 7-9), joint with the
/// App co-actor — now genuinely wired (`Actors/App.fs`/`Runtime.App.fs`,
/// `CellAgent.fs`'s lazy launch right after a real `"run-app"` dispatch):
/// real edit-save narrative, a real App window, a real
/// `Expectation.AppOutputChanged` observation through it.
let private hotReloadScenario (appKind: AppKind) (sample: Sample) (file: SampleFile) (knobText: string) (expectText: string) : Scenario =
  { Id = ScenarioId.derive Capability.HotReload Client.Neovim appKind
    Capability = Capability.HotReload
    Client = Client.Neovim
    App = appKind
    Sample = sample
    Layout = LayoutTemplate.EditorLeft
    Steps =
      [ { Caption = Caption.mk "1/6 · Create a session for the real project"
          Action = Action.Setup(ClientCommand.CreateSession sample)
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "2/6 · Run the app"
          Action = Action.Setup ClientCommand.RunApp
          Expect = Expectation.AppState AppRunStateCase.Running
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/6 · Edit a knob"
          Action = Action.Type(Target.EditorPosition(file, 1, 1), Text.mk knobText, CadenceSeed.ofId "hr-neovim-edit")
          Expect = Expectation.NvimBufferContains(Text.mk knobText)
          Dwell = Dwell.short }
        { Caption = Caption.mk "4/6 · Leave insert mode"
          // Same Chord doctrine as `replNeovim` (see its doc): escape is its
          // own step, never folded into the typed `Text` above.
          Action = Action.Chord [ Key.Escape ]
          Expect = Expectation.NvimBufferContains(Text.mk knobText)
          Dwell = Dwell.short }
        { Caption = Caption.mk "5/6 · Save"
          Action = Action.Type(Target.NvimCommandLine, Text.mk ":w\n", CadenceSeed.ofId "hr-neovim-save")
          Expect = Expectation.EditorSaved file
          Dwell = Dwell.short }
        { Caption = Caption.mk "6/6 · Watch it hot-reload"
          Action = Action.Await Signal.hotReloadApplied
          Expect = Expectation.AppOutputChanged(Region.appHeading)
          Dwell = Dwell.long } ]
    Cost = (match appKind with AppKind.Web -> CostClass.web | AppKind.Raylib -> CostClass.raylib | _ -> CostClass.console)
    Masks = [] }

let hrNeovimNeovimWeb: Scenario = hotReloadScenario AppKind.Web Sample.WebappDatastar webappProgram "// hot-reloaded" "hot-reloaded"
let hrNeovimNeovimRaylib: Scenario = hotReloadScenario AppKind.Raylib Sample.RaylibGame raylibProgram "let starMaxSpeed  = 400.0f" "400.0f"
let hrNeovimNeovimConsole: Scenario = hotReloadScenario AppKind.Console Sample.ConsoleTicker consoleTicker "// hot-reloaded" "hot-reloaded"

/// Every scenario filmed through the Neovim client. `Scenarios.All.fs`
/// (Island F) aggregates this with every other client's own list.
let scenarios: Scenario list = [ replNeovim; ltNeovim; hrNeovimNeovimWeb; hrNeovimNeovimRaylib; hrNeovimNeovimConsole ]
