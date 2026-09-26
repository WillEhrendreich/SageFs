module SageFs.Tests.EditingRuleTests

/// WHY — `AGENTS.md` forbids `sed -i`, `python3 -c` rewrites and regex bulk
/// edits, and says a scripted edit MUST assert it matched. That is easy to
/// agree with and easy to break under time pressure, so it is stated here as
/// the set it is meant to be: a compliant editor REPORTS, and a bulk rewrite
/// cannot.
///
/// The properties, each of which is a way the failure actually presents:
///   1. a no-op is reported, so a silent rewrite never reads as success
///   2. an ambiguous find refuses, rather than hitting call sites unread
///   3. edits stop at the first refusal, so a tree is never left half-applied
///   4. the find text travels with the refusal, so the caller can see what it
///      asked for — which is all a caller has when a bulk rewrite silently
///      edited the wrong thing
///
/// The contract lives in `SageFs.Core` (a pure module, no test dependency);
/// the tests live here because Core does not reference Expecto.

open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let editingRuleTests =
  testList "the editing rule is a contract, not a preference" [

    testCase "WHY — a no-op is REPORTED, so a silent rewrite can never read as success" <| fun _ ->
      let outcome = SageFs.Contract.applyInOrder "let x = 1" [ ("let y = 2", "let y = 3") ]
      match outcome with
      | SageFs.EditOutcome.AllApplied _ -> failtest "a find that matched nothing must NOT report success"
      | SageFs.EditOutcome.SomeFailed (_, refused) ->
        // `refused` is a LIST of what would not apply, so the check is on the
        // first one — a caller is told the FIRST thing it got wrong, not a
        // summary that hides it.
        match refused with
        | SageFs.Applied.NothingMatched find :: _ -> (find = "let y = 2") |> Expect.isTrue "and it names what it looked for"
        | _ -> failtest "the refusal must be a named miss"

    testCase "WHY — an AMBIGUOUS find refuses, rather than rewriting every call site it did not read" <| fun _ ->
      let outcome = SageFs.Contract.applyInOrder "f a\nf b" [ ("f", "g") ]
      match outcome with
      | SageFs.EditOutcome.SomeFailed (_, refused) ->
        match refused with
        | SageFs.Applied.Ambiguous(find, n) :: _ ->
          (n = 2) |> Expect.isTrue "it counted the occurrences"
          (find = "f") |> Expect.isTrue "and it states the find text"
        | _ -> failtest "an ambiguous match must refuse, not guess"
      | SageFs.EditOutcome.AllApplied _ -> failtest "rewriting both call sites is the bug this prevents"

    testCase "WHY — edits stop at the first refusal, so the set is never left half-applied" <| fun _ ->
      let outcome = SageFs.Contract.applyInOrder "first" [ ("nope", "x"); ("first", "second") ]
      match outcome with
      | SageFs.EditOutcome.SomeFailed (applied, _) ->
        (List.isEmpty applied) |> Expect.isTrue "the second edit must not have run"
      | SageFs.EditOutcome.AllApplied _ -> failtest "stopping is the point"

    testCase "WHY — an exact, single match applies" <| fun _ ->
      let outcome = SageFs.Contract.applyInOrder "let x = 1" [ ("let x = 1", "let x = 2") ]
      match outcome with
      | SageFs.EditOutcome.AllApplied [ SageFs.Applied.MatchedOnce next ] -> (next = "let x = 2") |> Expect.isTrue "it applied"
      | _ -> failtest "an exact single match must apply"

    testCase "WHY — the find text travels with the refusal, so a caller can SEE what it asked for" <| fun _ ->
      let outcome = SageFs.Contract.applyInOrder "" [ ("the exact text", "x") ]
      match outcome with
      | SageFs.EditOutcome.SomeFailed (_, SageFs.Applied.NothingMatched find :: _) -> (find = "the exact text") |> Expect.isTrue "the find text is stated"
      | _ -> failtest "a miss must carry the find text"
  ]
