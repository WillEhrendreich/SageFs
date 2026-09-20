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

    testCase "a report with no evidence must say so, not a plan sentence" <| fun _ ->
      let request : TargetedVerificationRequest =
        { Intent = VerificationIntent.ExploreBehavior "behavior"
          NamedGuard = None
          SessionObservation =
            { MatchingSessionIds = [ "session-1" ]
              SessionStatus = Some (SageFs.WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1234; Port = Some 5000 })
              LoadedState = Some (LoadedDefinitionState.ConfirmedCurrent "artifact-v1")
              TypeIdentityDiagnostic = None }
          LoadedState = LoadedDefinitionState.ConfirmedCurrent "artifact-v1" }
      let report = TargetedVerification.createReport request None None
      TargetedVerification.summarize report
      |> Expect.stringContains "a report with no evidence must say so" "No snippet or exact-test evidence"

    testCase "conflicting snippet and exact-test evidence is reported as blocked, not as a plan" <| fun _ ->
      let guard = exact "Tests.UserPreferences.guard"
      let report =
        { Trust = SessionTrust.Trusted "session-1"
          Plan = VerificationPlan.Perform (VerificationMode.SnippetThenExactTest guard)
          Evidence = Some (VerificationEvidence.Blocked (VerificationBlocker.ConflictingEvidence "snippet passed but the exact test failed")) }
      TargetedVerification.summarize report
      |> Expect.stringContains "should say the evidence conflicts, not restate the plan" "disagree"
  ]
