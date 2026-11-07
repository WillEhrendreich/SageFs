module SageFs.Tests.DebugRecordSimTests

// DST and mutation tests for the daemon's half of debugging a test: a verdict must reach a row only for a hold the host ended
// with the test run under a debugger, once per ticket, and in the session whose host held the test. The simulation folds the
// real HeldTable, verdictFor and DebugRunRecord.plan over seeded and generated event lists. Each twin must break the
// invariant built to catch it, and each mutant of `plan` must be caught by what `plan` is for.

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting
open SageFs.Simulation
open SageFs.Simulation.DebugRecordSim
open SageFs.Simulation.DebugRecordSimInvariants
open MutationTestingFramework

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (behavior: Behavior) (scenario: Scenario) =
  match violations (trace behavior scenario) with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION, replay this scenario:\n  Seed=%d\n  Events=%A\n  Violations=%A" scenario.Seed scenario.Events vs

let private violatedIds (behavior: Behavior) (scenario: Scenario) : string list =
  violations (trace behavior scenario) |> List.map fst

/// Any event list at all is a scenario, so the interleavings are the generator's and not only the seed's.
let private genEvent : Gen<SimEvent> =
  gen {
    let! session = Gen.elements [ 0; 1 ]
    let! test = Gen.choose (0, 5)
    let! pick = Gen.choose (0, 1000)
    let! attached = Gen.elements [ true; false ]
    return!
      Gen.frequency
        [ 4, Gen.constant (SimEvent.Begin(session, test))
          4, Gen.constant (SimEvent.Release(session, attached))
          6, Gen.constant (SimEvent.Ask pick)
          2, Gen.constant (SimEvent.Expire session)
          2, Gen.constant (SimEvent.HostEnds session) ]
  }

type private Interleavings =
  static member Events() : Arbitrary<SimEvent list> = Gen.listOf genEvent |> Arb.fromGen

let private generated = { simConfig with arbitrary = [ typeof<Interleavings> ] }

// ---- the planner, as a value, so a mutant can replace it ------------------------------------------

type private Planner = DebugRunRecord -> DebugTicketLog -> (Set<TestId> * (LiveTestState -> LiveTestState) * DebugTicketLog) option

let private test : TestCase =
  { Id = TestId.create "suite/adds" TestFramework.Expecto
    FullName = "suite/adds"
    DisplayName = "suite/adds"
    NamePath = []
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private record : DebugRunRecord =
  match
    DebugRunRecord.create
      "s" "debug-1-1" test.Id test.FullName
      (TestResult.Failed(TestFailure.AssertionFailed "boom", FixtureDurations.slowResult))
      ResultProvenance.Evaluated
      DateTimeOffset.UtcNow
  with
  | Some record -> record
  | None -> failwith "a failed result is a verdict"

let private flakyWindowBefore : Map<TestId, ResultWindow> = LiveTestState.empty.FlakyHistory

/// What `plan` is for, as one question: does it record a ticket once, put the record's own provenance on the row, and leave
/// the flake window alone. True of the real planner and false of each mutant below.
let private behavesAsPlanned (planner: Planner) : bool =
  let fresh = { LiveTestState.empty with DiscoveredTests = [| test |] }
  match planner record DebugTicketLog.empty with
  | None -> false
  | Some (_, update, log) ->
    let after = update fresh
    let once = (planner record log).IsNone
    let provenance = Map.tryFind test.Id after.Provenances = Some ResultProvenance.Evaluated
    let flakeUntouched = after.FlakyHistory = flakyWindowBefore
    let rowHasVerdict = (Map.tryFind test.Id after.LastResults |> Option.map (fun r -> r.Result)) = Some record.Verdict.Result
    once && provenance && flakeUntouched && rowHasVerdict

let private ticketCheckRemoved : Mutant<Planner> =
  { Name = "plan without the ticket check"
    Description = "a repeated continue records the verdict again"
    Apply = fun real -> fun record _ -> real record DebugTicketLog.empty }

let private flakeSampleAdded : Mutant<Planner> =
  { Name = "plan that also records a flake sample"
    Description = "a test stopped at a breakpoint becomes a data point about stability"
    Apply =
      fun real ->
        fun record log ->
          real record log
          |> Option.map (fun (ids, update, log') ->
            ids,
            (fun state ->
              let updated = update state
              { updated with
                  FlakyHistory = FlakyDetection.recordResult record.Verdict.TestId record.Verdict.Result updated.FlakyHistory }),
            log') }

let private basisSwapped : Mutant<Planner> =
  { Name = "plan with the basis swapped"
    Description = "a compiled test is recorded as evaluated and the other way round"
    Apply =
      fun real ->
        fun record log ->
          let swapped =
            match record.CodeBasis with
            | ResultProvenance.Compiled -> ResultProvenance.Evaluated
            | _ -> ResultProvenance.Compiled
          real { record with CodeBasis = swapped } log }

[<Tests>]
let tests =
  testList "DST and mutation: a debug verdict reaches a row only once, only when it ran, only in its own session" [

    testList "the real daemon steps hold every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios: a verdict needs an attached end, a ticket records at most once, and a verdict lands in the session that held the test"
      <| fun (seed: int) -> assertHolds Behavior.Real (scenarioOf seed)

      testPropertyWithConfig generated "any interleaving of begins, releases, repeated continues, expiries and dying hosts records at most one verdict per ticket and none for a hold that never ran"
      <| fun (events: SimEvent list) -> assertHolds Behavior.Real { Seed = -1; Events = events @ [ for i in 0 .. 10 -> SimEvent.Ask i ] }

      testCase "a repeated continue records the verdict once" <| fun _ ->
        assertHolds Behavior.Real repeatedContinue
        (final Behavior.Real repeatedContinue).Landed.Length |> Expect.equal "one verdict for the ticket" 1

      testCase "a hold released with no debugger, one that expired, and a host that ended each record nothing" <| fun _ ->
        for scenario in [ releasedWithoutDebugger; expiredThenReleased; hostEndsHolding ] do
          assertHolds Behavior.Real scenario
          (final Behavior.Real scenario).Landed |> Expect.isEmpty "no verdict for a hold that never ran"

      testCase "a hold in the second session lands in the second session" <| fun _ ->
        assertHolds Behavior.Real secondSessionRuns
        match (final Behavior.Real secondSessionRuns).Landed with
        | [ landed ] -> landed.Session |> Expect.equal "the session that held the test" 1
        | other -> failtestf "expected one verdict, got %A" other
    ]

    testList "the twins reproduce the failures the invariants exist to catch (teeth)" [

      testCase "REPRODUCED: recording on every continue records a ticket again each time it is asked about" <| fun _ ->
        violatedIds Behavior.RecordsEveryContinueTwin repeatedContinue
        |> Expect.contains "a-ticket-records-at-most-once must fire" "a-ticket-records-at-most-once"

      testCase "REPRODUCED: recording a failure for a hold that never ran fabricates a verdict" <| fun _ ->
        violatedIds Behavior.FabricatesFailureTwin releasedWithoutDebugger
        |> Expect.contains "a-verdict-is-backed-by-an-attached-end must fire" "a-verdict-is-backed-by-an-attached-end"

      testCase "REPRODUCED: resolving the session by the daemon's default puts a verdict on another session's row" <| fun _ ->
        violatedIds Behavior.DefaultSessionTwin secondSessionRuns
        |> Expect.contains "a-verdict-lands-in-the-session-that-held-the-test must fire" "a-verdict-lands-in-the-session-that-held-the-test"

      testCase "the seeded scenarios catch every twin too, so the teeth are in the random space and not only in the hand-built shapes" <| fun _ ->
        let seeds = [ 1..400 ]
        let caught (behavior: Behavior) =
          seeds |> List.exists (fun seed -> not (List.isEmpty (violations (trace behavior (scenarioOf seed)))))
        caught Behavior.RecordsEveryContinueTwin |> Expect.isTrue "some seed catches the record-every-continue twin"
        caught Behavior.FabricatesFailureTwin |> Expect.isTrue "some seed catches the fabricating twin"
        caught Behavior.DefaultSessionTwin |> Expect.isTrue "some seed catches the default-session twin"
    ]

    testList "mutants of DebugRunRecord.plan are each caught" [

      testCase "the real planner passes the question the mutants are asked, so a caught mutant means something" <| fun _ ->
        behavesAsPlanned DebugRunRecord.plan |> Expect.isTrue "real plan behaves as planned"

      detectsMutant ticketCheckRemoved (DebugRunRecord.plan : Planner) behavesAsPlanned
      detectsMutant flakeSampleAdded (DebugRunRecord.plan : Planner) behavesAsPlanned
      detectsMutant basisSwapped (DebugRunRecord.plan : Planner) behavesAsPlanned
    ]

    testList "determinism and replay" [
      testProperty "the same seed gives the same final state" <| fun (seed: int) ->
        let a = final Behavior.Real (scenarioOf seed)
        let b = final Behavior.Real (scenarioOf seed)
        a.Landed = b.Landed && a.HostEnded = b.HostEnded && a.Began = b.Began
    ]
  ]
