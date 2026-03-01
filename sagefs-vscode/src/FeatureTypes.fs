module SageFs.Vscode.FeatureTypes

open SageFs.Vscode.JsHelpers

// ── Feature 1: Eval Diff ────────────────────────────────────────────────

type VscDiffLineKind = Unchanged | Added | Removed | Modified

type VscDiffLine =
  { Kind: VscDiffLineKind
    Text: string
    OldText: string option }

type VscDiffSummary =
  { Added: int
    Removed: int
    Modified: int
    Unchanged: int }

type VscEvalDiff =
  { Lines: VscDiffLine list
    Summary: VscDiffSummary
    HasDiff: bool }

// ── Feature 2: Cell Dependency Graph ────────────────────────────────────

type VscCellNode =
  { CellId: int
    Source: string
    Produces: string list
    Consumes: string list
    IsStale: bool }

type VscCellEdge = { From: int; To: int }

type VscCellGraph =
  { Cells: VscCellNode list
    Edges: VscCellEdge list }

// ── Feature 3: Binding Explorer ─────────────────────────────────────────

type VscBindingInfo =
  { Name: string
    TypeSig: string
    CellIndex: int
    IsShadowed: bool
    ShadowedBy: int list
    ReferencedIn: int list }

type VscBindingScopeSnapshot =
  { Bindings: VscBindingInfo list
    ActiveCount: int
    ShadowedCount: int }

// ── Feature 4: Eval Timeline ────────────────────────────────────────────

type VscTimelineEntry =
  { CellId: int
    DurationMs: float
    Status: string
    Timestamp: float }

type VscTimelineStats =
  { Count: int
    P50Ms: float option
    P95Ms: float option
    P99Ms: float option
    MeanMs: float option
    Sparkline: string }

// ── Feature 5: Notebook Export ──────────────────────────────────────────

type VscNotebookCell =
  { Index: int
    Label: string option
    Code: string
    Output: string option
    Deps: int list
    Bindings: string list }

// ── SSE Feature Event Types ─────────────────────────────────────────────

type VscFeatureEvent =
  | EvalDiffReceived of VscEvalDiff
  | CellGraphReceived of VscCellGraph
  | BindingScopeReceived of VscBindingScopeSnapshot
  | TimelineReceived of VscTimelineStats

type FeatureCallbacks =
  { OnEvalDiff: VscEvalDiff -> unit
    OnCellGraph: VscCellGraph -> unit
    OnBindingScope: VscBindingScopeSnapshot -> unit
    OnTimeline: VscTimelineStats -> unit }

// ── JSON Parsers ────────────────────────────────────────────────────────

// CRITICAL: tryField arg order is (name: string) (obj: obj) — name FIRST, then obj.
// This matches JsHelpers.tryField<'T> (name: string) (obj: obj)

let parseDiffLine (data: obj) : VscDiffLine =
  let kindStr = tryField<string> "kind" data |> Option.defaultValue "unchanged"
  let kind =
    match kindStr with
    | "added" -> Added | "removed" -> Removed
    | "modified" -> Modified | _ -> Unchanged
  { Kind = kind
    Text = tryField<string> "text" data |> Option.defaultValue ""
    OldText = tryField<string> "oldText" data }

let parseDiffSummary (data: obj) : VscDiffSummary =
  { Added = tryField<int> "added" data |> Option.defaultValue 0
    Removed = tryField<int> "removed" data |> Option.defaultValue 0
    Modified = tryField<int> "modified" data |> Option.defaultValue 0
    Unchanged = tryField<int> "unchanged" data |> Option.defaultValue 0 }

let parseEvalDiff (data: obj) : VscEvalDiff =
  let lines =
    tryField<obj array> "lines" data
    |> Option.map (Array.map parseDiffLine >> Array.toList)
    |> Option.defaultValue []
  let summary =
    tryField<obj> "summary" data
    |> Option.map parseDiffSummary
    |> Option.defaultValue { Added = 0; Removed = 0; Modified = 0; Unchanged = 0 }
  { Lines = lines; Summary = summary
    HasDiff = tryField<bool> "hasDiff" data |> Option.defaultValue false }

let parseCellNode (data: obj) : VscCellNode =
  { CellId = tryField<int> "cellId" data |> Option.defaultValue 0
    Source = tryField<string> "source" data |> Option.defaultValue ""
    Produces =
      tryField<obj array> "produces" data
      |> Option.map (Array.choose (tryOfObj >> Option.map unbox<string>) >> Array.toList)
      |> Option.defaultValue []
    Consumes =
      tryField<obj array> "consumes" data
      |> Option.map (Array.choose (tryOfObj >> Option.map unbox<string>) >> Array.toList)
      |> Option.defaultValue []
    IsStale = tryField<bool> "isStale" data |> Option.defaultValue false }

let parseCellGraph (data: obj) : VscCellGraph =
  let cells =
    tryField<obj array> "cells" data
    |> Option.map (Array.map parseCellNode >> Array.toList)
    |> Option.defaultValue []
  let edges =
    tryField<obj array> "edges" data
    |> Option.map (Array.map (fun e ->
      { From = tryField<int> "from" e |> Option.defaultValue 0
        To = tryField<int> "to" e |> Option.defaultValue 0 }
    ) >> Array.toList)
    |> Option.defaultValue []
  { Cells = cells; Edges = edges }

let parseBindingInfo (data: obj) : VscBindingInfo =
  { Name = tryField<string> "name" data |> Option.defaultValue ""
    TypeSig = tryField<string> "typeSig" data |> Option.defaultValue ""
    CellIndex = tryField<int> "cellIndex" data |> Option.defaultValue 0
    IsShadowed = tryField<bool> "isShadowed" data |> Option.defaultValue false
    ShadowedBy =
      tryField<obj array> "shadowedBy" data
      |> Option.map (Array.choose (tryOfObj >> Option.map unbox<int>) >> Array.toList)
      |> Option.defaultValue []
    ReferencedIn =
      tryField<obj array> "referencedIn" data
      |> Option.map (Array.choose (tryOfObj >> Option.map unbox<int>) >> Array.toList)
      |> Option.defaultValue [] }

let parseBindingScopeSnapshot (data: obj) : VscBindingScopeSnapshot =
  let bindings =
    tryField<obj array> "bindings" data
    |> Option.map (Array.map parseBindingInfo >> Array.toList)
    |> Option.defaultValue []
  { Bindings = bindings
    ActiveCount = tryField<int> "activeCount" data |> Option.defaultValue 0
    ShadowedCount = tryField<int> "shadowedCount" data |> Option.defaultValue 0 }

let parseTimelineStats (data: obj) : VscTimelineStats =
  { Count = tryField<int> "count" data |> Option.defaultValue 0
    P50Ms = tryField<float> "p50Ms" data
    P95Ms = tryField<float> "p95Ms" data
    P99Ms = tryField<float> "p99Ms" data
    MeanMs = tryField<float> "meanMs" data
    Sparkline = tryField<string> "sparkline" data |> Option.defaultValue "" }

let parseNotebookCell (data: obj) : VscNotebookCell =
  { Index = tryField<int> "index" data |> Option.defaultValue 0
    Label = tryField<string> "label" data
    Code = tryField<string> "code" data |> Option.defaultValue ""
    Output = tryField<string> "output" data
    Deps =
      tryField<obj array> "deps" data
      |> Option.map (Array.choose (tryOfObj >> Option.map unbox<int>) >> Array.toList)
      |> Option.defaultValue []
    Bindings =
      tryField<obj array> "bindings" data
      |> Option.map (Array.choose (tryOfObj >> Option.map unbox<string>) >> Array.toList)
      |> Option.defaultValue [] }

let processFeatureEvent (eventType: string) (data: obj) (callbacks: FeatureCallbacks) =
  match eventType with
  | "eval_diff" -> callbacks.OnEvalDiff (parseEvalDiff data)
  | "cell_graph" -> callbacks.OnCellGraph (parseCellGraph data)
  | "binding_scope" -> callbacks.OnBindingScope (parseBindingScopeSnapshot data)
  | "eval_timeline" -> callbacks.OnTimeline (parseTimelineStats data)
  | _ -> ()

let formatSparklineStatus (stats: VscTimelineStats) =
  if stats.Count = 0 then ""
  else
    let p50 =
      stats.P50Ms
      |> Option.map (sprintf "p50=%.0fms")
      |> Option.defaultValue ""
    sprintf "⚡ %s [%d] %s" stats.Sparkline stats.Count p50
