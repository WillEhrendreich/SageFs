module SageFs.Tests.DebugRunRecordTests

// A debug run is a real run of the code, so its verdict belongs on the test's row. Everything about HOW it lands is decided
// by `DebugRunRecord` (pure) and the reducer arm that applies it: the row takes the verdict and where the code came from,
// the same ticket changes nothing twice, and the run is not a flake sample, not a line in the run summary, and not a
// discovery. These cases drive the real reducer with a session that has discovered tests.

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

let private mk name = mkTestCase name TestFramework.Expecto TestCategory.Unit

let private adds = mk "Suite/adds"
let private removes = mk "Suite/removes"
let private everyTest = [| adds; removes |]

let private step event (model: SageFsModel) : SageFsModel =
  SageFsUpdate.update (SageFsMsg.Event event) model |> fst

/// A session that has discovered both tests, each with a Passed row from an ordinary run.
let private started () : SageFsModel * string =
  let model, sid = withKnownSession "/work/proj" (SageFsModel.initial ())
  let enabled = model |> step TuiEvent.LiveTestingEnabled
  let discovered = enabled |> step (TuiEvent.LiveDiscoveryMerged (sid, everyTest))
  let ran =
    discovered
    |> step (TuiEvent.TestRunStarted (everyTest |> Array.map (fun t -> t.Id), Some sid))
    |> step (
      TuiEvent.TestResultsBatch (
        Some sid,
        everyTest |> Array.map (fun t -> mkResult t.Id (TestResult.Passed FixtureDurations.usualResult))))
    |> step (TuiEvent.TestRunCompleted (Some sid))
  ran, sid

let private failure : TestResult =
  TestResult.Failed (TestFailure.AssertionFailed "expected 3 but was 4", FixtureDurations.slowResult)

let private recordOf (sid: string) (ticket: string) (test: TestCase) (result: TestResult) (basis: ResultProvenance) : DebugRunRecord =
  match DebugRunRecord.create sid ticket test.Id test.FullName result basis DateTimeOffset.UtcNow with
  | Some record -> record
  | None -> failtestf "a %A result should be recordable" result

let private rowOf (model: SageFsModel) (sid: string) (test: TestCase) : TestStatusEntry =
  (SageFsModel.cycleForSession sid model).TestState.StatusIndex.Entries
  |> Array.find (fun e -> e.TestId = test.Id)

[<Tests>]
let debugRunRecordTests =
  testList "a debug run is recorded where every other run is" [

    testCase "WHY - a failed debug run turns a passed row into a failed one, from compiled code" <| fun _ ->
      let model, sid = started ()
      (rowOf model sid adds).Status |> function TestRunStatus.Passed _ -> () | other -> failtestf "setup: expected Passed, got %A" other
      let recorded = model |> step (TuiEvent.DebugRunFinished (recordOf sid "debug-1-1" adds failure ResultProvenance.Compiled))
      match (rowOf recorded sid adds).Status with
      | TestRunStatus.Failed (TestFailure.AssertionFailed message, _) -> message |> Expect.equal "the failure the debugger saw" "expected 3 but was 4"
      | other -> failtestf "expected the Failed row, got %A" other
      (rowOf recorded sid adds).Provenance |> Expect.equal "compiled" ResultProvenance.Compiled
      (rowOf recorded sid removes).Status
      |> function TestRunStatus.Passed _ -> () | other -> failtestf "the other test is untouched, got %A" other

    testCase "WHY - a test an eval defined records its verdict as Evaluated" <| fun _ ->
      let model, sid = started ()
      let recorded = model |> step (TuiEvent.DebugRunFinished (recordOf sid "debug-1-1" adds failure ResultProvenance.Evaluated))
      (rowOf recorded sid adds).Provenance |> Expect.equal "evaluated" ResultProvenance.Evaluated

    testCase "WHY - the same ticket twice changes nothing the second time, because a continue is safe to repeat" <| fun _ ->
      let model, sid = started ()
      let record = recordOf sid "debug-1-1" adds failure ResultProvenance.Compiled
      let once = model |> step (TuiEvent.DebugRunFinished record)
      let twice = once |> step (TuiEvent.DebugRunFinished record)
      obj.ReferenceEquals(SageFsModel.cycleForSession sid once, SageFsModel.cycleForSession sid twice)
      |> Expect.isTrue "the session's cycle came back as it was, by reference"

    testCase "WHY - a later debug run of the same test under a new ticket does record" <| fun _ ->
      let model, sid = started ()
      let failed = model |> step (TuiEvent.DebugRunFinished (recordOf sid "debug-1-1" adds failure ResultProvenance.Compiled))
      let fixedUp =
        failed |> step (TuiEvent.DebugRunFinished (recordOf sid "debug-1-2" adds (TestResult.Passed TimeSpan.Zero) ResultProvenance.Compiled))
      (rowOf fixedUp sid adds).Status |> function TestRunStatus.Passed _ -> () | other -> failtestf "expected Passed again, got %A" other

    testCase "WHY - a debug run is not a flake sample, a run-summary line or a discovery" <| fun _ ->
      let model, sid = started ()
      let before = SageFsModel.cycleForSession sid model
      let after = model |> step (TuiEvent.DebugRunFinished (recordOf sid "debug-1-1" adds failure ResultProvenance.Compiled))
      let cycle = SageFsModel.cycleForSession sid after
      cycle.TestState.FlakyHistory |> Expect.equal "the flake window is untouched" before.TestState.FlakyHistory
      cycle.TestState.DiscoveryGeneration |> Expect.equal "not a discovery" before.TestState.DiscoveryGeneration
      after.PendingRunSummary |> Expect.equal "no 'N passed' line will appear later under an unrelated run" model.PendingRunSummary
      after.QuarantinedTests |> Expect.equal "no quarantine decision" model.QuarantinedTests

    testCase "WHY - the row shows no duration for a debug verdict, because the host's clock ran through every breakpoint" <| fun _ ->
      let model, sid = started ()
      let after = model |> step (TuiEvent.DebugRunFinished (recordOf sid "debug-1-1" adds failure ResultProvenance.Compiled))
      match (rowOf after sid adds).Status with
      | TestRunStatus.Failed (_, duration) -> duration |> Expect.equal "no measured time" TimeSpan.Zero
      | other -> failtestf "expected Failed, got %A" other

    testCase "WHY - a result that says the test did not run is not a verdict and is not recorded" <| fun _ ->
      for result in [ TestResult.NotRun; TestResult.NoResult NoResultReason.RunCancelled ] do
        DebugRunRecord.create "s" "t" adds.Id adds.FullName result ResultProvenance.Compiled DateTimeOffset.UtcNow
        |> Expect.isNone (sprintf "%A fabricates nothing" result)

    testCase "WHY - the record lands in the session that held the test, never another" <| fun _ ->
      let model, sid = started ()
      let other, otherSid = withKnownSession "/work/other" model
      let other' = other |> step (TuiEvent.LiveDiscoveryMerged (otherSid, everyTest))
      let after = other' |> step (TuiEvent.DebugRunFinished (recordOf otherSid "debug-1-1" adds failure ResultProvenance.Compiled))
      (rowOf after sid adds).Status
      |> function TestRunStatus.Passed _ -> () | status -> failtestf "the first session's row moved: %A" status
      (rowOf after otherSid adds).Status
      |> function TestRunStatus.Failed _ -> () | status -> failtestf "the held session's row should be Failed, got %A" status

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 } "WHY - however tickets repeat, a ticket is recorded at most once and the log stays bounded"
    <| fun (tickets: PositiveInt list) ->
      let names = tickets |> List.map (fun (PositiveInt n) -> sprintf "debug-%d" (n % 200))
      let model, sid = started ()
      let applied =
        names
        |> List.fold (fun m name -> m |> step (TuiEvent.DebugRunFinished (recordOf sid name adds failure ResultProvenance.Compiled))) model
      let cycle = SageFsModel.cycleForSession sid applied
      let remembered = DebugTicketLog.toList cycle.DebugTickets
      remembered |> List.distinct |> List.length = remembered.Length
      && remembered.Length <= DebugTicketLog.capacity
  ]
