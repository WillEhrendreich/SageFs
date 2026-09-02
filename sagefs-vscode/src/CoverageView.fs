module SageFs.Vscode.CoverageView

// WHY — Fable-specific parser that turns a coverage_view SSE payload
// into a typed CoverageViewPure.CoverageView. The pure rendering logic
// (CoverageViewPure) is tested under plain `dotnet fsi` via
// CoverageViewContractTests.fsx. This file is the thin Fable shim.

open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.CoverageViewPure
open SageFs.Vscode.SafeInterop

/// Map the server-side health DU name to the pure CoverageHealth DU.
let private parseHealth (s: string) : CoverageHealth =
  match s with
  | "Passing" -> CoverageHealth.Passing
  | "Failing" -> CoverageHealth.Failing
  | "Running" -> CoverageHealth.Running
  | "Stale" -> CoverageHealth.Stale
  | "Skipped" -> CoverageHealth.Skipped
  | _ -> CoverageHealth.Absent

/// Map the server-side overflow DU name + Fields to pure Overflow DU.
let private parseOverflow (caseName: string) (fields: obj array) : Overflow =
  match caseName, fields.Length with
  | "Overflow", n when n >= 1 ->
    match fields.[0] with
    | :? int as n -> Overflow.Overflow n
    | _ -> Overflow.Overflow 0
  | _ -> Overflow.Within

/// Parse a coverage_view SSE payload into a typed CoverageViewPure.CoverageView.
let parseCoverageView (data: obj) : CoverageView =
  let overflowArr = fieldArray "Overflow" data |> Option.defaultValue [||]
  let overflowCase =
    match overflowArr |> Array.tryHead with
    | Some o -> fieldString "Case" o |> Option.defaultValue ""
    | None -> ""
  let overflowFields =
    match overflowArr |> Array.tryHead with
    | Some o -> fieldArray "Fields" o |> Option.defaultValue [||]
    | None -> [||]
  let healthArr = fieldArray "Health" data |> Option.defaultValue [||]
  let healthStr =
    match healthArr |> Array.tryHead with
    | Some s -> string s
    | None -> "Absent"
  { Symbol = fieldString "Symbol" data |> Option.defaultValue ""
    FilePath = fieldString "FilePath" data |> Option.defaultValue ""
    DefinitionLine = fieldInt "DefinitionLine" data |> Option.defaultValue 0
    TotalCount = fieldInt "TotalCount" data |> Option.defaultValue 0
    Overflow = parseOverflow overflowCase overflowFields
    InlineBadgeText = fieldString "InlineBadgeText" data |> Option.defaultValue ""
    Health = parseHealth healthStr }

/// Parse the run generation from a coverage_view SSE payload (the batch
/// generation that produced this view). Defaults to 0 when absent (older
/// servers) — the sweep treats generation 0 as "always current" so views
/// from a server without generations are never dropped.
let parseGeneration (data: obj) : int =
  fieldInt "Generation" data |> Option.defaultValue 0
