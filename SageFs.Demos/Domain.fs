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

module Client =
  /// Every editor/client surface a scenario can be filmed through.
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

module Signal =
  let appOutputChanged = Signal.AppOutputChanged
  let hotReloadApplied = Signal.HotReloadApplied
  let testRunCompleted = Signal.TestRunCompleted

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

[<RequireQualifiedAccess>]
type Action =
  | Click of Target
  | Type of Target * Text * CadenceSeed
  | Chord of Key list
  | Typo of Target * wrong: Text * right: Text
  | Setup of ClientCommand
  | Await of Signal

[<RequireQualifiedAccess>]
type Expectation =
  | PageShows of DashboardId * Text
  | AppState of AppRunStateCase
  | EditorSaved of SampleFile
  | TestOutcome of TestId * Outcome
  | NvimBufferContains of Text
  | AppOutputChanged of Region

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
/// (`GetKeyboardMapping`) so `Keymap.resolve` never hard-codes a layout.
/// TODO(shape): minimal placeholder for the fetched mapping shape.
type KeyboardMapping = { KeysymToKeycode: Map<int, int> }

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
    Magnifier: Rect option }

/// One ffmpeg filtergraph operation, modeled as data so a filter chain is
/// testable and can never drift into a broken hand-written command string
/// (§4.6). `MpDecimate`/`SetPts` are applied together, deliberately never
/// alongside `Fps` (§4.6: the dedup/fps ordering bug this avoids).
[<RequireQualifiedAccess>]
type FilterGraph =
  | Scale of width: int * height: int
  | Fps of int
  | Overlay of x: int * y: int
  | DrawBox of Rect * color: string
  | DrawText of text: string * x: int * y: int
  | Crop of Rect
  | Concat of inputs: int
  | MpDecimate
  | SetPts of factor: float
  | PaletteGen of statsMode: string
  | PaletteUse of dither: string
  | Chain of FilterGraph list

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
