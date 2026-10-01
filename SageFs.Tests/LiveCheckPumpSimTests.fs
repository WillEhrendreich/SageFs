module SageFs.Tests.LiveCheckPumpSimTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Simulation
open SageFs.Simulation.LiveCheckPumpSim
open SageFs.Simulation.LiveCheckPumpInvariants

/// DST for the claim "an edit typed while the worker restarts still gets a verdict, and a worker that was not
/// there to answer never makes the code look broken". The fold under test is the real `LiveCheckPump.step`; see
/// `SageFs.Simulation/LiveCheckPumpSim.fs` for the world around it, the op order that serves as the scheduler,
/// and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Supersession=%A\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Supersession t.Scenario.Ops vs

let private effectsOf (t: Trace) : PumpEffect<Req, Verdict> list = t.Steps |> List.collect (fun s -> s.Effects)

let private deliveredFor (text: int) (t: Trace) : PumpOutcome<Verdict> list =
  effectsOf t
  |> List.choose (function
    | PumpEffect.Deliver (request, outcome) when request.Text = text -> Some outcome
    | _ -> None)

let private asks (t: Trace) : (Req * int) list =
  effectsOf t |> List.choose (function PumpEffect.Ask (request, pid) -> Some (request, pid) | _ -> None)

let private answered (verdict: Verdict) = PumpOutcome.Answered verdict

[<Tests>]
let liveCheckPumpSimTests =
  testList "DST live check pump" [

    testList "the real decision holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios — the newest edit is judged, no false blocked or clear, no stale apply, only a ready worker is asked, one call at a time, everything resolves"
        <| fun (seed: int) -> assertHolds (run (LiveCheckPumpGenerators.fromSeed seed))

      testCase "quiet: a request to a ready worker is asked once and answered truthfully" <| fun _ ->
        let t = run LiveCheckPumpGenerators.quiet
        asks t |> List.length |> Expect.equal "one ask" 1
        deliveredFor 1 t |> Expect.equal "answered clean" [ answered Verdict.Clean ]
        assertHolds t

      testCase "editWhileBuilding: texts typed with no proxy wait for the worker, and the newest is judged" <| fun _ ->
        let t = run LiveCheckPumpGenerators.editWhileBuilding
        asks t |> List.map (fun (r, pid) -> r.Text, pid) |> Expect.equal "only the newest is asked, of the replacement worker" [ 1, 1; 4, 2 ]
        deliveredFor 4 t |> Expect.equal "the newest text is answered, and it has errors" [ answered Verdict.Clean ]
        deliveredFor 2 t |> Expect.isEmpty "the text typed before it was replaced is never reported"
        assertHolds t

      testCase "editWhileWarming: a worker that has a proxy but is not Ready is not asked" <| fun _ ->
        let t = run LiveCheckPumpGenerators.editWhileWarming
        asks t |> List.map (fun (r, pid) -> r.Text, pid) |> Expect.equal "asked once, after it was Ready" [ 1, 2 ]
        deliveredFor 1 t |> Expect.equal "the verdict is the truthful one" [ answered Verdict.Clean ]
        assertHolds t

      testCase "retiredUnderTheRequest: the retired worker's answer counts for nothing, and the text is asked again" <| fun _ ->
        let t = run LiveCheckPumpGenerators.retiredUnderTheRequest
        asks t |> List.map (fun (r, pid) -> r.Text, pid) |> Expect.equal "asked of the first worker, then of the replacement" [ 1, 1; 1, 2 ]
        deliveredFor 1 t |> Expect.equal "answered once, truthfully" [ answered Verdict.Clean ]
        assertHolds t

      testCase "supersededWhileInFlight: the older text's answer is applied to nothing" <| fun _ ->
        let t = run LiveCheckPumpGenerators.supersededWhileInFlight
        deliveredFor 1 t |> Expect.isEmpty "nothing is reported for the text that was typed over"
        deliveredFor 2 t |> Expect.equal "the newest is answered" [ answered Verdict.Clean ]
        assertHolds t

      testCase "twoThatMustBothRun: under EveryOneRuns the second waits its turn and both are delivered" <| fun _ ->
        let t = run LiveCheckPumpGenerators.twoThatMustBothRun
        deliveredFor 1 t |> Expect.equal "the first" [ answered Verdict.Clean ]
        deliveredFor 2 t |> Expect.equal "the second" [ answered Verdict.Clean ]
        assertHolds t

      testCase "everyOneRunsAcrossARestart: every request is delivered, in the order made" <| fun _ ->
        let t = run LiveCheckPumpGenerators.everyOneRunsAcrossARestart
        t.Requests |> List.length |> Expect.equal "four requests" 4
        assertHolds t

      testCase "sessionFaults: the text ends unanswered because no worker is coming, and says why" <| fun _ ->
        let t = run LiveCheckPumpGenerators.sessionFaults
        match deliveredFor 1 t with
        | [ PumpOutcome.Unanswered (UnansweredWhy.WorkerGone reason) ] -> reason |> Expect.isNotEmpty "the reason is named"
        | other -> failtestf "expected one WorkerGone, got %A" other
        assertHolds t

      testCase "waitRunsOut: the wait ends at its deadline, unanswered, and says so" <| fun _ ->
        let t = run LiveCheckPumpGenerators.waitRunsOut
        deliveredFor 1 t |> Expect.equal "unanswered for lack of a worker in time" [ PumpOutcome.Unanswered UnansweredWhy.NoWorkerInTime ]
        asks t |> Expect.isEmpty "nothing was asked of a worker that was not there"
        assertHolds t

      testCase "staleWake: a deadline for a wait that already ended changes nothing" <| fun _ ->
        let t = run LiveCheckPumpGenerators.staleWake
        deliveredFor 1 t |> Expect.equal "answered once" [ answered Verdict.Clean ]
        assertHolds t

      testCase "restartTwice: a worker retired while warming does not strand the newest text" <| fun _ ->
        let t = run LiveCheckPumpGenerators.restartTwice
        deliveredFor 2 t |> Expect.equal "the newest is answered" [ answered Verdict.Clean ]
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED: the drops-what-it-cannot-ask twin loses the text typed while the worker restarts" <| fun _ ->
        let t = runDropsWhatItCannotAsk LiveCheckPumpGenerators.editWhileBuilding
        violations t |> List.map fst |> Expect.contains "newest-edit-always-judged must fire" "newest-edit-always-judged"

      testCase "REPRODUCED: the asks-any-proxy twin reports errors that a warming worker made up" <| fun _ ->
        let t = runAsksAnyProxy LiveCheckPumpGenerators.editWhileWarming
        violations t |> List.map fst |> Expect.contains "no-false-blocked must fire" "no-false-blocked"
        violations t |> List.map fst |> Expect.contains "asks-only-a-ready-worker must fire" "asks-only-a-ready-worker"

      testCase "REPRODUCED: the trusts-without-recheck twin believes a retired worker's silence" <| fun _ ->
        let t = runTrustsWithoutRecheck LiveCheckPumpGenerators.retiredUnderTheRequest
        violations t |> List.map fst |> Expect.contains "newest-edit-always-judged must fire" "newest-edit-always-judged"

      testCase "REPRODUCED: the applies-older-answers twin applies an answer for text that was typed over" <| fun _ ->
        let t = runAppliesOlderAnswers LiveCheckPumpGenerators.supersededWhileInFlight
        violations t |> List.map fst |> Expect.contains "no-stale-apply must fire" "no-stale-apply"

      testCase "REPRODUCED: the asks-while-in-flight twin puts a second call to the worker at once" <| fun _ ->
        let t = runAsksWhileInFlight LiveCheckPumpGenerators.twoThatMustBothRun
        violations t |> List.map fst |> Expect.contains "single-flight must fire" "single-flight"

      testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 400 ]
        let violatedBy runTwin (id: string) =
          seeds |> List.exists (fun seed -> violations (runTwin (LiveCheckPumpGenerators.fromSeed seed)) |> List.exists (fun (v, _) -> v = id))
        violatedBy runDropsWhatItCannotAsk "newest-edit-always-judged" |> Expect.isTrue "drops-what-it-cannot-ask is caught by a generated scenario"
        violatedBy runAsksAnyProxy "no-false-blocked" |> Expect.isTrue "asks-any-proxy is caught by a generated scenario"
        violatedBy runTrustsWithoutRecheck "newest-edit-always-judged" |> Expect.isTrue "trusts-without-recheck is caught by a generated scenario"
        violatedBy runAppliesOlderAnswers "no-stale-apply" |> Expect.isTrue "applies-older-answers is caught by a generated scenario"
        violatedBy runAsksWhileInFlight "single-flight" |> Expect.isTrue "asks-while-in-flight is caught by a generated scenario"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical steps (real decision)" <| fun (seed: int) ->
        let a = run (LiveCheckPumpGenerators.fromSeed seed)
        let b = run (LiveCheckPumpGenerators.fromSeed seed)
        a.Steps = b.Steps
    ]
  ]
