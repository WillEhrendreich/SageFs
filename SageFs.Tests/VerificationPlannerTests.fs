module SageFs.Tests.VerificationPlannerTests

open Expecto
open Expecto.Flip
open SageFs.Features.Verification

let private exact text =
  match ExactTestRef.create text with
  | Ok value -> value
  | Error err -> failtestf "expected exact test ref, got error: %s" err

[<Tests>]
let tests =
  testList "VerificationPlanner" [
    testCase "when a developer changes one local behavior, SageFs verifies it locally first" <| fun _ ->
      let plan =
        VerificationPlanner.plan
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2")
          None
          (VerificationIntent.VerifyChangedBehavior ("UserPreferences.loadFromFile", RegressionRisk.LocalizedBehavior))
      plan
      |> Expect.equal
        "localized changes should stay snippet-first"
        (VerificationPlan.Perform VerificationMode.SnippetFirst)

    testCase "when a changed behavior has a named guard, SageFs proves the behavior then the guard" <| fun _ ->
      let guard = exact "Tests.UserPreferences.loadFromFile returns error for missing directory"
      let plan =
        VerificationPlanner.plan
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2")
          (Some guard)
          (VerificationIntent.VerifyChangedBehavior ("UserPreferences.loadFromFile", RegressionRisk.SharedContract))
      plan
      |> Expect.equal
        "named guards should produce snippet-then-test verification"
        (VerificationPlan.Perform (VerificationMode.SnippetThenExactTest guard))

    testCase "when the user asks for one exact regression guard, SageFs runs exactly that guard" <| fun _ ->
      let guard = exact "Tests.UserPreferences.loadFromFile returns error for missing directory"
      let plan =
        VerificationPlanner.plan
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2")
          None
          (VerificationIntent.RunExactTest guard)
      plan
      |> Expect.equal
        "exact test intent should not broaden scope"
        (VerificationPlan.Perform (VerificationMode.ExactTestOnly guard))

    testCase "when the session is ambiguous, SageFs refuses to guess which result proves the behavior" <| fun _ ->
      let plan =
        VerificationPlanner.plan
          (SessionTrust.Ambiguous [ "session-a"; "session-b" ])
          (LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs@v2")
          None
          (VerificationIntent.ExploreBehavior "UserPreferences.loadFromFile")
      plan
      |> Expect.equal
        "ambiguous sessions must block verification"
        (VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy (SessionTrust.Ambiguous [ "session-a"; "session-b" ])))

    testCase "when the loaded definitions are stale, SageFs refuses to claim green" <| fun _ ->
      let plan =
        VerificationPlanner.plan
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedStale ("UserPreferences.fs@disk-v2", "UserPreferences.fs@loaded-v1"))
          None
          (VerificationIntent.ExploreBehavior "UserPreferences.loadFromFile")
      plan
      |> Expect.equal
        "stale loaded code must block verification"
        (VerificationPlan.Blocked (VerificationBlocker.LoadedCodeNotCurrent (LoadedDefinitionState.ConfirmedStale ("UserPreferences.fs@disk-v2", "UserPreferences.fs@loaded-v1"))))
  ]
