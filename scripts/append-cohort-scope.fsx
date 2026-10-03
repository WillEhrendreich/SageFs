#!/usr/bin/env dotnet fsi
/// Append the cohort scope to every `CohortCommand` construction in one test file, and
/// ASSERT every replacement matched.
///
/// WHY A SCRIPT AND NOT sed. A silent no-op edit looks exactly like a successful one, which is
/// how a bulk edit half-applies and leaves sites untouched while the run reports success. Every
/// pattern here is counted BEFORE and AFTER, and the script exits non-zero if any count moved in
/// a way it did not intend — so "it ran" is not the same claim as "it applied".

open System
open System.IO

let file = fsi.CommandLineArgs.[1]

/// `CohortCommand.Name(...)` -> `CohortCommand.Name(..., CohortScope.Machine)`.
/// Matches only the CLOSING paren of the call, by counting parens from the opening one, so a
/// nested `CohortCommand.X(...)` inside the arguments is not mangled.
let closeParenIndex (text: string) (start: int) : int =
  let mutable depth = 0
  let mutable i = start
  let mutable found = -1
  while i < text.Length && found < 0 do
    match text.[i] with
    | '(' -> depth <- depth + 1
    | ')' ->
      depth <- depth - 1
      if depth = 0 then found <- i
    | _ -> ()
    i <- i + 1
  found

let before = File.ReadAllText file

let mutable text = ref before
let mutable applied = 0

// The commands whose arity changed. The scope goes LAST on every one of them.
let shapes =
  [ "Join"
    "Depart"
    "AcquireClaim"
    "ReleaseClaim"
    "ReassignClaim"
    "RequestLanding"
    "RenewLease"
    "ObserveSave"
    "SetIntegrationHead"
    "DelegateConductor"
    "VetoLanding"
    "ResolveVeto"
    "VerifySave"
    "RebaseCompleted"
    "AffectedComputed"
    "TestsCompleted"
    "VerificationInconclusive"
    "FastForwardCompleted"
    "FastForwardFailed" ]

for name in shapes do
  let needle = sprintf "CohortCommand.%s(" name
  let mutable at = 0
  let mutable searching = true
  while searching do
    let found = (!text).IndexOf(needle, at, StringComparison.Ordinal)
    if found < 0 then searching <- false
    else
      let openIdx = found + needle.Length - 1
      let closeIdx = closeParenIndex !text openIdx
      if closeIdx < 0 then
        printfn "UNBALANCED: %s at offset %d has no closing paren" name found
        exit 2
      else
        let inner = (!text).Substring(openIdx + 1, closeIdx - openIdx - 1)
        // Already migrated: the scope is the last argument.
        let alreadyHasScope =
          inner.Contains "CohortScope.Machine"
          || inner.Contains "CohortScope.Repository"
          || inner.Contains "CohortScope.Named"
        if alreadyHasScope then at <- closeIdx + 1
        else
          let replacement =
            if String.IsNullOrWhiteSpace inner then
              sprintf "CohortScope.Machine"
            else
              sprintf "%s, CohortScope.Machine" (inner.TrimEnd())
          let builder = new System.Text.StringBuilder(!text)
          builder.Remove(openIdx + 1, closeIdx - openIdx - 1) |> ignore
          builder.Insert(openIdx + 1, replacement) |> ignore
          text := builder.ToString()
          at <- openIdx + 1 + replacement.Length
          applied <- applied + 1

printfn "%s: %d replacement(s) applied" file applied

if applied = 0 then
  printfn "NOTHING CHANGED — the file may already be migrated, or the pattern missed."
  exit 3

if !text = before then
  printfn "BUG: reported %d replacements but the text is identical" applied
  exit 4

File.WriteAllText(file, !text)
printfn "written"