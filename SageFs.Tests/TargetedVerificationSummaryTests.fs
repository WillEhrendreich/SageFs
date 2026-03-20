module SageFs.Tests.TargetedVerificationSummaryTests

open Expecto
open Expecto.Flip
open SageFs.Features.Verification

let private exact text =
  match ExactTestRef.create text with
  | Ok value -> value
  | Error err -> failtestf "expected exact ref, got error: %s" err

[<Tests>]
let tests =
  testList "Targeted verification summary" [
    testCase "ambiguous trust explains why SageFs refuses to guess" <| fun _ ->
      let report =
        { Trust = SessionTrust.Ambiguous [ "a"; "b" ]
          Plan = VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy (SessionTrust.Ambiguous [ "a"; "b" ]))
          Evidence = None }
      TargetedVerification.summarize report
      |> Expect.stringContains "should mention pinning one session" "Pin one session"

    testCase "snippet then exact summary explains the two-step proof" <| fun _ ->
      let guard = exact "Tests.UserPreferences.guard"
      let report =
        { Trust = SessionTrust.Trusted "session-1"
          Plan = VerificationPlan.Perform (VerificationMode.SnippetThenExactTest guard)
          Evidence = None }
      TargetedVerification.summarize report
      |> Expect.stringContains "should mention local proof first" "prove the behavior locally first"

    testCase "unknown loaded state explains why proof is blocked" <| fun _ ->
      let report =
        { Trust = SessionTrust.Trusted "session-1"
          Plan = VerificationPlan.Blocked (VerificationBlocker.LoadedCodeNotCurrent (LoadedDefinitionState.UnknownLoadState "warmup file status unavailable"))
          Evidence = None }
      TargetedVerification.summarize report
      |> Expect.stringContains "should mention inability to prove loaded code" "cannot prove"
  ]
