module SageFs.Vscode.CoverageView

// WHY — Fable-specific parser that wraps the pure CoverageViewPure
// module. The pure module has no Fable dependency and is covered by
// CoverageViewContractTests.fsx running under plain `dotnet fsi`. This
// file is the thin Fable shim that turns a JS-interop object (the SSE
// event payload) into a typed CoverageView.

open SageFs.Vscode.CoverageViewPure

// Re-export the pure types so consumers can `open SageFs.Vscode.CoverageView`
// and get everything they need.
type CoverageHealth = CoverageViewPure.CoverageHealth
type CoverageView = CoverageViewPure.CoverageView

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

let private fieldBool (name: string) (o: obj) : bool =
  match Fable.Core.JsInterop.jsOptions o with
  | None -> false
  | Some opts ->
    match opts?``${name}``` with
    | null -> false
    | b -> bool b

let private fieldArray (name: string) (o: obj) : obj array =
  match Fable.Core.JsInterop.jsOptions o with
  | None -> [||]
  | Some opts ->
    match opts?``${name}``` with
    | null -> [||]
    | arr -> arr :?> obj array

let private extractBadges (badges: obj array) : (string * int) list =
  badges
  |> Array.toList
  |> List.map (fun b ->
    let case = fieldString "Case" b
    let fields = fieldArray "Fields" b
    let count =
      match fields.Length > 0 with
      | true -> fieldInt "0" fields.[0]
      | false -> 0
    (case, count))

/// Parse a coverage_view SSE payload into a typed CoverageView.
/// WHY — the editor only ever wants a per-function aggregate, never a
/// per-test list. This parser produces one value; downstream code does
/// not iterate.
let parseCoverageView (data: obj) : CoverageView =
  let badgeObjs = fieldArray "InlineBadge" data
  let healthObj = fieldArray "Health" data |> Array.tryHead |> Option.defaultValue (box "")
  CoverageViewPure.fromExtracted
    (fieldString "Symbol" data)
    (fieldString "FilePath" data)
    (fieldInt "DefinitionLine" data)
    (fieldInt "TotalCount" data)
    (fieldBool "HasOverflow" data)
    (extractBadges badgeObjs)
    (CoverageViewPure.healthFromString (fieldString "Case" healthObj))
