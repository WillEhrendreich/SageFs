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
    TargetActor: string option }

/// Placeholder per-actor configs (Island F, demo-actors-plan.md §1.2): each
/// actor island fills/extends only its own field. Deliberately minimal —
/// Island F builds no actor logic, only the seam these config records plug
/// into (`Runtime.<X>.fs` will read them to shape that actor's cell binds
/// and launch prologue).
type VsCodeConfig =
  { /// The built `sagefs-vscode` extension directory to load via
    /// `--extensionDevelopmentPath` (§2.1). Filled by the VsCode island.
    ExtensionDevPath: string option }

type NvimConfig =
  { /// The resolved, pinned sagefs.nvim commit SHA bound into the cell
    /// (§2.2 — roast I12: a resolved commit, never a floating ref). Filled
    /// by the Neovim island.
    PluginCommit: string option }

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
    App: AppConfig option }

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
