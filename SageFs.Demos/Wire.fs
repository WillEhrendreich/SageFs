/// The one JSON contract that crosses the sandbox namespace wall (demo-gif-
/// plan.md §4.1): the runner writes a `ScenarioPlan` line to a cell-agent's
/// stdin, the cell-agent writes a `StepLog` line back on its stdout. These
/// types are deliberately flat, primitive-typed DTOs — not the rich
/// `Domain.Scenario`/`Step`/`Action` tree — because the cell-agent (running
/// inside a sealed cell with no reference to this tool's domain vocabulary
/// beyond "click this selector, expect that selector") only needs the
/// already-resolved facts the Phase-0 spike's own `ScenarioPlan`/`StepLog`
/// proved sufficient (`spike/cell-agent/Program.fs`), generalized from one
/// step to a `WireStep list`. `Runtime.fs` (outside every sandbox, with full
/// access to `Domain`) is the only place that maps between the two.
module SageFs.Demos.Wire

open System.Text.Json
open System.Text.Json.Serialization

/// One step already resolved to primitive, cell-agent-executable terms: an
/// OPTIONAL selector to click FIRST, before anything else this step does
/// (`PreClickSelector` — `Action.ClickThenTypeThenClick`'s "expand this
/// collapsed panel" beat, with NO gap before the click/type that follows,
/// §9's "watch SageFs evaluate F# live" demo), a `data-testid` CSS selector
/// to click (if any), the text to type (if any), a SECOND selector to click
/// right after typing (`SubmitSelector` — the "type here, then click the
/// [EVAL] button" beat), the selector this step's `Expectation` must
/// observe (if any), and how long to dwell after that observation lands
/// (§9's "≥ 1.0s dwell").
type WireStep =
  { Index: int
    Caption: string
    PreClickSelector: string option
    ClickSelector: string option
    TypeText: string option
    SubmitSelector: string option
    ExpectSelector: string option
    DwellMs: int
    /// Which actor drives this step — `"dashboard"|"vscode"|"neovim"|"app"|
    /// "agent"` (Island F, demo-actors-plan.md §1.2). `None` defaults to the
    /// plan's own `Client`; a joint scenario (e.g. an editor's hot-reload
    /// demo stepping between the editor and the App co-actor) sets this per
    /// step once the editor/App islands land. Every plan built before Island
    /// F has every step `None`, so this is purely additive.
    TargetActor: string option
    /// A chord/shortcut to deliver via XTest (`Domain.Key.toToken`-encoded,
    /// e.g. `["Ctrl";"Char:S"]` for Ctrl+S) — `Domain.Action.Chord`'s wire
    /// image (seam-integration threading, demo-actors-plan.md §3: an editor
    /// scenario's "save"/"eval"/"leave insert mode" beats are real keyboard
    /// chords, not clicks). `None` for every non-chord step; additive, so a
    /// plan built before this field existed still round-trips (missing on
    /// the wire ⇒ `None`).
    ChordKeys: string list option
    /// A non-filmed, API-level client command to run before this step's own
    /// click/type/observe (`Domain.Action.Setup`'s wire image) — the exact
    /// opaque token `Actors.<X>.command`/`Command` already accepts
    /// (`"run-app"|"stop-app"|"save-all"|"open-file:<absolute-path>"`, the
    /// path already resolved through `{{REPO_ROOT}}` by the runner, never a
    /// literal token the cell has to interpret further). `None` for every
    /// non-setup step.
    SetupCommand: string option
    /// Which actor OBSERVES this step's `ExpectSelector` — distinct from
    /// `TargetActor` because a step's own input and its expectation can be
    /// proven through two different live actors in the SAME cell (e.g. an
    /// editor client types into its own window, but the daemon's session
    /// state that proves the eval/toggle landed is read off the shared
    /// Dashboard narrator pane `EditorFull`/`EditorLeft` always places
    /// alongside it — Scenarios.VsCode.fs's own documented reasoning).
    /// `None` defaults to `TargetActor` (the pre-existing, single-actor
    /// behavior every Dashboard/Agent scenario already relies on) — purely
    /// additive.
    ObserveActor: string option }

/// One actor's placed rect for this scenario's `LayoutTemplate`
/// (`Layout.rects`, flattened to primitive fields the cell-agent can place a
/// real window at) — without this every non-Dashboard-only scenario would
/// have no way to tell the cell-agent where on `:99` each actor's window
/// belongs, and every actor would silently overlap at a hardcoded
/// full-screen rect (the pre-seam Dashboard-only assumption). `ActorToken`
/// is the same wire vocabulary `Client`/`TargetActor` already use
/// (`"dashboard"|"vscode"|"neovim"|"app"|"agent"`).
type WireRect =
  { ActorToken: string
    X: int
    Y: int
    W: int
    H: int }

/// Placeholder per-actor configs (Island F, demo-actors-plan.md §1.2): each
/// actor island fills/extends only its own field. Deliberately minimal —
/// Island F builds no actor logic, only the seam these config records plug
/// into (`Runtime.<X>.fs` will read them to shape that actor's cell binds
/// and launch prologue).
type VsCodeConfig =
  { /// The built `sagefs-vscode` extension directory to load via
    /// `--extensionDevelopmentPath` (§2.1). Filled by the VsCode island.
    /// Fixed to the cell-mount constant (`/vscode-ext`) once the seam
    /// integration wires real cell binds — the value is carried on the wire
    /// mainly so a plan without this field means "no VS Code launch needed"
    /// (`None`), not because the cell-agent has to discover the path itself.
    ExtensionDevPath: string option }

type NvimConfig =
  { /// The resolved, pinned sagefs.nvim commit SHA bound into the cell
    /// (§2.2 — roast I12: a resolved commit, never a floating ref). Filled
    /// by the Neovim island.
    PluginCommit: string option
    /// The pinned plugin checkout's OWN path, bound into the cell at the
    /// SAME absolute path on both sides (`Runtime.Neovim.actorBinds`'s own
    /// pattern) — unlike VS Code's fixed cell-mount constant, this scratch
    /// directory's path is generated fresh per run (a GUID-suffixed temp
    /// dir), so the cell-agent cannot hardcode it and needs it on the wire.
    PluginRuntimePath: string option }

type AppConfig =
  { /// Which `AppKind` (`"web"|"raylib"|"console"`) the App co-actor should
    /// launch (§2.3). Filled by the App island.
    Kind: string option }

/// The JSON the runner sends a cell-agent over the one stdio pipe (§4.1).
type ScenarioPlan =
  { ScenarioId: string
    ChromePath: string
    PageUrl: string
    UserDataDir: string
    OutDir: string
    Steps: WireStep list
    /// Which actor this scenario is filmed through — `"dashboard"|"vscode"|
    /// "neovim"|"agent"` (Island F, demo-actors-plan.md §1.2). `Runtime.fs`
    /// always fills this from `Domain.Scenario.Client`, so it is never
    /// actually absent on the wire; kept a plain `string`, not a DU, because
    /// this module is deliberately primitive-typed (see the module doc
    /// above) — the cell-agent maps it back to `ActorId` itself.
    Client: string
    VsCode: VsCodeConfig option
    Nvim: NvimConfig option
    App: AppConfig option
    /// Every actor's placed rect for this scenario's layout (`WireRect`
    /// above) — `[]` means "place everything full-screen" (the pre-seam
    /// Dashboard-only default `CellAgent.fs` falls back to), so an old plan
    /// with no rects still renders exactly as before.
    ActorRects: WireRect list
    /// The real, absolute, already-`{{REPO_ROOT}}`-resolved sample project
    /// directory a non-Dashboard client actor opens as its workspace (VS
    /// Code's positional folder argument, Neovim's `--listen`-launched
    /// cwd/opened file) — `None` when the scenario's own `Client` needs
    /// nothing opened this way (Dashboard drives its own "Open Directory"
    /// picker instead; Agent finds the repo itself, `Actors/Agent.fs`'s
    /// `RepoRoot.find`).
    WorkspaceDir: string option
    /// The real, absolute file Neovim opens on launch (its positional CLI
    /// argument) — `WorkspaceDir` ALONE is a directory, and Neovim opens a
    /// `netrw` directory listing (a read-only buffer) rather than an
    /// editable text buffer for a bare directory argument, which silently
    /// broke every scenario step that types into "whatever buffer is
    /// already open" (confirmed directly against a real recording: a still
    /// frame showed `netrw`'s `[No Name] [-][RO]` listing, not the sample's
    /// source). `Runtime.fs`'s `wirePlanOf` resolves this to the scenario's
    /// own first `Target.EditorPosition`-named file when one exists, else a
    /// real, existing `Program.fs` in the workspace (every runnable/live-
    /// testing sample has one) — never a guess at file content, only at
    /// which REAL file to open. `None` only for clients that do not open a
    /// single file this way (VS Code opens the whole `WorkspaceDir` folder;
    /// Dashboard/Agent need neither).
    NvimOpenFilePath: string option }

/// One step's result, as the cell-agent streams back (§4.1, §4.5). `Segment`
/// is the path as the CELL sees it (under `/out`) — `Runtime.fs` rewrites it
/// to the host path via the same RW bind mount before handing the log to the
/// pure `Compose`/`Ffmpeg` planners.
type WireStepResult =
  { Index: int
    Caption: string
    Segment: string
    StartedMs: int64
    EndedMs: int64
    PointerPath: int[] list
    ObservedAtMs: int64
    Outcome: string // "Passed" | "Failed"
    Message: string }

type StepLog =
  { ScenarioId: string
    Steps: WireStepResult list }

let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let serializePlan (plan: ScenarioPlan) : string = JsonSerializer.Serialize(plan, jsonOptions)
let deserializePlan (json: string) : ScenarioPlan = JsonSerializer.Deserialize<ScenarioPlan>(json, jsonOptions)
let serializeStepLog (log: StepLog) : string = JsonSerializer.Serialize(log, jsonOptions)
let deserializeStepLog (json: string) : StepLog = JsonSerializer.Deserialize<StepLog>(json, jsonOptions)
