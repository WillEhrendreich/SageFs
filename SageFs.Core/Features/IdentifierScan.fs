/// Exact, regex-free identifier matching for eval-history analysis.
///
/// The binding explorer asks "does `\bNAME\b` match this cell's source?" and
/// the dependency graph asks "does `(?<![.\w])NAME(?![.\w])` match?". Both
/// used to build one Regex per name per cell on every eval. These functions
/// answer the same questions with identical results (proven against Regex by
/// property tests and an exhaustive character-class test), without compiling
/// anything, and expose each source's identifier tokens so callers can index
/// a cell ONCE and turn every later reference check into a hash probe.
module SageFs.Features.IdentifierScan

open System
open System.Collections.Generic
open System.Globalization

/// Regex `\w`: letters, non-spacing marks, decimal digits and connector
/// punctuation (the .NET definition, not ECMAScript's `[A-Za-z0-9_]`).
let isRegexWordChar (c: char) : bool =
  match Char.GetUnicodeCategory c with
  | UnicodeCategory.UppercaseLetter
  | UnicodeCategory.LowercaseLetter
  | UnicodeCategory.TitlecaseLetter
  | UnicodeCategory.ModifierLetter
  | UnicodeCategory.OtherLetter
  | UnicodeCategory.NonSpacingMark
  | UnicodeCategory.DecimalDigitNumber
  | UnicodeCategory.ConnectorPunctuation -> true
  | _ -> false

/// What Regex `\b` counts as a word character: `\w` plus the zero-width
/// non-joiner and joiner (U+200C, U+200D), per UTS#18 simple word boundaries.
let isBoundaryWordChar (c: char) : bool =
  isRegexWordChar c || c = '\u200C' || c = '\u200D'

let private boundaryAt (source: string) (i: int) =
  let before = i > 0 && isBoundaryWordChar source.[i - 1]
  let after = i < source.Length && isBoundaryWordChar source.[i]
  before <> after

/// Exactly `Regex.IsMatch(source, @"\b" + Regex.Escape name + @"\b")`.
let occursWordBounded (name: string) (source: string) : bool =
  let rec from start =
    match source.IndexOf(name, start, StringComparison.Ordinal) with
    | -1 -> false
    | p when boundaryAt source p && boundaryAt source (p + name.Length) -> true
    | p when p >= source.Length -> false
    | p -> from (p + 1)
  from 0

let private freeEdge (source: string) (i: int) =
  i < 0 || i >= source.Length || (source.[i] <> '.' && not (isRegexWordChar source.[i]))

/// Exactly `Regex.IsMatch(source, @"(?<![.\w])" + Regex.Escape name + @"(?![.\w])")`:
/// NAME appears with neither a word character nor a '.' on either side, so
/// `Foo.name`, `name.Length` and `longname` do not count.
let occursAsFreeIdentifier (name: string) (source: string) : bool =
  let rec from start =
    match source.IndexOf(name, start, StringComparison.Ordinal) with
    | -1 -> false
    | p when freeEdge source (p - 1) && freeEdge source (p + name.Length) -> true
    | p when p >= source.Length -> false
    | p -> from (p + 1)
  from 0

let private distinctRuns (isWord: char -> bool) (keep: string -> int -> int -> bool) (source: string) : string[] =
  let seen = HashSet<string>(StringComparer.Ordinal)
  let mutable i = 0
  while i < source.Length do
    match isWord source.[i] with
    | true ->
      let start = i
      while i < source.Length && isWord source.[i] do
        i <- i + 1
      match keep source start i with
      | true -> seen.Add(source.Substring(start, i - start)) |> ignore
      | false -> ()
    | false -> i <- i + 1
  let runs = Array.zeroCreate seen.Count
  seen.CopyTo runs
  runs

/// Distinct maximal runs of `\b`-word characters. For a name made only of
/// such characters, `occursWordBounded name source` holds exactly when the
/// name is one of these runs.
let boundaryWords (source: string) : string[] =
  distinctRuns isBoundaryWordChar (fun _ _ _ -> true) source

/// Distinct maximal runs of `\w` characters with no '.' immediately before or
/// after. For a name made only of `\w` characters, `occursAsFreeIdentifier
/// name source` holds exactly when the name is one of these runs.
let freeIdentifiers (source: string) : string[] =
  distinctRuns isRegexWordChar (fun s start stop -> freeEdge s (start - 1) && freeEdge s stop) source

/// A name that `boundaryWords` can answer for (non-empty, all `\b`-word chars).
let isBoundaryWordToken (name: string) : bool =
  name.Length > 0 && name |> Seq.forall isBoundaryWordChar

/// A name that `freeIdentifiers` can answer for (non-empty, all `\w` chars).
let isFreeIdentifierToken (name: string) : bool =
  name.Length > 0 && name |> Seq.forall isRegexWordChar
