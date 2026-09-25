module SageFs.Tests.TranslationValidationTests

/// WHY — `type-migration-direction.md` step 5: translation validation. Not
/// "this transformation is always safe" but "THIS rewrite of THIS code is
/// equivalent", checked every time, failing closed.
///
/// Step 4 asks whether a BINDING may be rewritten; this asks whether the
/// rewrite of its USE SITES is sound. A binding can satisfy every side
/// condition and still be read in a loop, captured by a lambda, or rebound —
/// each of which changes meaning when the read becomes a cell access.
///
/// `Undecidable` is a distinct value on purpose: "we could not check" must
/// never be readable as a licence to proceed.

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.TranslationValidation

let private site name =
  { Name = name
    IsWrite = false
    InLambda = false
    InsideLoop = false
    Used = true }

[<Tests>]
let translationValidationTests =
  testList "translation validation" [

    testCase "WHY — a plain read is EQUIVALENT, and records why" <| fun _ ->
      match validateUseSite (site "tuned") with
      | Verdict.Equivalent because ->
        (because |> List.exists (fun r -> r.Contains "tuned")) |> Expect.isTrue "it names the binding"
      | _ -> failtest "a plain read must be equivalent"

    testCase "WHY — a read inside a loop is NOT equivalent: the rewrite changes how often it evaluates" <| fun _ ->
      match validateUseSite { (site "tuned") with InsideLoop = true } with
      | Verdict.NotEquivalent reasons ->
        (reasons |> List.exists (fun r -> r.Contains "loop")) |> Expect.isTrue "it names the reason"
      | _ -> failtest "a loop read must refuse"

    testCase "WHY — a lambda capture is NOT equivalent: the cell is shared where the value was copied" <| fun _ ->
      match validateUseSite { (site "tuned") with InLambda = true } with
      | Verdict.NotEquivalent reasons ->
        (reasons |> List.exists (fun r -> r.Contains "lambda")) |> Expect.isTrue "it names the reason"
      | _ -> failtest "a captured read must refuse"

    testCase "WHY — a write is NOT equivalent: a cell mutation is not a rebinding" <| fun _ ->
      match validateUseSite { (site "tuned") with IsWrite = true } with
      | Verdict.NotEquivalent reasons ->
        (reasons |> List.exists (fun r -> r.Contains "written")) |> Expect.isTrue "it names the reason"
      | _ -> failtest "a write must refuse"

    testCase "WHY — a never-read binding is NOT equivalent: a cell would preserve nothing" <| fun _ ->
      match validateUseSite { (site "unused") with Used = false } with
      | Verdict.NotEquivalent reasons ->
        (reasons |> List.exists (fun r -> r.Contains "never read")) |> Expect.isTrue "it names the reason"
      | _ -> failtest "an unread binding must refuse"

    testCase "WHY — EVERY failing condition is reported, so the message is actionable" <| fun _ ->
      match validateUseSite { (site "t") with IsWrite = true; InLambda = true; InsideLoop = true } with
      | Verdict.NotEquivalent reasons ->
        (reasons.Length > 2) |> Expect.isTrue "it must not stop at the first problem"
      | _ -> failtest "must refuse"

    testCase "WHY — the WHOLE rewrite is validated as a conjunction: one safe site does not license an unsafe one" <| fun _ ->
      let sites = [| site "a"; site "b"; { (site "c") with InsideLoop = true } |]
      match validateRewrite "config" sites with
      | Verdict.NotEquivalent reasons ->
        (reasons |> List.exists (fun r -> r.Contains "loop")) |> Expect.isTrue "the one bad site refuses the whole rewrite"
      | _ -> failtest "a single unsafe site must refuse the rewrite"

    testCase "WHY — an all-safe rewrite IS licensed, so the gate is not inert" <| fun _ ->
      match validateRewrite "config" [| site "a"; site "b" |] with
      | Verdict.Equivalent because ->
        (because |> List.exists (fun r -> r.Contains "2")) |> Expect.isTrue "it reports the site count"
      | _ -> failtest "an all-safe rewrite must be licensed"

    testCase "WHY — a rewrite with NO use sites is UNDECIDABLE, not vacuously safe" <| fun _ ->
      match validateRewrite "mystery" [||] with
      | Verdict.Undecidable reasons ->
        (reasons |> List.exists (fun r -> r.Contains "no use sites")) |> Expect.isTrue "it says what is missing"
      | _ -> failtest "no use sites must never be treated as equivalent"

    testCase "WHY — a caller with no evidence gets UNDECIDABLE, which is never a licence to proceed" <| fun _ ->
      match undecidable "tuned" "the typed tree did not resolve the read" with
      | Verdict.Undecidable _ -> ()
      | Verdict.Equivalent _ -> failtest "unknown must never produce Equivalent"
      | Verdict.NotEquivalent _ -> failtest "unknown is not a proven failure either; it is unknown"
  ]
