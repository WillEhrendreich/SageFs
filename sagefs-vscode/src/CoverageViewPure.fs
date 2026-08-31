module SageFs.Vscode.CoverageViewPure

// WHY — this is the pure rendering + parsing logic for the coverage_view
// SSE event. It has NO Fable dependency so the contract test runs under
// plain `dotnet fsi` in <2s (vs the full Fable build which is 30s+).
//
// The Fable-aware module `CoverageView.fs` wraps this with a thin
// `JsInterop.jsOptions` parser. If the parser drifts, the tests on
// the pure shape catch it.

open System.Text

type CoverageHealth =
  | AllPassing
  | SomeFailing
  | NoCoverage

type CoverageView = {
  Symbol: string
  FilePath: string
  DefinitionLine: int
  TotalCount: int
  HasOverflow: bool
  /// Pre-formatted single-line badge text (e.g. "✓ 97 ✗ 3").
  /// Editors render this as one line of virtual text or one CodeLens.
  /// The render budget for this field is HARD: one line, always.
  InlineBadgeText: string
  Health: CoverageHealth
}

/// Format a single badge entry as a short string fragment.
/// WHY — JIT will fold this for the bounded 5-case DU. We deliberately
/// do NOT use a string interpolation inside a loop because every
/// interpolation allocates.
let formatBadge (case: string) (count: int) : string =
  match case with
  | "Pass" -> sprintf "✓ %d" count
  | "Fail" -> sprintf "✗ %d" count
  | "Running" -> sprintf "⟳ %d" count
  | "Stale" -> sprintf "~ %d" count
  | "Skipped" -> sprintf "⊘ %d" count
  | _ -> ""

// WHY — one StringBuilder, one pass, no string concat inside the loop.
// Even at the worst case of 5 badges, the output is < 16 chars.
let joinBadges (badges: (string * int) list) : string =
  match badges with
  | [] -> ""
  | _ ->
    let sb = StringBuilder(32)
    let mutable first = true
    for (case, count) in badges do
      if not first then sb.Append(' ') |> ignore
      sb.Append(formatBadge case count) |> ignore
      first <- false
    sb.ToString()

let healthFromString (s: string) : CoverageHealth =
  match s with
  | "AllPassing" -> CoverageHealth.AllPassing
  | "SomeFailing" -> CoverageHealth.SomeFailing
  | _ -> CoverageHealth.NoCoverage

// Fable-independent: accepts the already-extracted badge list
// so the parser layer (Fable-specific) can hand off to pure logic
// without re-parsing.
let fromExtracted
  (symbol: string)
  (filePath: string)
  (definitionLine: int)
  (totalCount: int)
  (hasOverflow: bool)
  (badges: (string * int) list)
  (health: CoverageHealth)
  : CoverageView =
  {
    Symbol = symbol
    FilePath = filePath
    DefinitionLine = definitionLine
    TotalCount = totalCount
    HasOverflow = hasOverflow
    InlineBadgeText = joinBadges badges
    Health = health
  }
