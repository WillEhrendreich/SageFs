/// The VS Code client's scenarios (demo-actors-plan.md §1.3/§2.1: `lt-
/// vscode`, `repl-vscode`, `hr-dashboard-vscode-{web,raylib,console}`).
/// `Scenarios.All.fs` already references `scenarios` below (Island F left
/// it an empty list) — this island fills it in.
///
/// INTEGRATION NOTE (read before wiring `record` against these): every
/// `Scenario` below is real, valid `Domain.Scenario` data — captions,
/// samples, dwell times, the exact target/action/expectation vocabulary
/// `Domain.fs` already defines — but `Runtime.fs`'s private `wireStepOf`
/// (a never-touch seam core for this island, demo-actors-plan.md §7's
/// table) only knows how to flatten `Target.DashboardElement`/
/// `DashboardCssSelector` into a `Wire.WireStep`'s click/type selectors
/// today; `Target.EditorPosition`/`PaletteItem`/`WindowCenter` (this
/// file's own VS Code actions) fall through its `_ -> None` case, so those
/// steps' `ClickSelector`/`TypeText` come out empty until `wireStepOf`
/// gains a VS Code-shaped sibling to `dashboardSelector` — exactly the
/// "single extra call site" integration gap flagged to the main thread
/// (this island's `Actors/VsCode.fs`/`Runtime.VsCode.fs` are ready to be
/// that sibling's targets). The `Expect` side is a different story: every
/// step below observes through the SAME dashboard narrator selectors
/// `Scenarios.fs`'s own dashboard scenarios use (`EditorFull`'s layout
/// places a Dashboard pane specifically as a narrator alongside the
/// editor, demo-actors-plan.md §2.1) — since the daemon's session state is
/// shared across every client, an eval/toggle driven from VS Code shows up
/// in the SAME dashboard view a Dashboard-client scenario would read, so
/// `wireStepOf`'s EXISTING `dashboardSelector`/`PageTextContains` handling
/// already flattens these `Expect` fields correctly today, with zero
/// changes needed. This is why the "spike" honestly proved the
/// launch+control-channel (`Actors/VsCode.fs`'s module doc) but not a full
/// `record lt-vscode` run — the click/type half of the pipe needs that one
/// remaining `wireStepOf` case.
module SageFs.Demos.Scenarios.VsCode

open SageFs.Demos.Domain

/// Mirrors `Scenarios.fs`'s own private narrator selectors verbatim (the
/// SAME real `DashboardFragments.fs` DOM contract — a still frame at a
/// timeout has already confirmed `body:has-text("Ready")` resolves even
/// when a narrower selector doesn't, per that file's own note) —
/// redeclared here rather than referenced across the module boundary
/// (`Scenarios.Dashboard`'s `let private` bindings are not exported), the
/// same way `CellAgent.fs` redeclares the cell's fixed `:99` display
/// rather than importing a shared literal.
let private sessionStatusSelector = "body"

let private outputPanelSelector = "[data-testid=session-output]"

let private liveTestingPanelSelector = "#live-testing-panel"

/// `lt-vscode` (§6's matrix, §5's LT blocker note): opens the real
/// `SageFs.Samples.FromCSharp` test project as the VS Code workspace root
/// (the project is already open because that IS the launched workspace —
/// unlike the dashboard's "Open Directory" picker flow, an editor client
/// has no separate "open project" UI beat to film), turns live testing on
/// from the editor's command palette, and watches the SAME "OFF"→"ON"→
/// passed-count narration `lt-dashboard` watches (the daemon's live-testing
/// state is one shared source of truth regardless of which client flipped
/// the toggle).
///
/// STEPS 1-3 are the genuine, buildable-today shape. Step 4 mirrors
/// `lt-dashboard`'s own honestly-documented blocker EXACTLY (demo-actors-
/// plan.md §5): the daemon's live-testing discovery-completion event never
/// fires for a project opened this way, root-caused in `SageFs.Core`/
/// `SageFs`'s discovery effect handlers — out of this demo island's scope
/// per `AGENTS.md`. Left wired, NOT faked, exactly like `lt-dashboard`.
let ltVsCode: Scenario =
  { Id = ScenarioId.derive Capability.LiveTesting Client.VsCode AppKind.NoApp
    Capability = Capability.LiveTesting
    Client = Client.VsCode
    App = AppKind.NoApp
    Sample = Sample.FromCSharp
    Layout = LayoutTemplate.EditorFull
    Steps =
      [ { Caption = Caption.mk "1/4 · A real test project is open in the editor"
          Action = Action.Setup(ClientCommand.OpenFile { Sample = Sample.FromCSharp; RelativePath = "Hello.fs" })
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/4 · It warms up and goes green"
          Action = Action.Await Signal.SessionReady
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/4 · Turn live testing on from the editor"
          // Command Palette (Ctrl+Shift+P) → "SageFs: Enable Live Testing" —
          // `ShowCommands` is the generic palette-open target every named
          // command reaches without needing its own `VsCodeCommand` case
          // (Domain.fs's closed vocabulary intentionally keeps this small).
          Action = Action.Click(Target.PaletteItem VsCodeCommand.ShowCommands)
          Expect = Expectation.PageTextContains(liveTestingPanelSelector, "Live Testing: ON")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "4/4 · SageFs runs the real tests — they pass"
          Action = Action.Await Signal.TestRunCompleted
          Expect = Expectation.PageTextContains(liveTestingPanelSelector, "✓")
          Dwell = Dwell.long } ]
    Cost = CostClass.console
    Masks = [] }

/// `repl-vscode` (§6's matrix, a hero — §3): the same "watch SageFs
/// evaluate a real project live" story as `replDashboard`, driven from the
/// editor instead of the dashboard's textarea. Typing happens at a real
/// `EditorPosition` inside the sample's own source file; evaluation is the
/// SAME chord `sagefs.eval` binds in `package.json` (`alt+enter`) — a real
/// keyboard chord, not a synthesized command, exactly matching how a human
/// actually triggers eval in this editor.
let replVsCode: Scenario =
  let file = { Sample = Sample.WebappDatastar; RelativePath = "Program.fs" }

  { Id = ScenarioId.derive Capability.Repl Client.VsCode AppKind.NoApp
    Capability = Capability.Repl
    Client = Client.VsCode
    App = AppKind.NoApp
    Sample = Sample.WebappDatastar
    Layout = LayoutTemplate.EditorFull
    Steps =
      [ { Caption = Caption.mk "1/3 · It warms up and goes green"
          Action = Action.Await Signal.SessionReady
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "2/3 · Type a plain expression into the editor"
          Action = Action.Type(Target.EditorPosition(file, 1, 0), Text.mk "List.sum [ 1 .. 10 ]", CadenceSeed.ofId "repl-vscode-eval-1")
          Expect = Expectation.EditorSaved file
          Dwell = Dwell.short }
        { Caption = Caption.mk "3/3 · Press the eval chord and watch the result"
          Action = Action.Chord [ Key.Alt; Key.Return ]
          // The daemon's session state is shared: an eval submitted from
          // VS Code lands in the SAME session the dashboard narrator pane
          // is watching (demo-actors-plan.md §2.1's `EditorFull` layout).
          Expect = Expectation.PageTextContains(outputPanelSelector, "int = 55")
          Dwell = Dwell.long } ]
    Cost = CostClass.web
    Masks = [] }

/// The three `hr-dashboard-vscode-*` hot-reload scenarios (§6's matrix,
/// two of them heroes: `-web` and `-console`) — JOINT with the App
/// co-actor (demo-actors-plan.md §2.3), which this island does not own.
/// Each edits a real, already-documented hot-reload knob in the sample's
/// own source (the roast's own citations: `starMinSpeed`/`starMaxSpeed`
/// for Raylib, "the message knob" for Console) at a real `EditorPosition`,
/// saves with the real `Ctrl+S` chord (`Key` module's own worked example:
/// "Ctrl+S; Ctrl+Shift+P"), and awaits the daemon's real
/// `AppOutputChanged` signal — the App actor's window is what visibly
/// proves it, which is why these three cannot genuinely RECORD until the
/// App island lands (§4's build order: "App — next, unblocks 9 of 18").
let private hotReloadVsCode (appKind: AppKind) (sample: Sample) (relativePath: string) (line: int) (knobText: string) (region: Region) : Scenario =
  let file = { Sample = sample; RelativePath = relativePath }

  { Id = ScenarioId.derive Capability.HotReload Client.VsCode appKind
    Capability = Capability.HotReload
    Client = Client.VsCode
    App = appKind
    Sample = sample
    Layout = LayoutTemplate.EditorLeft
    Steps =
      [ { Caption = Caption.mk "1/3 · The session and the app are both running"
          Action = Action.Await Signal.SessionReady
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "2/3 · Change a real knob in the editor"
          Action = Action.Type(Target.EditorPosition(file, line, 0), Text.mk knobText, CadenceSeed.ofId (sprintf "hr-vscode-%s-knob" relativePath))
          Expect = Expectation.EditorSaved file
          Dwell = Dwell.short }
        { Caption = Caption.mk "3/3 · Save it — the running app updates live"
          Action = Action.Chord [ Key.Ctrl; Key.S ]
          Expect = Expectation.AppOutputChanged region
          Dwell = Dwell.long } ]
    Cost = (match appKind with AppKind.Raylib -> CostClass.raylib | AppKind.Console -> CostClass.console | _ -> CostClass.web)
    Masks = [] }

/// ★ hero (§3): the same "edit → save → the live web app updates" story as
/// the dashboard/Neovim equivalents, filmed through VS Code.
let hrDashboardVsCodeWeb: Scenario =
  hotReloadVsCode AppKind.Web Sample.WebappDatastar "wwwroot/index.html" 1 "<!-- edited live by SageFs -->" Region.appHeading

let hrDashboardVsCodeRaylib: Scenario =
  hotReloadVsCode AppKind.Raylib Sample.RaylibGame "Program.fs" 1 "let starMinSpeed, starMaxSpeed = 4.0f, 9.0f" Region.appHeading

/// ★ hero (§3).
let hrDashboardVsCodeConsole: Scenario =
  hotReloadVsCode AppKind.Console Sample.ConsoleTicker "Ticker.fs" 1 "let message = \"hot-reloaded live\"" Region.appHeading

let scenarios: Scenario list =
  [ ltVsCode; replVsCode; hrDashboardVsCodeWeb; hrDashboardVsCodeRaylib; hrDashboardVsCodeConsole ]
