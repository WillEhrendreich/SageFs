/// The Neovim client's scenarios (demo-actors-plan.md §1.3/§2.2): `repl-
/// neovim` (hero), `lt-neovim`, and `hr-neovim-neovim-{web,raylib,console}`
/// (joint with the App co-actor). Built the same way the Dashboard scenarios
/// are — a plain `Scenario` value, a caption/action/expectation/dwell per
/// step — reusing `Target.NvimCommandLine`/`Expectation.NvimBufferContains`
/// (the two Neovim-specific `Domain` cases Wave 1 already reserved) plus the
/// actor-agnostic `Target.WindowCenter`/`Action.Await`/`Action.Chord`.
///
/// STATUS (read before assuming any of these record): none of these can
/// genuinely record yet, for a reason outside this file entirely —
/// `Runtime.fs`'s `wireStepOf` (never-touch) has no case that turns a
/// Neovim-shaped `Target`/`Expectation` into a wire selector string this
/// island's `Actors/Neovim.fs` understands, and `CellAgent.fs`'s
/// `assembleActors` (also never-touch) only ever builds the `"dashboard"`
/// actor. Recording any scenario below today would silently no-op every
/// step (no click, no type, `ExpectSelector = None` defaults to `observed =
/// true`) and report a FAKE "Passed" — exactly what this job forbids. This
/// is reported to the main thread as a cross-actor Island-F completion gap
/// (identical for VsCode/App/Agent), not chased or worked around here. What
/// IS genuinely proven: `Actors/Neovim.fs` itself, directly, against a real
/// kitty+nvim on a real Xvfb (`NeovimActorTests.fs`).
module SageFs.Demos.Scenarios.Neovim

open SageFs.Demos.Domain

let private webappProgram = { Sample = Sample.WebappDatastar; RelativePath = "Program.fs" }
let private raylibProgram = { Sample = Sample.RaylibGame; RelativePath = "Program.fs" }
let private consoleTicker = { Sample = Sample.ConsoleTicker; RelativePath = "Ticker.fs" }

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
          Action = Action.Type(Target.NvimCommandLine, Text.mk ":SageFsCreateSession\n", CadenceSeed.ofId "repl-neovim-create-session")
          // NOT YET CONFIRMED against a live recording (blocked on the seam
          // gap documented above, so there is nothing to record against
          // yet) — a reasonable reading of "the session reached Ready" for
          // this actor, kept honest rather than invented once verified.
          Expect = Expectation.NvimBufferContains(Text.mk "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "2/8 · It warms up and goes green"
          Action = Action.Await Signal.sessionReady
          Expect = Expectation.NvimBufferContains(Text.mk "Ready")
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
          Expect = Expectation.NvimBufferContains(Text.mk "55")
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
          Expect = Expectation.NvimBufferContains(Text.mk "3")
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
          Action = Action.Type(Target.NvimCommandLine, Text.mk ":SageFsCreateSession\n", CadenceSeed.ofId "lt-neovim-create-session")
          Expect = Expectation.NvimBufferContains(Text.mk "Scanned")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/4 · It warms up and goes green"
          Action = Action.Await Signal.sessionReady
          Expect = Expectation.NvimBufferContains(Text.mk "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/4 · Turn live testing on"
          Action = Action.Type(Target.NvimCommandLine, Text.mk ":SageFsEnableTesting\n", CadenceSeed.ofId "lt-neovim-enable-testing")
          Expect = Expectation.NvimBufferContains(Text.mk "ON")
          Dwell = Dwell.medium }
        // NOT YET PASSING — see the doc comment above. Wired, never faked.
        { Caption = Caption.mk "4/4 · SageFs runs the real tests — they pass"
          Action = Action.Await Signal.testRunCompleted
          Expect = Expectation.NvimBufferContains(Text.mk "✓")
          Dwell = Dwell.long } ]
    Cost = CostClass.console
    Masks = [] }

/// The three hot-reload scenarios (§6's matrix items 7-9) are joint with the
/// App co-actor, which has not landed (its `Actors/App.fs`/`Runtime.App.fs`
/// are still Island-F compiling stubs as of this island's own work — plan
/// §4's own build order runs Island App BEFORE the editor islands' hot-
/// reload scenarios). These three are still real, compilable `Scenario`
/// values — not stubs — using the same edit-save narrative every hot-reload
/// scenario in this codebase will share, but they are not attempted for
/// recording by this island: there is no `TargetActor = "app"` wiring yet
/// either (the same seam gap this file's top doc names), so a joint step
/// would have nothing on the other end to observe.
let private hotReloadScenario (appKind: AppKind) (sample: Sample) (file: SampleFile) (knobText: string) (expectText: string) : Scenario =
  { Id = ScenarioId.derive Capability.HotReload Client.Neovim appKind
    Capability = Capability.HotReload
    Client = Client.Neovim
    App = appKind
    Sample = sample
    Layout = LayoutTemplate.EditorLeft
    Steps =
      [ { Caption = Caption.mk "1/6 · Create a session and run the app"
          Action = Action.Type(Target.NvimCommandLine, Text.mk ":SageFsCreateSession\n", CadenceSeed.ofId "hr-neovim-create-session")
          Expect = Expectation.NvimBufferContains(Text.mk "Ready")
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
