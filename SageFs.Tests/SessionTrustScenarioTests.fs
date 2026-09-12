module SageFs.Tests.SessionTrustScenarioTests

open Expecto
open Expecto.Flip
open SageFs.Features.Verification
open SageFs.WorkerProtocol

let private observation matches status loaded typeLoad =
  ({ MatchingSessionIds = matches
     SessionStatus = status
     LoadedState = loaded
     TypeIdentityDiagnostic = typeLoad } : SessionTrust.SessionObservation)

[<Tests>]
let tests =
  testList "Session trust scenarios" [
    testCase "a single ready pinned session is trustworthy" <| fun _ ->
      SessionTrust.classify (observation [ "session-1" ] (Some (SessionLifecycleStatus.Ready { Pid = 1; Port = None })) None None)
      |> Expect.equal "one ready session should be trusted" (SessionTrust.Trusted "session-1")

    testCase "multiple matching sessions are ambiguity, not a guess" <| fun _ ->
      SessionTrust.classify (observation [ "session-a"; "session-b" ] (Some (SessionLifecycleStatus.Ready { Pid = 1; Port = None })) None None)
      |> Expect.equal "many sessions should stay ambiguous" (SessionTrust.Ambiguous [ "session-a"; "session-b" ])

    testCase "warming session is not trusted evidence" <| fun _ ->
      SessionTrust.classify (observation [ "session-1" ] (Some (SessionLifecycleStatus.Starting { Pid = 1; Port = None })) None None)
      |> Expect.equal "warming should not be trusted" (SessionTrust.WarmingUp "session-1")

    testCase "stale loaded definitions outrank ready status" <| fun _ ->
      SessionTrust.classify (observation [ "session-1" ] (Some (SessionLifecycleStatus.Ready { Pid = 1; Port = None })) (Some (LoadedDefinitionState.ConfirmedStale ("disk-v2", "loaded-v1"))) None)
      |> Expect.equal "stale definitions should block trust" (SessionTrust.StaleDefinitions "disk-v2")

    testCase "type identity compromise outranks everything else" <| fun _ ->
      SessionTrust.classify (observation [ "session-1" ] (Some (SessionLifecycleStatus.Ready { Pid = 1; Port = None })) None (Some "TypeLoadException"))
      |> Expect.equal "type identity issues should dominate" (SessionTrust.TypeIdentityCompromised "TypeLoadException")
  ]
