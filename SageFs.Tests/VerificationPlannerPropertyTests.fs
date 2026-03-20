module SageFs.Tests.VerificationPlannerPropertyTests

open Expecto
open SageFs.Features.Verification
open SageFs.Tests.SharedGenerators

let private exact text =
  match ExactTestRef.create text with
  | Ok value -> value
  | Error err -> failwith err

let private untrustedSessions = [
    SessionTrust.Ambiguous [ "a"; "b" ]
    SessionTrust.WarmingUp "warming"
    SessionTrust.StaleDefinitions "UserPreferences.fs"
    SessionTrust.TypeIdentityCompromised "TypeLoadException"
    SessionTrust.Missing
  ]

[<Tests>]
let tests =
  testList "VerificationPlanner properties" [
    testPropertyWithConfig propConfig "planner never executes from an untrusted session" <| fun (subject: string) ->
      let subject = if System.String.IsNullOrWhiteSpace subject then "UserPreferences.loadFromFile" else subject
      untrustedSessions
      |> List.forall (fun sessionTrust ->
        let plan =
          VerificationPlanner.plan
            sessionTrust
            (LoadedDefinitionState.ConfirmedCurrent "artifact")
            None
            (VerificationIntent.ExploreBehavior subject)
        match plan with
        | VerificationPlan.Blocked (VerificationBlocker.SessionNotTrustworthy trust) -> trust = sessionTrust
        | _ -> false)

    testPropertyWithConfig propConfig "planner never executes when loaded definitions are not current" <| fun (subject: string) ->
      let subject = if System.String.IsNullOrWhiteSpace subject then "UserPreferences.loadFromFile" else subject
      let staleStates = [
        LoadedDefinitionState.ConfirmedStale ("disk", "loaded")
        LoadedDefinitionState.UnknownLoadState "not yet inspected"
      ]
      staleStates
      |> List.forall (fun loadState ->
        let plan =
          VerificationPlanner.plan
            (SessionTrust.Trusted "session-1")
            loadState
            None
            (VerificationIntent.ExploreBehavior subject)
        match plan with
        | VerificationPlan.Blocked (VerificationBlocker.LoadedCodeNotCurrent blockedState) -> blockedState = loadState
        | _ -> false)

    testPropertyWithConfig propConfig "exact test intent remains exact" <| fun (name: string) ->
      let name = if System.String.IsNullOrWhiteSpace name then "Tests.UserPreferences.guard" else name
      let exactName = exact name
      let plan =
        VerificationPlanner.plan
          (SessionTrust.Trusted "session-1")
          (LoadedDefinitionState.ConfirmedCurrent "artifact")
          None
          (VerificationIntent.RunExactTest exactName)
      match plan with
      | VerificationPlan.Perform (VerificationMode.ExactTestOnly resolved) -> resolved = exactName
      | _ -> false
  ]
