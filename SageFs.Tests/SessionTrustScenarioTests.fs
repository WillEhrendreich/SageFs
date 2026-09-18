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

    // A bounded settle-wait (e.g. the cohort landing gate's
    // awaitIntegrationSessionTrusted) must split a trust reading three ways:
    // proceed, keep waiting only for a genuinely transient warmup, or stop now
    // for a state that will never become ready by waiting — carrying a reason.
    testList "settle decision (transient vs terminal)" [
      testCase "Trusted -> Ready carrying the session id" <| fun _ ->
        SessionTrust.settleDecision (SessionTrust.Trusted "session-1")
        |> Expect.equal "a trusted session proceeds" (SessionTrust.SettleDecision.Ready "session-1")

      testCase "WarmingUp -> Retry (the only transient case)" <| fun _ ->
        SessionTrust.settleDecision (SessionTrust.WarmingUp "session-1")
        |> Expect.equal "a warming session is worth waiting on" SessionTrust.SettleDecision.Retry

      testCase "Unavailable (a Faulted/Stopped worker) -> Terminal, reason names status and session" <| fun _ ->
        match SessionTrust.settleDecision (SessionTrust.Unavailable ("session-1", "Faulted")) with
        | SessionTrust.SettleDecision.Terminal reason ->
          reason |> Expect.stringContains "reason names the terminal status" "Faulted"
          reason |> Expect.stringContains "reason names the session" "session-1"
        | other -> failtestf "a faulted session must be terminal, got %A" other

      testCase "Missing -> Terminal (nothing to wait for)" <| fun _ ->
        match SessionTrust.settleDecision SessionTrust.Missing with
        | SessionTrust.SettleDecision.Terminal _ -> ()
        | other -> failtestf "a missing session must be terminal, got %A" other

      testCase "Ambiguous -> Terminal (cannot verify against a guess)" <| fun _ ->
        match SessionTrust.settleDecision (SessionTrust.Ambiguous [ "a"; "b" ]) with
        | SessionTrust.SettleDecision.Terminal _ -> ()
        | other -> failtestf "ambiguity must be terminal, got %A" other

      testCase "StaleDefinitions -> Terminal (needs a rebuild, not a wait)" <| fun _ ->
        match SessionTrust.settleDecision (SessionTrust.StaleDefinitions "x.dll") with
        | SessionTrust.SettleDecision.Terminal _ -> ()
        | other -> failtestf "stale definitions must be terminal, got %A" other

      testCase "TypeIdentityCompromised -> Terminal" <| fun _ ->
        match SessionTrust.settleDecision (SessionTrust.TypeIdentityCompromised "bad") with
        | SessionTrust.SettleDecision.Terminal _ -> ()
        | other -> failtestf "compromised type identity must be terminal, got %A" other
    ]
  ]
