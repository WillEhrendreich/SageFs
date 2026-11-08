module SageFs.Tests.EvalQueueSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation.EvalQueueSim

/// DST for the claim several callers of one session rely on: "a cancel only ever interrupts the eval of the request that asked
/// for it, and the host is killed only when that request's own eval is the one running and ignores the interrupt". The
/// decisions under test are the real `EvalInterrupts.planInterrupt` and `EvalInterrupts.graceExpired`; see
/// `SageFs.Simulation/EvalQueueSim.fs` for the host with its queue, the callers, virtual time and the two twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION (%s) — replay this scenario:\n  %A\n  Violations=%A" t.Reducer t.Scenario vs

let private ids (t: Trace) = violations t |> List.map (fun v -> v.Id)

let private how (t: Trace) (id: int) = t.Answers |> Map.tryFind id |> Option.map snd

[<Tests>]
let evalQueueSimTests =
  testList "DST eval queue" [

    testList "the real decisions hold the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios: a cancel reaches only its own eval, and a kill is only for the request's own ignored interrupt"
        <| fun (seed: int) -> assertHolds (run (fromSeed seed))

      testCase "B queued behind A's long legitimate eval, B's timeout fires: A finishes, B never ran, the host lives" <| fun _ ->
        let t = run queuedBehindLong
        assertHolds t
        how t 1 |> Expect.equal "A finished" (Some Answer.Finished)
        how t 2 |> Expect.equal "B never ran" (Some Answer.NeverRan)
        t.Kills |> Expect.isEmpty "no kill"

      testCase "a reply that lands as the grace ends has answered: the host is not killed and the queued eval runs" <| fun _ ->
        let t = run replyAsGraceEnds
        assertHolds t
        t.Kills |> Expect.isEmpty "no kill"
        how t 2 |> Expect.equal "the queued eval ran" (Some Answer.Finished)

      testCase "an eval that ignores its own caller's cancel gets the host killed, and the queue behind it is lost" <| fun _ ->
        let t = run ignorerKilled
        assertHolds t
        t.Kills |> List.map (fun k -> k.ForEval) |> Expect.equal "killed for the ignorer" [ 1 ]
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED: an interrupt with no request id lets B's timeout interrupt A's eval" <| fun _ ->
        ids (runNoRequestId queuedBehindLong) |> Expect.contains "CANCEL-ONLY-OWN must fire" "CANCEL-ONLY-OWN"

      testCase "REPRODUCED: a grace the reply does not disarm kills the host after the request answered" <| fun _ ->
        ids (runStaleGrace replyAsGraceEnds) |> Expect.contains "KILL-ONLY-FOR-OWN-IGNORER must fire" "KILL-ONLY-FOR-OWN-IGNORER"

      testCase "the invariants have teeth: a generated scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 400 ]
        let firesFor (twin: Scenario -> Trace) (id: string) = seeds |> List.exists (fun seed -> ids (twin (fromSeed seed)) |> List.contains id)
        firesFor runNoRequestId "CANCEL-ONLY-OWN" |> Expect.isTrue "no-request-id is caught"
        firesFor runStaleGrace "KILL-ONLY-FOR-OWN-IGNORER" |> Expect.isTrue "stale-grace is caught"
    ]

    testList "the host's rule for an interrupt" [
      testCase "the running eval is interrupted only by an interrupt that names it" <| fun _ ->
        SageFs.FsiHost.FsiProtocol.EvalInterrupts.planInterrupt (ValueSome 1L) [ 2L ] 1L |> Expect.equal "its own" SageFs.FsiHost.FsiProtocol.EvalInterrupts.Plan.InterruptRunning
        SageFs.FsiHost.FsiProtocol.EvalInterrupts.planInterrupt (ValueSome 1L) [ 2L ] 2L |> Expect.equal "a queued one is dropped, the running one left alone" SageFs.FsiHost.FsiProtocol.EvalInterrupts.Plan.DropQueued
        SageFs.FsiHost.FsiProtocol.EvalInterrupts.planInterrupt (ValueSome 1L) [ 2L ] 9L |> Expect.equal "an unknown one is nothing" SageFs.FsiHost.FsiProtocol.EvalInterrupts.Plan.NothingToInterrupt
        SageFs.FsiHost.FsiProtocol.EvalInterrupts.planInterrupt ValueNone [] 1L |> Expect.equal "nothing running, nothing queued" SageFs.FsiHost.FsiProtocol.EvalInterrupts.Plan.NothingToInterrupt

      testProperty "an interrupt never names the running eval's neighbour: whatever is asked, only the named id is ever acted on" <| fun (running: int) (queued: int list) (target: int) ->
        let plan = SageFs.FsiHost.FsiProtocol.EvalInterrupts.planInterrupt (ValueSome(int64 running)) (queued |> List.map int64) (int64 target)
        match plan with
        | SageFs.FsiHost.FsiProtocol.EvalInterrupts.Plan.InterruptRunning -> running = target
        | SageFs.FsiHost.FsiProtocol.EvalInterrupts.Plan.DropQueued -> running <> target && List.contains target queued
        | SageFs.FsiHost.FsiProtocol.EvalInterrupts.Plan.NothingToInterrupt -> running <> target && not (List.contains target queued)

      testCase "a grace keeps the host once the request has answered" <| fun _ ->
        SageFs.FsiHost.FsiProtocol.EvalInterrupts.graceExpired false |> Expect.equal "answered" SageFs.FsiHost.FsiProtocol.EvalInterrupts.GraceVerdict.KeepHost
        SageFs.FsiHost.FsiProtocol.EvalInterrupts.graceExpired true |> Expect.equal "still waiting" SageFs.FsiHost.FsiProtocol.EvalInterrupts.GraceVerdict.KillHost
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical trace" <| fun (seed: int) -> run (fromSeed seed) = run (fromSeed seed)
    ]
  ]
