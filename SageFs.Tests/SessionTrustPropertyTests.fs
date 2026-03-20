module SageFs.Tests.SessionTrustPropertyTests

open Expecto
open SageFs.Features.Verification
open SageFs.Tests.SharedGenerators
open SageFs.WorkerProtocol

let private observation matches status loaded typeLoad =
  ({ MatchingSessionIds = matches
     SessionStatus = status
     LoadedState = loaded
     TypeIdentityDiagnostic = typeLoad } : SessionTrust.SessionObservation)

[<Tests>]
let tests =
  testList "Session trust properties" [
    testPropertyWithConfig propConfig "trusted only arises from one ready unambiguous session" <| fun (sessionId: string) ->
      let sessionId =
        match System.String.IsNullOrWhiteSpace sessionId with
        | true -> "session-1"
        | false -> sessionId.Trim()
      match SessionTrust.classify (observation [ sessionId ] (Some SessionStatus.Ready) None None) with
      | SessionTrust.Trusted trustedId -> trustedId = sessionId
      | _ -> false

    testPropertyWithConfig propConfig "ambiguity is stable under permutation" <| fun () ->
      let left = SessionTrust.classify (observation [ "a"; "b"; "c" ] (Some SessionStatus.Ready) None None)
      let right = SessionTrust.classify (observation [ "c"; "a"; "b" ] (Some SessionStatus.Ready) None None)
      match left, right with
      | SessionTrust.Ambiguous xs, SessionTrust.Ambiguous ys -> Set.ofList xs = Set.ofList ys
      | _ -> false

    testPropertyWithConfig propConfig "stale load evidence can never classify as trusted" <| fun (sessionId: string) ->
      let sessionId =
        match System.String.IsNullOrWhiteSpace sessionId with
        | true -> "session-1"
        | false -> sessionId.Trim()
      match SessionTrust.classify (observation [ sessionId ] (Some SessionStatus.Ready) (Some (LoadedDefinitionState.ConfirmedStale ("disk", "loaded"))) None) with
      | SessionTrust.Trusted _ -> false
      | _ -> true
  ]
