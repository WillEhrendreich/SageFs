module SageFs.Tests.TargetedVerificationScenarioTests

open Expecto
open Expecto.Flip
open SageFs.Features.Verification
open SageFs.WorkerProtocol

let private exact text =
  match ExactTestRef.create text with
  | Ok value -> value
  | Error err -> failtestf "expected exact ref, got error: %s" err

let private observation matches status loaded typeLoad =
  ({ MatchingSessionIds = matches
     SessionStatus = status
     LoadedState = loaded
     TypeIdentityDiagnostic = typeLoad } : SessionTrust.SessionObservation)

[<Tests>]
let tests =
  testList "Targeted verification scenarios" [
    testCase "when the user changes one behavior, SageFs verifies the behavior locally before it wakes the suite" <| fun _ ->
      let report =
        TargetedVerification.createReport
          { Intent = VerificationIntent.VerifyChangedBehavior ("UserPreferences.loadFromFile", RegressionRisk.LocalizedBehavior)
            NamedGuard = None
            SessionObservation = observation [ "session-1" ] (Some SessionStatus.Ready) None None
            LoadedState = LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2" }
          None
          None
      report.Plan
      |> Expect.equal
        "localized work should stay local first"
        (VerificationPlan.Perform VerificationMode.SnippetFirst)

    testCase "when source changed on disk but the live session is older, SageFs refuses to claim green" <| fun _ ->
      let report =
        TargetedVerification.createReport
          { Intent = VerificationIntent.ExploreBehavior "UserPreferences.loadFromFile"
            NamedGuard = None
            SessionObservation = observation [ "session-1" ] (Some SessionStatus.Ready) (Some (LoadedDefinitionState.ConfirmedStale ("disk-v2", "loaded-v1"))) None
            LoadedState = LoadedDefinitionState.ConfirmedStale ("disk-v2", "loaded-v1") }
          (Some (SnippetObservation.SnippetPassed "looks green"))
          None
      match report.Evidence with
      | Some (VerificationEvidence.Blocked (VerificationBlocker.SessionNotTrustworthy (SessionTrust.StaleDefinitions "disk-v2"))) -> ()
      | other -> failtestf "expected blocked stale trust evidence, got %A" other

    testCase "when type identity is compromised, SageFs stops verification and recommends recovery" <| fun _ ->
      let report =
        TargetedVerification.createReport
          { Intent = VerificationIntent.ExploreBehavior "UserPreferences.loadFromFile"
            NamedGuard = None
            SessionObservation = observation [ "session-1" ] (Some SessionStatus.Ready) None (Some "TypeLoadException")
            LoadedState = LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2" }
          None
          None
      let summary = TargetedVerification.summarize report
      summary |> Expect.stringContains "should recommend recovery" "Recover"

    testCase "when a named regression guard exists, SageFs plans local proof then the exact guard" <| fun _ ->
      let guard = exact "Tests.UserPreferences.guard"
      let report =
        TargetedVerification.createReport
          { Intent = VerificationIntent.VerifyChangedBehavior ("UserPreferences.loadFromFile", RegressionRisk.SharedContract)
            NamedGuard = Some guard
            SessionObservation = observation [ "session-1" ] (Some SessionStatus.Ready) None None
            LoadedState = LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2" }
          None
          None
      report.Plan
      |> Expect.equal
        "shared behavior with named guard should compose snippet and exact test"
        (VerificationPlan.Perform (VerificationMode.SnippetThenExactTest guard))
  ]
