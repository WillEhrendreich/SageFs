module SageFs.Vscode.CoverageView

// WHY — Fable-specific parser that turns a coverage_view SSE payload
// into a typed VscCoverageView. The pure rendering logic
// (CoverageViewPure) is tested under plain `dotnet fsi` via
// CoverageViewContractTests.fsx. This file is the thin Fable shim.

open SageFs.Vscode.LiveTestingTypes

let private fieldString (name: string) (o: obj) : string =
  match Fable.Core.JsInterop.jsOptions o with
  | None -> ""
  | Some opts ->
    match opts?``${name}``` with
    | null -> ""
    | s -> string s

let private fieldInt (name: string) (o: obj) : int =
  match Fable.Core.JsInterop.jsOptions o with
  | None -> 0
  | Some opts ->
    match opts?``${name}``` with
    | null -> 0
    | n -> int n

let private fieldArray (name: string) (o: obj) : obj array =
  match Fable.Core.JsInterop.jsOptions o with
  | None -> [||]
  | Some opts ->
    match opts?``${name}``` with
    | null -> [||]
    | arr -> arr :?> obj array

/// Map the server-side health DU name to the Fable-side DU.
let private parseHealth (s: string) : VscCoverageHealth =
  match s with
  | "Passing" -> VscCoveragePassing
  | "Failing" -> VscCoverageFailing
  | "Running" -> VscCoverageRunning
  | "Stale" -> VscCoverageStale
  | "Skipped" -> VscCoverageSkipped
  | _ -> VscCoverageAbsent

/// Map the server-side overflow DU name + Fields to Fable DU.
/// The server serializes `Overflow.Overflow 3` as
/// `{"Case":"Overflow","Fields":[3]}`.
let private parseOverflow (o: obj) : VscCoverageOverflow =
  let case = fieldString "Case" o
  let fields = fieldArray "Fields" o
  match case, fields.Length with
  | "Overflow", n when n >= 1 -> VscOverflowOf (fieldInt "0" fields.[0])
  | _ -> VscOverflowWithin

/// Parse a coverage_view SSE payload into a typed VscCoverageView.
let parseCoverageView (data: obj) : VscCoverageView =
  let overflowObj =
    fieldArray "Overflow" data |> Array.tryHead |> Option.defaultValue (box "")
  let healthObj =
    fieldArray "Health" data |> Array.tryHead |> Option.defaultValue (box "")
  {
    Symbol = fieldString "Symbol" data
    FilePath = fieldString "FilePath" data
    DefinitionLine = fieldInt "DefinitionLine" data
    TotalCount = fieldInt "TotalCount" data
    Overflow = parseOverflow overflowObj
    InlineBadgeText = fieldString "InlineBadgeText" data
    Health = parseHealth (fieldString "Case" healthObj)
  }
