module SageFs.Features.FsiOutputParser

open System
open System.IO

// ── Core types ────────────────────────────────────────────────────────────────

/// A single FSI binding extracted from FSI output (e.g. `val x : int = 43`).
/// Carries everything needed to render inline ghost text in editors.
type BindingValue = {
  Name: string
  TypeSig: string
  /// Raw display string from FSI, with `<fun>` preserved.
  DisplayValue: string
  /// True when FSI truncated the value with `...`
  IsTruncated: bool
  /// True when `DisplayValue` is `<fun>`
  IsFunctionValue: bool
  /// Index of the cell that produced this binding (0 if unknown)
  CellIndex: int
  /// How long the eval took, in ms (0.0 if unknown)
  EvalDurationMs: float
}

/// What kind of code was evaluated — derived from the SUBMITTED code, not FSI output.
[<RequireQualifiedAccess>]
type EvalBoundaryKind =
  | LetBinding of name: string
  | TypeOrModuleDefinition of name: string
  | DoExpression
  | OpenStatement of namespaceName: string
  | HashDirective of directive: string
  | WholeFile of path: string
  | Unknown

// ── BindingValue helpers ──────────────────────────────────────────────────────

module BindingValue =

  /// Produces a compact ghost-text string for inline editor decoration.
  /// e.g.  "→ 42  int"  or  "→ <fn>  int -> string"  or  "→ [1; 2; …]  int list  ⟨truncated⟩"
  let toGhostText (bv: BindingValue) : string =
    let displayVal =
      match bv.IsFunctionValue with
      | true  -> "<fn>"
      | false ->
        match bv.DisplayValue with
        | "<null>" -> "null"
        | d ->
          match bv.IsTruncated with
          | true  ->
            // Replace trailing "...]" with "…]" if present, else just append "…"
            match d.EndsWith("...]") || d.EndsWith("...}") with
            | true  -> d.[..d.Length - 5] + "…" + string d.[d.Length - 1]
            | false -> d.TrimEnd('.') + "…"
          | false -> d
    let base_ = sprintf "→ %s  %s" displayVal bv.TypeSig
    match bv.IsTruncated with
    | true  -> base_ + "  ⟨truncated⟩"
    | false -> base_

// ── EvalBoundaryKind helpers ──────────────────────────────────────────────────

module EvalBoundaryKind =

  /// A short human-readable label for filmstrip entries, status bars, etc.
  let toLabel (kind: EvalBoundaryKind) : string =
    match kind with
    | EvalBoundaryKind.LetBinding name             -> sprintf "let %s" name
    | EvalBoundaryKind.TypeOrModuleDefinition name -> sprintf "type %s" name
    | EvalBoundaryKind.DoExpression                -> "do expr"
    | EvalBoundaryKind.OpenStatement ns            -> sprintf "open %s" ns
    | EvalBoundaryKind.HashDirective d             -> sprintf "#%s" (d.TrimStart('#').Trim())
    | EvalBoundaryKind.WholeFile path              -> Path.GetFileName path
    | EvalBoundaryKind.Unknown                     -> "?"

// ── FSI output parsing ────────────────────────────────────────────────────────

/// Find the index of the first ` = ` separator that lies outside all `<>` angle brackets
/// (i.e. at bracket-depth 0 in the type-signature portion).  Returns -1 if not found.
let private findEqAtDepthZero (s: string) : int =
  let mutable depth = 0
  let mutable i     = 0
  let mutable found = -1
  while i < s.Length - 2 && found = -1 do
    match s.[i] with
    | '<' -> depth <- depth + 1
    | '>' -> depth <- max 0 (depth - 1)
    | '=' when depth = 0 && i > 0 && s.[i - 1] = ' ' && s.[i + 1] = ' ' ->
      found <- i
    | _ -> ()
    i <- i + 1
  found

/// Parse a single FSI output line of the form `val [mutable] name : typeSig = value`.
/// Returns `None` for any line that is not a `val` binding declaration.
let parseFsiVal (line: string) : BindingValue option =
  let trimmed = line.Trim()
  match trimmed.StartsWith("val ") with
  | false -> None
  | true  ->
    let afterVal = trimmed.Substring(4) // strip "val "
    // Strip optional "mutable " keyword
    let afterMutable =
      match afterVal.StartsWith("mutable ") with
      | true  -> afterVal.Substring(8)
      | false -> afterVal
    // Find `: ` to locate the name/type boundary
    let colonIdx = afterMutable.IndexOf(": ", StringComparison.Ordinal)
    match colonIdx > 0 with
    | false -> None
    | true  ->
      let name        = afterMutable.Substring(0, colonIdx).Trim()
      let afterColon  = afterMutable.Substring(colonIdx + 2) // skip ": "
      // Find the first ` = ` at angle-bracket depth 0
      let eqIdx = findEqAtDepthZero afterColon
      match eqIdx with
      | -1 ->
        // No `= value` part — treat as type-only line (unusual but safe to skip)
        None
      | _  ->
        let typeSig      = afterColon.Substring(0, eqIdx).TrimEnd()
        let displayValue = afterColon.Substring(eqIdx + 2).Trim() // skip "= " (eqIdx points at '=')
        let isFun        = displayValue = "<fun>"
        let isTruncated  = displayValue.Contains("...") && not isFun
        Some {
          Name          = name
          TypeSig       = typeSig
          DisplayValue  = displayValue
          IsTruncated   = isTruncated
          IsFunctionValue = isFun
          CellIndex     = 0
          EvalDurationMs = 0.0
        }

/// Parse every `val …` line from a block of multiline FSI output.
let parseFsiBatch (fsiOutput: string) : BindingValue list =
  match String.IsNullOrWhiteSpace fsiOutput with
  | true -> []
  | false ->
    fsiOutput.Split('\n')
    |> Array.choose parseFsiVal
    |> Array.toList

// ── Boundary kind detection from submitted code ───────────────────────────────

/// Extract the first identifier after a keyword, stopping at `=`, `(`, whitespace-only.
let private firstIdentAfter (keyword: string) (code: string) : string =
  let rest = code.Substring(keyword.Length).TrimStart()
  // Stop at any of: space, '=', '(', ':', '\n', '<'
  let stop = rest |> Seq.tryFindIndex (fun c -> Char.IsWhiteSpace c || c = '=' || c = '(' || c = ':' || c = '<')
  match stop with
  | None   -> rest
  | Some i -> rest.Substring(0, i)

/// Determine what kind of F# code was submitted (from source text, not FSI output).
let detectBoundaryKind (code: string) : EvalBoundaryKind =
  let trimmed = code.Trim()
  match trimmed with
  | "" -> EvalBoundaryKind.Unknown
  | t when t.StartsWith("#") ->
    EvalBoundaryKind.HashDirective (t.Substring(1).Trim())
  | t when t.StartsWith("open ") ->
    EvalBoundaryKind.OpenStatement (t.Substring(5).Trim().Split('\n').[0].Trim())
  | t when t.StartsWith("type ") ->
    let name = firstIdentAfter "type " t
    EvalBoundaryKind.TypeOrModuleDefinition name
  | t when t.StartsWith("module ") ->
    let name = firstIdentAfter "module " t
    EvalBoundaryKind.TypeOrModuleDefinition name
  | t when t.StartsWith("let rec ") ->
    let name = firstIdentAfter "let rec " t
    EvalBoundaryKind.LetBinding name
  | t when t.StartsWith("let mutable ") ->
    let name = firstIdentAfter "let mutable " t
    EvalBoundaryKind.LetBinding name
  | t when t.StartsWith("let ") ->
    let name = firstIdentAfter "let " t
    EvalBoundaryKind.LetBinding name
  | t when t.StartsWith("do ") || not (t.StartsWith("let ") || t.StartsWith("type ") || t.StartsWith("module ") || t.StartsWith("open ") || t.StartsWith("#")) ->
    EvalBoundaryKind.DoExpression
  | _ ->
    EvalBoundaryKind.Unknown
