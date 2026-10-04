/// Deterministic simulation of the nudge door: seeded schedules of nudges, undos,
/// redos, inspects, outside edits and reformats, each of which may crash or fail
/// at any disk step, folded through the REAL `Nudge.runWith` on an in-memory
/// disk. Four twins (a write in place, a write before its record, a skipped hash
/// check, a crash never settled) show each invariant is able to fail.
///
/// To replay a failing seed: `NudgeSim.realTrace (NudgeSim.scenarioOf <seed>)`.
module SageFs.Tests.NudgeSimDstTests

open Expecto
open Expecto.Flip
open SageFs.Features.Tweak
open SageFs.Features.Tweak.Nudge
open SageFs.Simulation
open SageFs.Simulation.NudgeSim

let seeds = [ 1 .. 500 ]

let describe (scenario: Scenario) =
  sprintf "seed=%d family=%A\n  %s" scenario.Seed scenario.Family (scenario.Steps |> List.map (fun s -> sprintf "%A faults=%A" s.Op s.Faults) |> String.concat "\n  ")

let violationsOf (name: string) (strategy: Strategy) (policy: SeenPolicy) : (Scenario * NudgeSimInvariants.Violation list) list =
  seeds
  |> List.choose (fun seed ->
    let scenario = scenarioOf seed
    let trace = trace strategy policy scenario
    match NudgeSimInvariants.all scenario trace |> List.tryFind (fun (n, vs) -> n = name && not (List.isEmpty vs)) with
    | Some(_, vs) -> Some(scenario, vs)
    | None -> None)

let expectNone (name: string) (bad: (Scenario * NudgeSimInvariants.Violation list) list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" name bad.Length seeds.Length
      (bad
       |> List.truncate 3
       |> List.map (fun (s, vs) -> sprintf "%s\n  => %s" (describe s) (vs |> List.truncate 2 |> List.map (fun v -> sprintf "[%d] %s" v.Index v.Why) |> String.concat "\n     "))
       |> String.concat "\n\n")

let expectSome (name: string) (bad: (Scenario * NudgeSimInvariants.Violation list) list) =
  bad |> List.isEmpty |> Expect.isFalse (sprintf "the twin must break %s on some seed, or the invariant cannot fail" name)

let writeInPlaceTwin = { production with Replace = NudgeIo.AtomicWrite.writeInPlaceTwin }
let fileFirstTwin = { production with Order = StepOrder.FileThenJournalTwin }
let noSettleTwin = { production with Settle = fun _ _ -> Reconciliation.Consistent }

let signature (trace: Trace) =
  trace.Observations
  |> List.map (fun o -> o.FileAfter, o.JournalAfter, sprintf "%A" o.Result)
  |> fun steps -> steps, trace.AfterRecovery, sprintf "%A" trace.Recovery

[<Tests>]
let nudgeSimDstTests =
  testList "Nudge DST" [

    testCase "WHY - the scenarios are deterministic: the same seed gives the same trace, byte for byte" <| fun _ ->
      for seed in [ 1; 2; 42; 99; 500 ] do
        signature (realTrace (scenarioOf seed)) |> Expect.equal (sprintf "seed %d replays" seed) (signature (realTrace (scenarioOf seed)))

    testCase "WHY - the schedules actually exercise the door: crashes, landed writes, undos, refusals and torn tails all occur" <| fun _ ->
      let traces = seeds |> List.map (fun s -> realTrace (scenarioOf s))
      let results = traces |> List.collect (fun t -> t.Observations |> List.map (fun o -> o.Result))
      let count pick = results |> List.filter pick |> List.length
      count (function StepResult.Crashed _ -> true | _ -> false) |> fun n -> (n, 100) |> Expect.isGreaterThan "crashes happen"
      count (function StepResult.Returned(Ok { Outcome = NudgeOutcome.Written _ }) -> true | _ -> false) |> fun n -> (n, 200) |> Expect.isGreaterThan "writes land"
      count (function StepResult.Returned(Ok { Outcome = NudgeOutcome.Undone _ }) -> true | _ -> false) |> fun n -> (n, 50) |> Expect.isGreaterThan "undos land"
      count (function StepResult.Returned(Error(NudgeRefusal.SourceMoved _)) -> true | _ -> false) |> fun n -> (n, 20) |> Expect.isGreaterThan "stale nudges are refused"
      count (function StepResult.Returned(Error(NudgeRefusal.WriteFailed _)) -> true | _ -> false) |> fun n -> (n, 5) |> Expect.isGreaterThan "failed replaces happen"
      count (function StepResult.Returned(Ok { Notes = notes }) -> List.contains RunNote.TornJournalTailRemoved notes | _ -> false)
      |> fun n -> (n, 5) |> Expect.isGreaterThan "torn journal tails are healed"
      count (function StepResult.Returned(Ok { Notes = notes }) -> notes |> List.exists (function RunNote.UnlandedWriteMarkedUndone _ -> true | _ -> false) | _ -> false)
      |> fun n -> (n, 5) |> Expect.isGreaterThan "unlanded writes are settled"

    testList "the real door holds every invariant" [
      for (name, _) in NudgeSimInvariants.named ->
        testCase (sprintf "%s across seeded schedules" name) <| fun _ ->
          violationsOf name production SeenPolicy.AsGiven |> expectNone name
    ]

    testList "each twin breaks the invariant it exists to break" [
      testCase "a write in place is torn by a crash: FILE-NEVER-PARTIAL fails" <| fun _ ->
        violationsOf "FILE-NEVER-PARTIAL" writeInPlaceTwin SeenPolicy.AsGiven |> expectSome "FILE-NEVER-PARTIAL"

      testCase "a file replaced before its record exists leaves a change nothing can undo: FILE-CHANGE-IS-JOURNALED-FIRST fails" <| fun _ ->
        violationsOf "FILE-CHANGE-IS-JOURNALED-FIRST" fileFirstTwin SeenPolicy.AsGiven |> expectSome "FILE-CHANGE-IS-JOURNALED-FIRST"

      testCase "a door that skips the hash check overwrites a changed expression: STALE-SEEN-NEVER-WRITES fails" <| fun _ ->
        violationsOf "STALE-SEEN-NEVER-WRITES" production SeenPolicy.SkipCheckTwin |> expectSome "STALE-SEEN-NEVER-WRITES"

      testCase "a crash that is never settled leaves history that cannot be undone: RECOVERY-RESTORES-THE-ORIGINAL fails" <| fun _ ->
        violationsOf "RECOVERY-RESTORES-THE-ORIGINAL" noSettleTwin SeenPolicy.AsGiven |> expectSome "RECOVERY-RESTORES-THE-ORIGINAL"
    ]
  ]
