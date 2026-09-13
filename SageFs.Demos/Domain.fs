/// The pure domain model for `sagefs-demos` (demo-gif-plan.md §5, §6.1).
///
/// Everything here is data: no IO, no process/socket/file access. The only
/// impure edge in the whole tool is `DemoRuntime` at the bottom of this file
/// (a record of functions injected at the edge, mirroring the
/// `SessionManagerRuntime` pattern already used by the daemon). Every closed
/// vocabulary is a discriminated union with `[<RequireQualifiedAccess>]`
/// (matching §3's "no magic strings" doctrine); a `bool` is never used to
/// carry domain state (see `Pressed` below instead of a raw flag).
///
/// This is the Wave-1 skeleton: types compile and the §6.1 worked example
/// constructs correctly, but every *planner* that turns these types into a
/// plan is a `failwith "TODO: ... — Wave 2"` stub in its own file.
module SageFs.Demos.Domain

// ---------------------------------------------------------------------------
// Geometry, seeds and timing primitives — no dependencies on anything below.
// ---------------------------------------------------------------------------

/// A point on the virtual screen, in pixels.
type Point = { X: int; Y: int }

/// An axis-aligned rectangle on the virtual screen, in pixels.
type Rect = { X: int; Y: int; W: int; H: int }

/// A `Rect` that came from resolving a `Target` through a live actor (§3:
/// "a `ResolvedTarget` is a screen rect that came from a live actor, never a
/// guess"). Kept as a distinct name — not a fresh `Rect` construction — so a
/// call site cannot silently substitute a made-up rectangle for one an actor
/// actually measured.
/// TODO(shape): currently a type abbreviation; promote to a single-case
/// wrapper if Wave 2 needs the compiler to reject an unresolved `Rect` at a
/// `ScreenRect`-typed call site.
type ScreenRect = Rect

/// The virtual screen's resolution (§4.5: `Xvfb :99 -screen 0 1280x720x24`).
type Screen = { Width: int; Height: int }

/// A target position expressed as a fraction (0.0–1.0) of an actor's own
/// window, used for `Target.AppWindowPoint` where no semantic DOM/API target
/// exists (e.g. a point inside the Raylib window).
type RelativePoint = { Fx: float; Fy: float }

/// A deterministic seed driving `Motion`/`Cadence`, always derived from a
/// scenario id (§9 "Seeds") so a run is identical every time and different
/// per scenario.
type Seed = Seed of uint64

/// A delay between two input events, in milliseconds.
type Delay = Delay of int

/// A native X11 keycode (8–255), resolved from a `Key` via a live server's
/// `KeyboardMapping` (§4.3) — never hard-coded, since keymaps are per-layout.
type KeyCode = KeyCode of int

/// A mouse button, delivered via XTEST (§4.3).
[<RequireQualifiedAccess>]
type Button =
  | Left
  | Middle
  | Right

/// Whether a button or key is going down or coming back up. A DU instead of
/// a raw `bool` per doctrine: `true`/`false` at a call site reads as noise,
/// `Pressed.Down`/`Pressed.Up` reads as what actually happened on the wire.
[<RequireQualifiedAccess>]
type Pressed =
  | Down
  | Up

/// A git-blob-style content hash, used by the fingerprint machinery (§4.10).
type Digest = Digest of string

/// The Xvfb display a cell's apps draw onto (e.g. ":99"). Every cell gets a
/// private `/tmp`, so every cell can reuse the same display name (§4.1)
/// without colliding.
type Display = Display of string

// ---------------------------------------------------------------------------
// The closed vocabularies from §5.
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Client =
  | Dashboard
  | VsCode
  | Neovim
  /// The Agent/MCP actor's own on-screen viz page (demo-actors-plan.md
  /// §2.4) — added here (the plan's own §0 overview text only lists three,
  /// written before this island's analysis) because `Scenario.Client` is a
  /// required field and `agent-mcp` is not filmed through any editor: it has
  /// no ClickThenTypeThenClick, no DOM, only a real MCP transcript. Additive
  /// only — no existing case changed.
  | Agent

module Client =
  /// Every editor client a HOT-RELOAD scenario can be filmed through (§6's
  /// "3 clients × 3 runnable samples = 9 hot-reload scenarios" comprehension
  /// — `DomainTests.fs` pins this exact count). Deliberately excludes
  /// `Client.Agent`: the Agent/MCP actor authors no hot-reload scenarios at
  /// all (demo-actors-plan.md §2.4 lists exactly one scenario, `agent-mcp`,
  /// outside the hot-reload matrix entirely) — including it here would
  /// silently inflate that matrix to 12 pairs with 3 non-existent
  /// "hr-*-agent-*" scenarios this actor never builds.
  let all : Client list = [ Client.Dashboard; Client.VsCode; Client.Neovim ]

[<RequireQualifiedAccess>]
type AppKind =
  | Web
  | Raylib
  | Console
  | NoApp

[<RequireQualifiedAccess>]
type Capability =
  | HotReload
  | LiveTesting
  | Repl
  | Sessions
  | Agent

[<RequireQualifiedAccess>]
type Sample =
  | WebappDatastar
  | RaylibGame
  | ConsoleTicker
  | FromCSharp

module Sample =
  /// Samples a `HotReload` scenario can actually edit-and-rerun live (§6:
  /// "9 hot-reload" = 3 clients × these 3 samples). `FromCSharp` is used only
  /// by the live-testing scenarios, fixed per client, so it is excluded here.
  let runnable : Sample list = [ Sample.WebappDatastar; Sample.RaylibGame; Sample.ConsoleTicker ]

  /// The sample's real project directory, relative to the repo root — a
  /// scenario that wants to show SageFs opening a REAL project (§10: not a
  /// bare Quick Start temp session, which has nothing to auto-open and
  /// nothing to eval against the project's own code) resolves this and
  /// hands it to `Runtime.fs`, the one place with a `repoRoot` to make it
  /// absolute. Exhaustive over every `Sample` case so a new sample can never
  /// silently have no real directory to point a session at.
  let relativePath (sample: Sample) : string =
    match sample with
    | Sample.WebappDatastar -> "samples/demos/SageFs.Samples.WebappDatastar"
    | Sample.RaylibGame -> "samples/demos/SageFs.Samples.RaylibGame"
    | Sample.ConsoleTicker -> "samples/demos/SageFs.Samples.ConsoleTicker"
    | Sample.FromCSharp -> "samples/from-csharp/SageFs.Samples.FromCSharp"

[<RequireQualifiedAccess>]
type LayoutTemplate =
  | EditorLeft
  | EditorFull
  | DashboardOnly

/// An actor is one thing the runner controls inside a cell: an editor, the
/// dashboard, the app under test, or a scripted agent. Distinct from `Client`
/// because a scenario always has exactly one `Client` (the editor "on
/// camera") but a `Layout`/`ScenarioPlan` places *every* actor in the cell.
[<RequireQualifiedAccess>]
type ActorId =
  | Dashboard
  | VsCode
  | Neovim
  | App
  | Agent

/// A scenario's stable, derived identity — never typed by hand (§5, §6.1).
/// Private so the only way to get one is `ScenarioId.derive`.
type ScenarioId = private ScenarioId of string

module ScenarioId =

  let private capabilityToken (capability: Capability) =
    match capability with
    | Capability.HotReload -> "hr"
    | Capability.LiveTesting -> "lt"
    | Capability.Repl -> "repl"
    | Capability.Sessions -> "sessions"
    | Capability.Agent -> "agent"

  let private clientToken (client: Client) =
    match client with
    | Client.Dashboard -> "dashboard"
    | Client.VsCode -> "vscode"
    | Client.Neovim -> "neovim"
    | Client.Agent -> "agent"

  let private appToken (appKind: AppKind) =
    match appKind with
    | AppKind.Web -> Some "web"
    | AppKind.Raylib -> Some "raylib"
    | AppKind.Console -> Some "console"
    | AppKind.NoApp -> None

  /// Derives the kebab-case scenario id from the three facets that define a
  /// scenario. §5 gives only the function's signature and §6.1/§6 give six
  /// worked outputs, not the algorithm — this is the shape inferred to
  /// reproduce all six exactly:
  ///   "hr-dashboard-vscode-web"    (HotReload, VsCode,  Web)
  ///   "hr-neovim-neovim-raylib"    (HotReload, Neovim,  Raylib)
  ///   "hr-dashboard-vscode-console"(HotReload, VsCode,  Console)
  ///   "lt-vscode"                  (LiveTesting, VsCode, NoApp)
  ///   "repl-neovim"                (Repl, Neovim, NoApp)
  ///   "sessions-dashboard"         (Sessions, Dashboard, NoApp)
  /// TODO(shape): a `HotReload` scenario names both a "narrator" pane (the
  /// SageFs dashboard, unless the client itself IS Neovim — its own
  /// statusline narrates instead) and the app kind, because those demos show
  /// a separate app pane; every other capability has no app pane
  /// (`AppKind.NoApp`) and no narrator, so its id is just "<cap>-<client>".
  let derive (capability: Capability) (client: Client) (appKind: AppKind) : ScenarioId =
    let cap = capabilityToken capability
    let clientTok = clientToken client
    let tokens =
      match capability with
      | Capability.HotReload ->
        let narrator = match client with Client.Neovim -> "neovim" | _ -> "dashboard"
        [ narrator; clientTok ] @ (appToken appKind |> Option.toList)
      | Capability.LiveTesting
      | Capability.Repl
      | Capability.Sessions
      | Capability.Agent -> [ clientTok ]
    cap :: tokens |> String.concat "-" |> ScenarioId

  /// An explicit escape hatch for scenario ids that do NOT compose from
  /// `Capability × Client × AppKind` — today only the Phase-0/1 throwaway
  /// smoke scenario (`hello-dashboard`, demo-gif-plan.md §10 Phase 1), which
  /// predates the matrix and is never one of the `matrix` values in §6. Every
  /// matrix-composed scenario must go through `derive`, never this.
  let ofRaw (text: string) : ScenarioId = ScenarioId text

  let value (ScenarioId s) : string = s

// ---------------------------------------------------------------------------
// Leaf "named value" types — closed or validated data referenced by the
// worked example in §6.1 via a lowerCamelCase companion-module member
// (`Dwell.short`, `Region.clock`, `SampleFile.homePage`, `DashboardId.runApp`,
// `CostClass.web`, `Signal.appOutputChanged`), the way the plan's own code
// reads. Each type keeps its DU cases (or validated constructor) PascalCase;
// the module supplies the readable, doc-literal alias.
// ---------------------------------------------------------------------------

/// A caption band's text (§9: "≤ 70 characters, enforced by the `Caption`
/// smart constructor"). Truncating rather than failing: the smart constructor
/// must be total (§6.1 assigns `Caption.mk "..."` directly into a `Step`
/// field with no error handling), so an over-long caption is fixed up rather
/// than a hole in the domain model.
type Caption = private Caption of string

module Caption =
  [<Literal>]
  let MaxLength = 70

  let mk (text: string) : Caption =
    if text.Length <= MaxLength then Caption text
    else Caption (text.Substring(0, MaxLength))

  let value (Caption s) : string = s

/// Text typed into an editor during a `Type`/`Typo` action.
type Text = private Text of string

module Text =
  let mk (text: string) : Text = Text text
  let value (Text s) : string = s

  /// A scenario built as a pure, static `Scenario` value has no `repoRoot`
  /// to make an absolute sample-project path with (only `Runtime.fs`, which
  /// builds the `Wire.ScenarioPlan`, knows that) — a scenario that needs to
  /// type one (§10: driving the dashboard's real "open a project" flow
  /// against a sample under `samples/`, instead of the Quick Start temp
  /// session that has nothing to auto-open) embeds this token in the typed
  /// `Text` instead of a literal path; `Runtime.fs`'s `wireStepOf` is the
  /// one place with a `repoRoot` to substitute it before the text ever
  /// reaches a real keystroke, so nothing downstream ever sees or types the
  /// literal token itself.
  [<Literal>]
  let RepoRootToken = "{{REPO_ROOT}}"

/// The seed driving `Cadence.keys` for one `Type` action, derived from the
/// scenario id so typing cadence is identical every run and different per
/// scenario (§9 "Seeds").
type CadenceSeed = private CadenceSeed of string

module CadenceSeed =
  let ofId (scenarioIdText: string) : CadenceSeed = CadenceSeed scenarioIdText
  let value (CadenceSeed s) : string = s

/// How long to hold on a step after its expectation is observed (§9: "≥ 1.0 s
/// dwell after every observed change").
[<RequireQualifiedAccess>]
type Dwell =
  | Short
  | Medium
  | Long

module Dwell =
  let short = Dwell.Short
  let medium = Dwell.Medium
  let long = Dwell.Long

  /// TODO(shape): §9 pins only the *floor* (≥ 1.0 s) and the first/last-frame
  /// holds (1.2 s / 2.0 s); these per-case defaults are a reasonable minimal
  /// reading of "short/medium/long", not values pinned anywhere in the plan.
  let ms (dwell: Dwell) : int =
    match dwell with
    | Dwell.Short -> 1000
    | Dwell.Medium -> 1500
    | Dwell.Long -> 2000

/// A named rectangular mask, used both to ignore dynamic regions when diffing
/// golden stills (§4.7 `Masks`) and as an `Expectation.AppOutputChanged`
/// target (the region a step expects to visibly change).
type Region = { X: int; Y: int; W: int; H: int; Label: string }

module Region =
  /// TODO(shape): the plan names these regions ("ignore the app's clock",
  /// "the app's own output changing") but not their pixel rects — actual
  /// coordinates are an actor/layout concern for Wave 2 (`Layout.rects`).
  let clock = { X = 0; Y = 0; W = 0; H = 0; Label = "clock" }
  let appHeading = { X = 0; Y = 0; W = 0; H = 0; Label = "app-heading" }

/// A file inside a `Sample` project that a scenario opens/edits.
type SampleFile = { Sample: Sample; RelativePath: string }

module SampleFile =
  /// TODO(shape): the actual relative path inside
  /// `SageFs.Samples.WebappDatastar` is a Wave-2/G-gap concern; this names
  /// the file the worked example (§6.1) opens and edits.
  let homePage = { Sample = Sample.WebappDatastar; RelativePath = "wwwroot/index.html" }

/// The measured (never guessed, §4.2) resource cost of recording one
/// scenario, used by `Schedule.plan` to pack cells without exceeding the
/// core/memory budget.
type CostClass = { Cpu: int; MemoryGb: float; DurationSeconds: float }

module CostClass =
  /// TODO(shape): placeholders until `sagefs-demos measure` (§4.2) fills
  /// `costclass.json` — the plan is explicit these numbers are calibrated,
  /// not guessed, so these are deliberately round starting points.
  let web = { Cpu = 2; MemoryGb = 2.5; DurationSeconds = 20.0 }
  let raylib = { Cpu = 2; MemoryGb = 1.5; DurationSeconds = 20.0 }
  let console = { Cpu = 1; MemoryGb = 1.0; DurationSeconds = 15.0 }

/// Every dashboard element a demo can click, one DU mirroring the
/// `data-testid`s the dashboard renders (§4.3 Targets, gap G4) — contract-
/// tested against `DashboardFragments.fs` in Wave 2 so a renamed button
/// breaks a test, not a demo.
[<RequireQualifiedAccess>]
type DashboardId =
  | RunApp
  | StopApp
  | SessionCard
  | QuickStart
  | Eval
  | Reset
  | HardReset
  | Clear
  | WatchAll
  | UnwatchAll
  /// The live-testing panel's ON/OFF toggle button — a real `data-testid`
  /// already in the dashboard's markup (`DashboardFragments.fs`'s
  /// `renderLiveTestingPanel`, `testid "live-testing-toggle"`), not one added
  /// for this tool.
  | LiveTestingToggle

module DashboardId =
  let runApp = DashboardId.RunApp
  let stopApp = DashboardId.StopApp
  let sessionCard = DashboardId.SessionCard
  let quickStart = DashboardId.QuickStart
  let eval = DashboardId.Eval
  let reset = DashboardId.Reset
  let hardReset = DashboardId.HardReset
  let clear = DashboardId.Clear
  let watchAll = DashboardId.WatchAll
  let unwatchAll = DashboardId.UnwatchAll
  let liveTestingToggle = DashboardId.LiveTestingToggle

  /// The exhaustive DU → `data-testid` mapping (§4.3, G4) — the one place a
  /// dashboard button name and its DOM contract can drift apart.
  let testId (id: DashboardId) : string =
    match id with
    | DashboardId.RunApp -> "run-app"
    | DashboardId.StopApp -> "stop-app"
    | DashboardId.SessionCard -> "session-card"
    | DashboardId.QuickStart -> "quick-start"
    | DashboardId.Eval -> "eval"
    | DashboardId.Reset -> "reset"
    | DashboardId.HardReset -> "hard-reset"
    | DashboardId.Clear -> "clear"
    | DashboardId.WatchAll -> "watch-all"
    | DashboardId.UnwatchAll -> "unwatch-all"
    | DashboardId.LiveTestingToggle -> "live-testing-toggle"

/// A VS Code command id a demo invokes via `executeCommand` (§4.4) — never a
/// bare string at a call site.
[<RequireQualifiedAccess>]
type VsCodeCommand =
  | SageFsRunApp
  | SageFsStopApp
  | SageFsDebugRectFor
  | QuickOpen
  | ShowCommands

module VsCodeCommand =
  let commandId (cmd: VsCodeCommand) : string =
    match cmd with
    | VsCodeCommand.SageFsRunApp -> "sagefs.runApp"
    | VsCodeCommand.SageFsStopApp -> "sagefs.stopApp"
    | VsCodeCommand.SageFsDebugRectFor -> "sagefs.debug.rectFor"
    | VsCodeCommand.QuickOpen -> "workbench.action.quickOpen"
    | VsCodeCommand.ShowCommands -> "workbench.action.showCommands"

/// A live-testing test's identity (§5 `Expectation.TestOutcome`).
type TestId = TestId of string

/// The tri-state result of a live-tested test (mirrors the daemon's own
/// outcome vocabulary, kept local so this tool never references
/// SageFs.Core).
[<RequireQualifiedAccess>]
type Outcome =
  | Passed
  | Failed
  | Skipped

/// The daemon-observed app run state a step can assert on (mirrors G1's
/// `AppStateView`: "▶ Running · ⏳ Starting · ⚠ CouldNotStart: reason ·
/// ↻ RestartRequired").
[<RequireQualifiedAccess>]
type AppRunStateCase =
  | Starting
  | Running
  | CouldNotStart
  | RestartRequired
  | Stopped

/// Something a step can wait for without an explicit click (`Action.Await`).
[<RequireQualifiedAccess>]
type Signal =
  | AppOutputChanged
  | HotReloadApplied
  | TestRunCompleted
  /// The daemon's own session-status label (§9 fix) has actually reached
  /// "Ready" — not just "the session card appeared", which fires while the
  /// session is still `WarmingUp` and stops the recording before anything
  /// interesting happens.
  | SessionReady

module Signal =
  let appOutputChanged = Signal.AppOutputChanged
  let hotReloadApplied = Signal.HotReloadApplied
  let testRunCompleted = Signal.TestRunCompleted
  let sessionReady = Signal.SessionReady

/// An API-level setup action (`Action.Setup`) — not filmed, used to get a
/// scenario into its starting state (e.g. opening a file before the story
/// begins).
[<RequireQualifiedAccess>]
type ClientCommand =
  | OpenFile of SampleFile
  | RunApp
  | StopApp
  | SaveAll

// ---------------------------------------------------------------------------
// Targets, actions, expectations, steps, scenarios (§5, §6.1).
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Target =
  | DashboardElement of DashboardId
  | EditorPosition of file: SampleFile * line: int * column: int
  | PaletteItem of VsCodeCommand
  | NvimCommandLine
  | AppWindowPoint of RelativePoint
  | WindowCenter of ActorId
  /// An explicit escape hatch for a dashboard element that is not in the
  /// `DashboardId` vocabulary because it carries no `data-testid` (e.g. the
  /// eval code textarea, which has only a plain `id`) — a raw CSS/Playwright
  /// selector, used ONLY when adding a real `data-testid` to the dashboard
  /// is not warranted for one demo-only target. Every other target stays a
  /// closed, typed vocabulary; this is the deliberate, documented exception.
  | DashboardCssSelector of selector: string

/// A key on the chord/shortcut vocabulary (§4.3 `Keymap`). `Char` covers
/// arbitrary typed text (fed by `Cadence.keys`); the named letters below are
/// convenience aliases for the shortcut letters scenarios reference directly
/// (`Action.Chord [ Key.Ctrl; Key.S ]`, §6.1) so a chord reads as named keys
/// rather than `Key.Char 'S'`.
[<RequireQualifiedAccess>]
type Key =
  | Ctrl
  | Shift
  | Alt
  | Return
  | Escape
  | Tab
  | Left
  | Right
  | Up
  | Down
  | F of int
  | Char of char
  // §4.3: backspace-and-retype of one character is the mechanism behind an
  // explicit `Typo` step; the Wave-1 `Key` DU had no way to express it.
  | Backspace

module Key =
  // TODO(shape): the plan is silent on whether shortcut letters get their
  // own cases; these aliases keep `Key.Char` total for arbitrary typed text
  // while giving the letters used in chords (§6.1: Ctrl+S; §4.3: Ctrl+Shift+P)
  // a readable name at the call site.
  let S = Key.Char 'S'
  let P = Key.Char 'P'

  /// The wire token for a `Key` (seam-integration threading, demo-actors-
  /// plan.md §3: `Action.Chord` needs to cross the `Wire.WireStep` boundary
  /// the same way every other `Action` case already does). Exhaustive and
  /// round-trippable through `ofToken` — a closed vocabulary, never a raw
  /// stringly-typed literal at a call site.
  let toToken (key: Key) : string =
    match key with
    | Key.Ctrl -> "Ctrl"
    | Key.Shift -> "Shift"
    | Key.Alt -> "Alt"
    | Key.Return -> "Return"
    | Key.Escape -> "Escape"
    | Key.Tab -> "Tab"
    | Key.Left -> "Left"
    | Key.Right -> "Right"
    | Key.Up -> "Up"
    | Key.Down -> "Down"
    | Key.Backspace -> "Backspace"
    | Key.F n -> sprintf "F%d" n
    | Key.Char c -> sprintf "Char:%c" c

  /// The inverse of `toToken` — `None` for anything not actually produced by
  /// `toToken`, never a guessed key (mirrors `Keymap.resolve`'s own "drop
  /// rather than silently produce the wrong thing" doctrine).
  let ofToken (token: string) : Key option =
    match token with
    | "Ctrl" -> Some Key.Ctrl
    | "Shift" -> Some Key.Shift
    | "Alt" -> Some Key.Alt
    | "Return" -> Some Key.Return
    | "Escape" -> Some Key.Escape
    | "Tab" -> Some Key.Tab
    | "Left" -> Some Key.Left
    | "Right" -> Some Key.Right
    | "Up" -> Some Key.Up
    | "Down" -> Some Key.Down
    | "Backspace" -> Some Key.Backspace
    | t when t.Length > 1 && t.[0] = 'F' && t.Substring(1) |> Seq.forall System.Char.IsDigit ->
      Some(Key.F(int (t.Substring 1)))
    | t when t.StartsWith "Char:" && t.Length = 6 -> Some(Key.Char t.[5])
    | _ -> None

[<RequireQualifiedAccess>]
type Action =
  | Click of Target
  | Type of Target * Text * CadenceSeed
  | Chord of Key list
  | Typo of Target * wrong: Text * right: Text
  | Setup of ClientCommand
  | Await of Signal
  /// Type `text` at `typeTarget` (clicking it first, exactly like `Type`),
  /// THEN click `submitTarget` — e.g. type an F# expression into the eval
  /// box, then click the `[EVAL]` button — as one filmed step, with the
  /// cursor moving continuously from the type target to the submit target
  /// rather than resetting. Distinct from two separate `Step`s so the
  /// caption ("3/3 · Evaluate F#") narrates it as the one beat it visually
  /// is; §9's "watch SageFs evaluate F# live" demo needs exactly this.
  | TypeThenClick of typeTarget: Target * text: Text * seed: CadenceSeed * submitTarget: Target
  /// Click `preClickTarget` (e.g. expand a collapsed panel), THEN type
  /// `text` at `typeTarget`, THEN click `submitTarget` — all as one
  /// uninterrupted step. Distinct from chaining a separate pre-click step
  /// before a `TypeThenClick` one: measured directly against real
  /// recordings, a dashboard whose collapsed-panel state does not survive
  /// its own server-driven re-render (a genuine upstream defect, out of
  /// scope here) can re-collapse the panel in the gap BETWEEN two steps —
  /// even a short one — so the expand click and the typing that depends on
  /// it must land inside the SAME step, with no step boundary between them.
  | ClickThenTypeThenClick of preClickTarget: Target * typeTarget: Target * text: Text * seed: CadenceSeed * submitTarget: Target

[<RequireQualifiedAccess>]
type Expectation =
  | PageShows of DashboardId * Text
  | AppState of AppRunStateCase
  | EditorSaved of SampleFile
  | TestOutcome of TestId * Outcome
  | NvimBufferContains of Text
  | AppOutputChanged of Region
  /// A raw selector's text content contains `text` — the general-purpose
  /// observation `PageShows` cannot express (it only checks a `DashboardId`
  /// selector's PRESENCE, never its text — §9's "session reached Ready", not
  /// just "the card exists", and "the eval result value appeared" both need
  /// a text-content check on an arbitrary selector, not just a data-testid
  /// existence check).
  | PageTextContains of selector: string * text: string

type Step =
  { Caption: Caption
    Action: Action
    Expect: Expectation
    Dwell: Dwell }

type Scenario =
  { Id: ScenarioId
    Capability: Capability
    Client: Client
    App: AppKind
    Sample: Sample
    Layout: LayoutTemplate
    Steps: Step list
    Cost: CostClass
    Masks: Region list }

// ---------------------------------------------------------------------------
// Isolation, resources, scheduling (§4.1, §4.2).
// ---------------------------------------------------------------------------

/// A bwrap sandbox description — the paths bound into a cell (§4.1's fixed
/// filesystem layout).
/// TODO(shape): minimal placeholder; Wave 2 (Phase 0/4) fills in the real
/// bind-mount table.
type Sandbox = { RootDir: string; ToolchainDir: string }

type UserName = UserName of string

/// The `env -i` allowlist for the last-resort `ScratchEnv` isolation
/// strategy (§4.1).
type Allowlist = { EnvVars: string list }

/// The isolation strategy the doctor resolved for this run, strongest first
/// (§4.1). Chosen once by the doctor, never assumed by a cell.
[<RequireQualifiedAccess>]
type Isolation =
  | Bubblewrap of Sandbox
  | TempUser of UserName
  | ScratchEnv of Allowlist

/// Whether a cell's Raylib/Chromium rendering is software or hardware GL
/// (§4.2, §5's `Resources.Gl`).
[<RequireQualifiedAccess>]
type Gl =
  | SoftwareGl
  | Hardware

/// The machine's measured resources (§4.2), used by `Schedule.plan`.
type Resources = { Cores: int; MemoryGb: float; Gl: Gl }

/// One scenario's whole world: sandbox, display, and (in Wave 2) the daemon
/// and actors running inside it (§4.1). A `Cell` cannot exist without its
/// `Display` — the illegal state §5 calls out ("a `Cell` cannot exist without
/// its `Display`") is unrepresentable because the field is required, not
/// optional.
/// TODO(shape): minimal placeholder — the daemon/actor handles a live cell
/// owns are a Wave-2/cell-agent concern (§4.1).
type Cell =
  { Id: ScenarioId
    Isolation: Isolation
    Display: Display }

/// A batch of cells the scheduler runs together without exceeding the
/// resource budget (§4.2).
type Wave = Cell list

// ---------------------------------------------------------------------------
// Input engine (§4.3).
// ---------------------------------------------------------------------------

/// The live X11 server's keysym → keycode table, fetched once per cell
/// (`XTest.keyboardMapping`, via `XGetKeyboardMapping`) so `Keymap.resolve`
/// never hard-codes a layout. `ShiftedKeysyms` names every keysym in
/// `KeysymToKeycode` that is reachable ONLY by holding Shift while pressing
/// its keycode — an uppercase letter's keysym shares a keycode with its
/// lowercase sibling (`A`/`a` both live on one physical key), and a shifted
/// punctuation symbol shares a keycode with its unshifted sibling (`|`/`\`,
/// `>`/`.`) — exactly how a real X11 keyboard mapping is shaped: one keysym
/// per shift level per keycode, never one keycode per printable character.
/// A prior placeholder (`identity mapping: keysym == keycode`, never
/// replaced with a real live fetch) sent raw ASCII codepoints as literal
/// X11 keycodes to a real display — every punctuation/digit character
/// landed on whatever unrelated physical key happened to share that keycode
/// number, corrupting typed text end to end (confirmed against a real
/// recording: `[1..10] |> List.sum` rendered as `` `ll`'ool ``). This type
/// exists so that corruption is structurally impossible once the mapping is
/// actually fetched live: `Keymap.resolve` decides "does this need Shift"
/// from `ShiftedKeysyms`, never from `Char.IsUpper` alone (which cannot see
/// shifted punctuation at all).
type KeyboardMapping =
  { KeysymToKeycode: Map<int, int>
    ShiftedKeysyms: Set<int> }

/// One XTEST wire event, delivered by the bundled `libXtst` P/Invoke edge —
/// never a hand-rolled X11 wire codec (§4.3).
[<RequireQualifiedAccess>]
type X11Request =
  | FakeMotion of x: int * y: int
  | FakeButton of button: Button * pressed: Pressed
  | FakeKey of keyCode: KeyCode * pressed: Pressed

/// One frame of the synthetic cursor's path, used both to deliver
/// `FakeMotion` events and to draw the visible cursor overlay in the
/// composer (§4.3, §4.6).
type PointerFrame = { At: Point; TMs: int }

// ---------------------------------------------------------------------------
// Style, composition, ffmpeg (§4.6, §9).
// ---------------------------------------------------------------------------

/// The one place colours live (§9) — every pane in every GIF reads this
/// record, never a literal hex string at a call site.
type Style =
  { Ink: string
    Ground: string
    Panel: string
    Rule: string
    Accent: string
    Good: string
    Warn: string
    Bad: string }

module Style =
  /// The Kanagawa Wave palette already used by `SageFs/dashboard.css` (§9).
  let kanagawa =
    { Ink = "#dcd7ba"
      Ground = "#1f1f28"
      Panel = "#2a2a37"
      Rule = "#54546d"
      Accent = "#7e9cd8"
      Good = "#98bb6c"
      Warn = "#e6c384"
      Bad = "#ff5d62" }

/// One step's own timing, in the runner's stopwatch-relative milliseconds
/// (§4.1's `StepLog`: `StartedMs`/`EndedMs`/`ObservedAtMs`) — needed by the
/// composer to place the click ripple at the moment its expectation was
/// actually observed, and to spread the synthetic-cursor holds across the
/// step's own recorded duration (§4.6, §9).
type StepTiming =
  { StartedMs: int
    EndedMs: int
    ObservedAtMs: int }

/// The pure plan `Compose.plan` produces from a `StepLog` — everything
/// `Ffmpeg.render` needs to build the one filtergraph, with no ffmpeg-string
/// concerns leaking into it.
/// TODO(shape): minimal placeholder; the exact fields (segment ordering,
/// per-step caption/magnifier data) are a Wave-2 `Compose.fs` concern.
type ComposePlan =
  { Segments: string list
    Layout: Map<ActorId, Rect>
    Style: Style
    Captions: Caption list
    // Same length as Segments/Captions — one per step, in step order; `[]` for
    // a step with no pointer motion. Magnifier is the editor pane's rect when
    // the layout has one (None for DashboardOnly). (§4.6)
    PointerPaths: Point list list
    // Same length as Segments — each segment's own local timing, used to
    // place the cursor holds and the click ripple within THAT segment's own
    // timeline (every `-i` input to a multi-input `-filter_complex` has its
    // own PTS starting near zero, so ripple/cursor timing is always relative
    // to the owning step, never the whole concatenated run). (§4.6)
    Timings: StepTiming list
    Magnifier: Rect option }

/// A pad name in a `-filter_complex` graph (§4.6): `Input i` is ffmpeg's own
/// `-i` stream (rendered `i:v`), `Named s` is a label produced by an earlier
/// node in the same graph (rendered `s`). Modeling pads as data means a
/// `[0:v]`/`[s3]`-style bracket never has to be hand-formatted more than once
/// (`Ffmpeg.toCommandString` is the one place that happens).
[<RequireQualifiedAccess>]
type Pad =
  | Input of index: int
  | Named of label: string

/// One dimension of a `drawbox` rectangle: either a fixed pixel value or a
/// raw ffmpeg filter EXPRESSION string (ffmpeg's own `t`-in-seconds
/// expression language, e.g. `"min(8+80*t,28)"`) — needed for the click
/// ripple's 8px→28px growth over 250ms (§9), which a fixed `Rect` cannot
/// express. `Expr` is deliberately just a `string`: ffmpeg's expression
/// grammar is its own closed language already validated by ffmpeg itself at
/// run time, and re-typing it as an F# AST here would just be a second,
/// out-of-sync parser for the same thing `toCommandString` already treats as
/// a single opaque, hand-verified literal (mirrors `toCommandString`'s own
/// "every case below is a fixed, hand-verified ffmpeg filter syntax" doctrine
/// applied at the sub-argument level).
[<RequireQualifiedAccess>]
type Extent =
  | Fixed of pixels: int
  | Expr of expression: string

/// A `drawbox` rectangle whose edges may vary over time (§9's growing click
/// ripple) or stay fixed (every other box this tool draws). Field names
/// deliberately differ from `Rect`'s `X`/`Y`/`W`/`H` (`Left`/`Top`/
/// `BoxWidth`/`BoxHeight`) — F# resolves an ambiguous `{ X = ...; Y = ... }`
/// record literal to whichever same-named-field record was declared LAST, so
/// reusing `Rect`'s field names here would silently repoint every existing
/// `Rect` literal in `Layout.fs`/`CellAgent.fs` at this new type instead.
type TimedRect =
  { Left: Extent
    Top: Extent
    BoxWidth: Extent
    BoxHeight: Extent }

/// Whether a `drawbox` is solid or a hollow ring of the given pixel width —
/// the click ripple (§9: "Accent ring") is a hollow, growing box; the caption
/// band and progress dots are solid fills.
[<RequireQualifiedAccess>]
type Thickness =
  | Fill
  | Outline of pixels: int

/// One ffmpeg filtergraph operation, modeled as data so a filter chain is
/// testable and can never drift into a broken hand-written command string
/// (§4.6). `MpDecimate`/`SetPts` are applied together, deliberately never
/// alongside `Fps` (§4.6: the dedup/fps ordering bug this avoids).
///
/// `Labeled`/`Complex` are what let this DU express a REAL multi-input
/// `-filter_complex` (§4.6 Wave-3 gap this module closes): `Labeled(ins,
/// filter, outs)` renders as ffmpeg's own `[in1][in2]...filterchain[out1]`
/// node syntax, and `Complex` joins an ordered list of such nodes with `;` —
/// ffmpeg's own node separator — so a `Concat`/`Overlay`/`PaletteUse` node
/// can finally carry the pad labels its multi-input ffmpeg filter actually
/// requires, entirely as typed data.
[<RequireQualifiedAccess>]
type FilterGraph =
  | Scale of width: int * height: int
  | Fps of int
  /// A two-input filter: overlays a second video/image pad onto the first at
  /// (`x`,`y`), optionally gated to a time window. `x`/`y` are `Extent` (not
  /// bare ints) so a position can reference ffmpeg's own `overlay_w`/
  /// `overlay_h` runtime variables (e.g. `"620-overlay_w/2"` to keep a
  /// time-varying-sized overlay centered) without the composer having to
  /// duplicate whatever sizing formula scaled that overlay in the first
  /// place (§4.6, §9's click ripple).
  | Overlay of x: Extent * y: Extent * enable: string option
  | DrawBox of Rect * color: string
  | DrawText of text: string * x: int * y: int
  | Crop of Rect
  | Concat of inputs: int
  | MpDecimate
  | SetPts of factor: float
  | PaletteGen of statsMode: string
  | PaletteUse of dither: string
  | Chain of FilterGraph list
  /// A `drawbox` that may be translucent (`alpha`), time-varying
  /// (`TimedRect`'s `Extent.Expr` edges), a hollow ring (`Thickness.Outline`),
  /// and/or gated to a time window (`enable`, ffmpeg's own `between(t,a,b)`
  /// idiom) — the synthetic cursor hold, the click ripple, and the
  /// translucent caption band (§9) are all this one case.
  | DrawBoxTimed of rect: TimedRect * color: string * alpha: float option * thickness: Thickness * enable: string option
  /// A `drawtext` with the color/size/optional time-gating `DrawText` does
  /// not carry — the caption text, the tabular step counter, and (were it
  /// ever gated to one step) any other styled overlay text (§9).
  | DrawTextStyled of text: string * x: int * y: int * color: string * fontSize: int * enable: string option
  /// ffmpeg's `split` filter: one input pad fanning out to N identical output
  /// pads — the mechanism a self-referential picture-in-picture (the
  /// magnifier) or a two-pass palette needs, since neither can be expressed
  /// as a single-input `Chain` (§4.6).
  | Split of outputs: int
  /// `scale` with `Extent` width/height and `eval=frame` always on — needed
  /// when a dimension is a `t`-varying expression (ffmpeg's `scale` only
  /// re-evaluates expression options once at init unless told otherwise),
  /// which is how the click ripple's PNG grows 8→28px over 250ms (§9)
  /// without the composer hand-computing per-frame sizes itself.
  | ScaleTimed of width: Extent * height: Extent
  /// `tpad=stop_mode=clone:stop_duration=<seconds>` — clones the LAST frame
  /// for `stopDurationSec` more seconds before the stream ends (§9: "final
  /// frame held 2.0s before the loop restarts"). Without this the GIF's own
  /// infinite loop snaps straight back to frame 0 the instant the last real
  /// frame's own short per-frame delay elapses.
  | Tpad of stopDurationSec: float
  /// One `-filter_complex` node: `inputs` feed `filter` (itself often a
  /// `Chain`), producing `outputs`. The ONLY place a bracketed pad name is
  /// attached to a filter.
  | Labeled of inputs: Pad list * filter: FilterGraph * outputs: Pad list
  /// An ordered list of `Labeled` (or other) nodes forming one full
  /// `-filter_complex` graph, `;`-joined by `toCommandString`.
  | Complex of nodes: FilterGraph list

// ---------------------------------------------------------------------------
// The runner ⇄ cell-agent wire shapes (§4.1) and freshness (§4.10).
// ---------------------------------------------------------------------------

/// The JSON the runner sends a cell-agent over the one stdio pipe (§4.1).
type ScenarioPlan =
  { ScenarioId: ScenarioId
    Layout: Map<ActorId, Rect>
    Seed: Seed
    Steps: Step list }

/// One step's result, as the cell-agent streams it back (§4.1).
type StepRecord =
  { Index: int
    Caption: Caption
    Segment: string
    StartedMs: int
    EndedMs: int
    PointerPath: Point list
    ObservedAtMs: int
    Outcome: Outcome }

/// What a cell returns for a whole scenario (§4.1, §4.5).
type StepLog =
  { ScenarioId: ScenarioId
    Steps: StepRecord list }

/// One fingerprint category a scenario's content depends on (§4.10). A DU,
/// not a string, so `check`'s "changed: Input list" names *what kind* of
/// thing changed without parsing prose.
[<RequireQualifiedAccess>]
type Input =
  | SampleTree of Sample
  | ClientSurface of Client
  | DaemonRoutes
  | ScenarioDefinition of ScenarioId
  | StyleAndProfiles
  | ToolVersions
  | NvimPluginCommit of string

/// One fingerprint category already resolved to its current content hash
/// (§4.10). A category alone can't be hashed or diffed against a prior
/// recording, so this is what `Fingerprint.ofInputs`/`check` actually take:
/// pre-hashed data (a git blob hash, computed by the edge in Wave 3), never
/// a filesystem/git lookup of their own.
/// TODO(shape): additive for the Schedule/Fingerprint planners (Wave 2).
type ResolvedInput = { Category: Input; Hash: Digest }

/// The full set of fingerprint categories one scenario declares, each
/// already resolved to its current content hash (§4.10) — coarse-but-safe
/// whole trees, composed by `Client × Sample × Capability`.
/// TODO(shape): the Wave-1 skeleton had this as bare `Input list`; a bare
/// category can't be hashed, so Wave 2 pairs each with its resolved digest.
type Inputs = ResolvedInput list

/// Whether a manifest already recorded input digests for a scenario. A DU
/// instead of `Inputs option`, so "never recorded" is a first-class reason
/// rather than an absent value a caller must remember to interpret.
/// TODO(shape): `At` carries the previously-resolved per-input digests
/// (not one combined blob) because `Fingerprint.check` must report EXACTLY
/// which `Input` categories changed (§4.10's "sagefs.nvim @ 3f2a…→ce2f…,
/// SageFs.Samples.RaylibGame/RaylibGame.fs" example) — one aggregate digest
/// can prove *that* something changed but never *what*. `manifest.json`
/// persisting this same per-input list (rather than a lone hash) is Wave 3's
/// concern, not this pure module's.
[<RequireQualifiedAccess>]
type RecordedDigest =
  | Never
  | At of Inputs

/// `sagefs-demos check`'s result for one scenario (§4.10, §1).
[<RequireQualifiedAccess>]
type Freshness =
  | Fresh
  | Stale of changed: Input list
  | Missing

/// A rendered storyboard strip (§4.8) — an SVG document, kept as a distinct
/// type rather than a bare `string` so a call site can't confuse it with any
/// other text.
type Svg = Svg of string

// ---------------------------------------------------------------------------
// The impure edge (§5) — injected, never called from a pure planner.
// ---------------------------------------------------------------------------

/// The one place side effects live: spawning processes, building/tearing
/// down a sandbox, delivering XTEST input, driving Playwright, calling the
/// VS Code extension host, talking to nvim's RPC socket, invoking ffmpeg,
/// reading the clock, and touching the filesystem. Every planner in this
/// project takes data in and returns data out; only code holding a
/// `DemoRuntime` may touch the world (mirrors `SessionManagerRuntime`).
/// TODO(shape): field shapes are a minimal, sensible reading of §4.1–§4.4;
/// Wave 2 will refine each as its actor/edge is built.
type DemoRuntime =
  { Spawn: string -> string list -> Async<int>
    Sandbox: Isolation -> Async<Cell>
    XTest: Cell -> X11Request list -> Async<unit>
    Playwright: Cell -> Target -> Async<ScreenRect>
    ExtHostCmd: Cell -> VsCodeCommand -> Async<ScreenRect>
    NvimRpc: Cell -> string -> Async<unit>
    Ffmpeg: FilterGraph -> Async<unit>
    Clock: unit -> System.DateTimeOffset
    Fs: string -> Async<byte[]> }
