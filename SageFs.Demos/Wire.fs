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

/// One step already resolved to primitive, cell-agent-executable terms: a
/// `data-testid` CSS selector to click (if any), the text to type (if any),
/// the selector this step's `Expectation` must observe (if any), and how
/// long to dwell after that observation lands (§9's "≥ 1.0s dwell").
type WireStep =
  { Index: int
    Caption: string
    ClickSelector: string option
    TypeText: string option
    ExpectSelector: string option
    DwellMs: int }

/// The JSON the runner sends a cell-agent over the one stdio pipe (§4.1).
type ScenarioPlan =
  { ScenarioId: string
    ChromePath: string
    PageUrl: string
    UserDataDir: string
    OutDir: string
    Steps: WireStep list }

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
