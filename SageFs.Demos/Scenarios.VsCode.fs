/// The VS Code client's scenarios (demo-actors-plan.md §1.3/§2.1: `lt-
/// vscode`, `repl-vscode`, `hr-dashboard-vscode-{web,raylib,console}`).
/// `Scenarios.All.fs` already references `scenarios` below (Island F left
/// it an empty list) — this island fills it in.
///
/// INTEGRATION STATUS (read before wiring `record` against these):
/// `Runtime.fs`'s `vsCodeTargetSelector` now flattens `Target.
/// EditorPosition`/`WindowCenter ActorId.VsCode` for real (routed through
/// the extension host's `sagefs.debug.rectFor` — `Actors/VsCode.fs`), so
/// every `Type`/`Chord` step below drives a genuine click/type. The ONE
/// remaining gap is `Target.PaletteItem`: no `executeCommand` channel exists
/// yet (`Actors/VsCode.fs` has none), so that one click never lands — noted
/// inline on `ltVsCode`'s own step, never faked. `Expect` was always fine:
/// every step observes through the SAME shared dashboard narrator selectors
/// `Scenarios.fs`'s own dashboard scenarios use (`EditorFull`'s layout
/// places a Dashboard pane specifically as a narrator alongside the editor,
/// demo-actors-plan.md §2.1) — the daemon's session state is shared across
/// every client, so an eval/toggle driven from VS Code shows up in the SAME
/// dashboard view a Dashboard-client scenario would read.
module SageFs.Demos.Scenarios.VsCode

open SageFs.Demos.Domain

/// The daemon's own session-status badge, scoped to the ACTUAL element
/// rather than the whole `body` (`Scenarios.fs`'s own dashboard scenarios
/// use `body:has-text("Ready")`, justified there for a DIFFERENT reason —
/// that file's own note). An unscoped `body`-wide check false-positives for
/// an editor client the instant the dashboard's SSE connection comes up:
/// the cmdline area renders the idle label `"SageFs -- ready"`
/// (`DashboardTypes.fs`'s `cmdlineLabel`) whenever `ConnectionState =
/// Connected`, regardless of whether the SESSION itself has reached Ready —
/// `body:has-text("Ready")` (case-insensitive substring) matches that text
/// immediately, long before warmup finishes, which is exactly the false
/// positive this scenario was flagged for. `#session-status .status` scopes
/// to the ONE element whose text is genuinely `SessionState.label` — a real,
/// snapshot-verified selector (`SageFs.Tests/snapshots/
/// DashboardSnapshotTests.dashboard_sessionStatus_ready.verified.html`:
/// `<div id="session-status"><span class="status status-ready">Ready</span>
/// ...`), never a guess.
let private sessionStatusSelector = "#session-status .status"

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
      [ // The extension's own auto-discover-and-create flow is interactive
        // (`Window.showInformationMessage` "Create Session?" confirm
        // dialog, `Extension.fs`) — synthetic input has no reliable way to
        // answer it, so this creates the session directly through the cell
        // daemon's own API instead, before that flow's 2-second delay even
        // fires (`Actors/VsCode.fs`'s `command` doc).
        { Caption = Caption.mk "1/5 · Create a session for the real test project"
          Action = Action.Setup(ClientCommand.CreateSession Sample.FromCSharp)
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/5 · A real test project is open in the editor"
          Action = Action.Setup(ClientCommand.OpenFile { Sample = Sample.FromCSharp; RelativePath = "Hello.fs" })
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned")
          Dwell = Dwell.short }
        { Caption = Caption.mk "3/5 · It warms up and goes green"
          Action = Action.Await Signal.SessionReady
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "4/5 · Turn live testing on from the editor"
          // Command Palette (Ctrl+Shift+P) → "SageFs: Enable Live Testing" —
          // `ShowCommands` is the generic palette-open target every named
          // command reaches without needing its own `VsCodeCommand` case
          // (Domain.fs's closed vocabulary intentionally keeps this small).
          // NOTE (honest gap, not chased here): `Runtime.fs`'s
          // `vsCodeTargetSelector` has no case for `Target.PaletteItem` yet
          // (this file's own top-of-file INTEGRATION NOTE) — a real palette
          // command execution needs a `executeCommand` channel
          // `Actors/VsCode.fs` does not implement today, so this click does
          // not yet land. Left wired and honest, never faked with a click
          // that doesn't happen.
          Action = Action.Click(Target.PaletteItem VsCodeCommand.ShowCommands)
          Expect = Expectation.PageTextContains(liveTestingPanelSelector, "Live Testing: ON")
          Dwell = Dwell.medium }
        // NOT YET PASSING (a second, independent blocker on top of the note
        // above): identical to `lt-dashboard`/`lt-neovim`'s own documented
        // blocker — `SageFs.Core`'s live-testing discovery-completion event
        // never fires for a project opened this way, out of a demo island's
        // scope per `AGENTS.md`. Left wired, never faked.
        { Caption = Caption.mk "5/5 · SageFs runs the real tests — they pass"
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
      [ // Same non-interactive create as `ltVsCode`'s own step 1 — see
        // `Actors/VsCode.fs`'s `command` doc for why this bypasses the
        // extension's own interactive confirm-dialog flow entirely.
        { Caption = Caption.mk "1/4 · Create a session for the real project"
          Action = Action.Setup(ClientCommand.CreateSession Sample.WebappDatastar)
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/4 · It warms up and goes green"
          Action = Action.Await Signal.SessionReady
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/4 · Type a plain expression into the editor"
          Action = Action.Type(Target.EditorPosition(file, 1, 0), Text.mk "List.sum [ 1 .. 10 ]", CadenceSeed.ofId "repl-vscode-eval-1")
          Expect = Expectation.EditorSaved file
          Dwell = Dwell.short }
        { Caption = Caption.mk "4/4 · Press the eval chord and watch the result"
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
/// co-actor, now genuinely wired (`Actors/App.fs`/`Runtime.App.fs`,
/// `CellAgent.fs`'s lazy launch right after a real `"run-app"` dispatch).
/// Each creates a real session, runs the app, edits a real,
/// already-documented hot-reload knob in the sample's own source (the
/// roast's own citations: `starMinSpeed`/`starMaxSpeed` for Raylib, "the
/// message knob" for Console) at a real `EditorPosition`, saves with the
/// real `Ctrl+S` chord (`Key` module's own worked example: "Ctrl+S;
/// Ctrl+Shift+P"), and awaits the daemon's real `AppOutputChanged` signal
/// through the App actor's own real window.
let private hotReloadVsCode (appKind: AppKind) (sample: Sample) (relativePath: string) (line: int) (knobText: string) (region: Region) : Scenario =
  let file = { Sample = sample; RelativePath = relativePath }

  { Id = ScenarioId.derive Capability.HotReload Client.VsCode appKind
    Capability = Capability.HotReload
    Client = Client.VsCode
    App = appKind
    Sample = sample
    Layout = LayoutTemplate.EditorLeft
    Steps =
      [ // Same non-interactive create every VS Code scenario now uses — see
        // `Actors/VsCode.fs`'s `command` doc.
        { Caption = Caption.mk "1/5 · Create a session for the real project"
          Action = Action.Setup(ClientCommand.CreateSession sample)
          Expect = Expectation.PageTextContains(outputPanelSelector, "Scanned")
          Dwell = Dwell.short }
        { Caption = Caption.mk "2/5 · The session warms up and goes green"
          Action = Action.Await Signal.SessionReady
          Expect = Expectation.PageTextContains(sessionStatusSelector, "Ready")
          Dwell = Dwell.medium }
        // Without this step the App co-actor never launches (`CellAgent.fs`'s
        // lazy launch fires only right after a real "run-app" dispatch) —
        // the original 3-step shape here was missing it entirely, which
        // would have left step 5's `AppOutputChanged` observing nothing.
        { Caption = Caption.mk "3/5 · Run the app"
          Action = Action.Setup ClientCommand.RunApp
          Expect = Expectation.AppState AppRunStateCase.Running
          Dwell = Dwell.medium }
        { Caption = Caption.mk "4/5 · Change a real knob in the editor"
          Action = Action.Type(Target.EditorPosition(file, line, 0), Text.mk knobText, CadenceSeed.ofId (sprintf "hr-vscode-%s-knob" relativePath))
          Expect = Expectation.EditorSaved file
          Dwell = Dwell.short }
        { Caption = Caption.mk "5/5 · Save it — the running app updates live"
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
