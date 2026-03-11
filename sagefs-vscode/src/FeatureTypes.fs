module SageFs.Vscode.FeatureTypes

open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

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

// ── Feature 6: Binding Values (inline ghost text) ────────────────────────────

/// Mirror of server BindingValue — carries all info needed for inline decorations.
/// SourceLine is 1-based within the evaluated block (0 = unknown).
type ClientBindingValue =
  { Name: string
    TypeSig: string
    DisplayValue: string
    IsTruncated: bool
    IsFunctionValue: bool
    CellIndex: int
    EvalDurationMs: float
    SourceLine: int }

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

// CRITICAL: SafeInterop field accessors use (name: string) (obj: obj) — name FIRST, then obj.

/// Produce compact ghost text for inline editor decoration.
/// Semantics mirror server-side BindingValue.toGhostText — must stay in sync.
/// e.g. "→ 42  int"  "→ <fn>  int -> string"  "→ [1; 2; …]  int list  ⟨truncated⟩"
let toGhostText (bv: ClientBindingValue) : string =
  let displayVal =
    match bv.IsFunctionValue with
    | true  -> "<fn>"
    | false ->
      match bv.DisplayValue with
      | "<null>" -> "null"
      | d ->
        match bv.IsTruncated with
        | true  ->
          match d.EndsWith("...]") || d.EndsWith("...}") with
          | true  -> d.[..d.Length - 5] + "…" + string d.[d.Length - 1]
          | false -> d.TrimEnd('.') + "…"
        | false -> d
  let base_ = sprintf "→ %s  %s" displayVal bv.TypeSig
  match bv.IsTruncated with
  | true  -> base_ + "  ⟨truncated⟩"
  | false -> base_

/// Parse a BindingValue object from the server's bindings_snapshot SSE payload.
/// All fields are null-safe. SourceLine=0 when absent (single-parse fallback).
let parseClientBindingValue (data: obj) : ClientBindingValue option =
  match jsIsNullOrUndefined data with
  | true -> None
  | false ->
    let name = fieldString "Name" data |> Option.defaultValue ""
    match name with
    | "" -> None
    | _ ->
      Some {
        Name = name
        TypeSig = fieldString "TypeSig" data |> Option.defaultValue ""
        DisplayValue = fieldString "DisplayValue" data |> Option.defaultValue ""
        IsTruncated = fieldBool "IsTruncated" data |> Option.defaultValue false
        IsFunctionValue = fieldBool "IsFunctionValue" data |> Option.defaultValue false
        CellIndex = fieldInt "CellIndex" data |> Option.defaultValue 0
        EvalDurationMs = fieldFloat "EvalDurationMs" data |> Option.defaultValue 0.0
        SourceLine = fieldInt "SourceLine" data |> Option.defaultValue 0
      }

let parseDiffLine (data: obj) : VscDiffLine =
  let kindStr = fieldString "kind" data |> Option.defaultValue "unchanged"
  let kind =
    match kindStr with
    | "added" -> Added | "removed" -> Removed
    | "modified" -> Modified | _ -> Unchanged
  { Kind = kind
    Text = fieldString "text" data |> Option.defaultValue ""
    OldText = fieldString "oldText" data }

let parseDiffSummary (data: obj) : VscDiffSummary =
  { Added = fieldInt "added" data |> Option.defaultValue 0
    Removed = fieldInt "removed" data |> Option.defaultValue 0
    Modified = fieldInt "modified" data |> Option.defaultValue 0
    Unchanged = fieldInt "unchanged" data |> Option.defaultValue 0 }

let parseEvalDiff (data: obj) : VscEvalDiff =
  let lines =
    fieldArray "lines" data
    |> Option.map (Array.map parseDiffLine >> Array.toList)
    |> Option.defaultValue []
  let summary =
    fieldObj "summary" data
    |> Option.map parseDiffSummary
    |> Option.defaultValue { Added = 0; Removed = 0; Modified = 0; Unchanged = 0 }
  { Lines = lines; Summary = summary
    HasDiff = fieldBool "hasDiff" data |> Option.defaultValue false }

let parseCellNode (data: obj) : VscCellNode =
  { CellId = fieldInt "cellId" data |> Option.defaultValue 0
    Source = fieldString "source" data |> Option.defaultValue ""
    Produces =
      fieldArray "produces" data
      |> Option.map (Array.choose tryCastString >> Array.toList)
      |> Option.defaultValue []
    Consumes =
      fieldArray "consumes" data
      |> Option.map (Array.choose tryCastString >> Array.toList)
      |> Option.defaultValue []
    IsStale = fieldBool "isStale" data |> Option.defaultValue false }

let parseCellGraph (data: obj) : VscCellGraph =
  let cells =
    fieldArray "cells" data
    |> Option.map (Array.map parseCellNode >> Array.toList)
    |> Option.defaultValue []
  let edges =
    fieldArray "edges" data
    |> Option.map (Array.map (fun e ->
      { From = fieldInt "from" e |> Option.defaultValue 0
        To = fieldInt "to" e |> Option.defaultValue 0 }
    ) >> Array.toList)
    |> Option.defaultValue []
  { Cells = cells; Edges = edges }

let parseBindingInfo (data: obj) : VscBindingInfo =
  { Name = fieldString "name" data |> Option.defaultValue ""
    TypeSig = fieldString "typeSig" data |> Option.defaultValue ""
    CellIndex = fieldInt "cellIndex" data |> Option.defaultValue 0
    IsShadowed = fieldBool "isShadowed" data |> Option.defaultValue false
    ShadowedBy =
      fieldArray "shadowedBy" data
      |> Option.map (Array.choose tryCastInt >> Array.toList)
      |> Option.defaultValue []
    ReferencedIn =
      fieldArray "referencedIn" data
      |> Option.map (Array.choose tryCastInt >> Array.toList)
      |> Option.defaultValue [] }

let parseBindingScopeSnapshot (data: obj) : VscBindingScopeSnapshot =
  let bindings =
    fieldArray "bindings" data
    |> Option.map (Array.map parseBindingInfo >> Array.toList)
    |> Option.defaultValue []
  { Bindings = bindings
    ActiveCount = fieldInt "activeCount" data |> Option.defaultValue 0
    ShadowedCount = fieldInt "shadowedCount" data |> Option.defaultValue 0 }

let parseTimelineStats (data: obj) : VscTimelineStats =
  { Count = fieldInt "count" data |> Option.defaultValue 0
    P50Ms = fieldFloat "p50Ms" data
    P95Ms = fieldFloat "p95Ms" data
    P99Ms = fieldFloat "p99Ms" data
    MeanMs = fieldFloat "meanMs" data
    Sparkline = fieldString "sparkline" data |> Option.defaultValue "" }

let parseNotebookCell (data: obj) : VscNotebookCell =
  { Index = fieldInt "index" data |> Option.defaultValue 0
    Label = fieldString "label" data
    Code = fieldString "code" data |> Option.defaultValue ""
    Output = fieldString "output" data
    Deps =
      fieldArray "deps" data
      |> Option.map (Array.choose tryCastInt >> Array.toList)
      |> Option.defaultValue []
    Bindings =
      fieldArray "bindings" data
      |> Option.map (Array.choose tryCastString >> Array.toList)
      |> Option.defaultValue [] }

let processFeatureEvent (eventType: string) (data: obj) (callbacks: FeatureCallbacks) =
  tryHandleEvent eventType (fun () ->
    match eventType with
    | "eval_diff" -> callbacks.OnEvalDiff (parseEvalDiff data)
    | "cell_dependencies" -> callbacks.OnCellGraph (parseCellGraph data)
    | "binding_scope_map" -> callbacks.OnBindingScope (parseBindingScopeSnapshot data)
    | "eval_timeline" -> callbacks.OnTimeline (parseTimelineStats data)
    | _ -> ())

let formatSparklineStatus (stats: VscTimelineStats) =
  if stats.Count = 0 then ""
  else
    let p50 =
      stats.P50Ms
      |> Option.map (sprintf "p50=%.0fms")
      |> Option.defaultValue ""
    sprintf "⚡ %s [%d] %s" stats.Sparkline stats.Count p50
