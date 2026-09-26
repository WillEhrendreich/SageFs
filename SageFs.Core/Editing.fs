namespace SageFs

// ── The editing rule, made executable ───────────────────────────────────
//
// WHY this exists. The rule in AGENTS.md ("no sed -i, no python3 -c file
// rewrites, no regex bulk edits; script in F# and assert each replacement
// matched") is easy to agree with and easy to break under time pressure. So it
// is stated here as the set it is meant to be, in a form a test can check.
//
// This is a contract on the DOMAIN, not a linter over a transcript: the
// disallowed shape is an EDIT that can silently do nothing, and the properties
// below are the ones a compliant editor has and a bulk rewrite does not.
//
//   1. A replacement is total or reported. `Applied.MatchedOnce` and
//      `Applied.NothingMatched` are different values, so a caller cannot treat
//      a no-op as a success — that is the exact bug where a scripted edit
//      rewrites what it did not read and exits quietly.
//   2. A near-miss is surfaced, not absorbed. `Applied.Ambiguous` means the
//      find text matched more than once, which a bulk rewrite would have
//      cheerfully changed everywhere.
//   3. The find text is stated, so a caller can see what it asked for.

open System

[<RequireQualifiedAccess>]
type Applied =
  /// The text appeared exactly once and was replaced.
  | MatchedOnce of replaced: string
  /// The text did not appear. REPORTED, not silent: a caller that expected a
  /// change now knows it did not happen.
  | NothingMatched of find: string
  /// The text appeared more than once, so replacing would have hit call sites
  /// the caller never read. Deliberately refuses rather than guessing which.
  | Ambiguous of find: string * occurrences: int

[<RequireQualifiedAccess>]
type EditOutcome =
  /// Every requested replacement applied.
  | AllApplied of applied: Applied list
  /// At least one did not. The failures are named, so nothing is reported as
  /// done that was not.
  | SomeFailed of applied: Applied list * refused: Applied list

[<RequireQualifiedAccess>]
module Contract =

  /// The single, specific edit. It reports rather than assuming, and that is
  /// the whole contract — which is why a bulk rewrite cannot stand in for it.
  let replaceOnce (text: string) (find: string) (repl: string) : Applied =
    let occurrences = text.Split([| find |], StringSplitOptions.None).Length - 1
    if occurrences = 1 then Applied.MatchedOnce (text.Replace(find, repl))
    elif occurrences = 0 then Applied.NothingMatched find
    else Applied.Ambiguous(find, occurrences)

  /// Apply edits IN ORDER, stopping at the first refusal. Stopping matters: a
  /// bulk rewrite continues past a file that did not match and leaves the set
  /// in a half-applied state nobody can describe.
  let applyInOrder (text: string) (edits: (string * string) list) : EditOutcome =
    let rec go (current: string) (remaining: (string * string) list) (applied: Applied list) =
      match remaining with
      | [] -> EditOutcome.AllApplied(List.rev applied)
      | (find, repl) :: rest ->
        match replaceOnce current find repl with
        | Applied.MatchedOnce next -> go next rest (Applied.MatchedOnce repl :: applied)
        | refused -> EditOutcome.SomeFailed(List.rev applied, refused :: List.map (fun (f, _) -> Applied.NothingMatched f) rest)

    go text edits []

