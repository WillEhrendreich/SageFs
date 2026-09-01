module SageFs.Vscode.CoverageViewPure

// WHY — this is the pure rendering + parsing logic for the coverage_view
// SSE event. It has NO Fable dependency so the contract test runs under
// plain `dotnet fsi` in <2s (vs the full Fable build which is 30s+).
//
// The Fable-aware module `CoverageView.fs` wraps this with a thin
// `JsInterop.jsOptions` parser. If the parser drifts, the tests on
// the pure shape catch it.
//
// The shape mirrors the server-side CoverageView exactly:
//   Symbol / FilePath / DefinitionLine — the function identity
//   TotalCount — total covering tests (may be 0 = absent)
//   Overflow — DU (not bool) with the hidden count
//   InlineBadgeText — pre-formatted single line, no rendering at use site
//   Health — DU preserving the 5 status kinds (not collapsed)

open System.Text

/// Overflow indicator — NOT a bool. The renderer needs the exact
/// "hidden" count to render "▾ +N more", not just a flag.
[<RequireQualifiedAccess>]
type Overflow =
  | Within
  | Overflow of hidden: int

/// Honest health indicator — NOT a bool. Preserves the 5 status kinds
/// so the renderer can show the exact problem (a Stale test is not
/// the same as a Passing test).
[<RequireQualifiedAccess>]
type CoverageHealth =
  | Passing
  | Failing
  | Running
  | Stale
  | Skipped
  | Absent

type CoverageView = {
  Symbol: string
  FilePath: string
  DefinitionLine: int
  TotalCount: int
  Overflow: Overflow
  /// Pre-formatted single-line badge text (e.g. "v 97 x 3").
  /// Editors render this as one line of virtual text or one CodeLens.
  /// The render budget is HARD: one line, always.
  InlineBadgeText: string
  Health: CoverageHealth
}

let healthFromString (s: string) : CoverageHealth =
  match s with
  | "Passing" -> CoverageHealth.Passing
  | "Failing" -> CoverageHealth.Failing
  | "Running" -> CoverageHealth.Running
  | "Stale" -> CoverageHealth.Stale
  | "Skipped" -> CoverageHealth.Skipped
  | _ -> CoverageHealth.Absent

let toInlineBadge (badges: (string * int) list) : string =
  badges
  |> List.fold (fun (acc, first) (kind, count) ->
    let prefix = if first then "" else " "
    let fragment =
      match kind with
      | "Pass" -> "✓ " + string count
      | "Fail" -> "✗ " + string count
      | "Running" -> "⟳ " + string count
      | "Stale" -> "~ " + string count
      | "Skipped" -> "⊘ " + string count
      | _ -> ""
    (acc + prefix + fragment, false)) ("", true)
  |> fst

/// Editor-side configuration for coverage view rendering.
/// The daemon sends full CoverageView data; the editor decides
/// how to render based on this config. No round-trip needed.
type CoverageViewConfig = {
  /// Inline names only when total covering tests < this number.
  /// Default Int32.MaxValue: always inline (F# users have many tests
  /// per function; auto-collapsing would punish their style).
  InlineCollapseAt: int
}

module CoverageViewConfig =
  /// F#-friendly default: no auto-collapse.
  let defaults = { InlineCollapseAt = System.Int32.MaxValue }

/// Pure CodeLens shape — editor-agnostic, no Fable dependency.
/// Rendered to VSCode CodeLens by CoverageViewCodeLensProvider.
type PureCodeLens = {
  Line: int
  Title: string
  Tooltip: string
  CommandLabel: string
}

/// Pure projection: CoverageView → PureCodeLens. No Option, no bool,
/// no mutable state — the same projection used by the contract tests.
module PureProvider =
  let tooltipSuffix (v: CoverageView) : string =
    match v.Overflow with
    | Overflow.Within -> ""
    | Overflow.Overflow n -> sprintf "%d more" n

  /// When TotalCount >= InlineCollapseAt, return a collapsed badge
  /// ("▸ 42 tests") instead of the full inline text. This keeps the
  /// editor readable when a function has 50+ covering tests.
  let collapsedTitle (v: CoverageView) : string =
    sprintf "▸ %d tests" v.TotalCount

  let project (config: CoverageViewConfig) (v: CoverageView) : PureCodeLens =
    let title =
      if v.TotalCount >= config.InlineCollapseAt then collapsedTitle v
      else v.InlineBadgeText
    { Line = v.DefinitionLine
      Title = title
      Tooltip = sprintf "%d test(s), %s" v.TotalCount (tooltipSuffix v)
      CommandLabel = "sagefs.showCoveringTests" }

  let lensesForFile
    (config: CoverageViewConfig)
    (store: Map<string, CoverageView array>)
    (file: string)
    : PureCodeLens array =
    match Map.tryFind file store with
    | Some arr -> arr |> Array.map (project config)
    | None -> [||]
