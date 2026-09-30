/// `run_tests` is one more door into the live-testing engine, not a second runner.
/// What it hands back is a receipt, and the receipt is a pure read of the engine's
/// own durable record of that request (`LiveTestState.RunRequests`,
/// `ResultGenerations`, `LastResults`), so it cannot disagree with `list_tests`,
/// the dashboard, or the cohort landing gate, which read the same state.
///
/// The rule the receipt exists to keep: it never says "passed" for a test that
/// this run did not itself pass. A pass left over from an earlier run, a test that
/// never reported, a skip, all read as "did not pass here", and each says why.
module SageFs.Tests.TestRunReceiptTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features
open SageFs.Features.RunReceipts
open SageFs.Features.Verification
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

let private session = "s1"
let private framework = TestFramework.Expecto
let private idOf name = TestId.create name framework
let private caseOf name = mkTestCase name framework TestCategory.Unit

/// How one requested test turned out, as data, so a fixture can be built for any mix.
type private Outcome =
  | PassedHere
  | PassedEarlier
  | FailedHere
  | SkippedHere
  | NoResultHere
  | Silent

let private requestId = RunRequestId.fresh ()

/// A state in which `names` were requested in one run, the run completed, and each
/// test did what `outcomes` says. Built with the engine's own transitions, so the
/// fixture cannot drift from the real lifecycle.
let private finishedState (outcomes: (string * Outcome) list) : LiveTestState =
  let ids = outcomes |> List.map (fst >> idOf)
  let requested, earlier = RequestedRuns.request (Some requestId) session ids LiveTestState.empty
  let running = RequestedRuns.started session earlier requested
  let resultFor (name: string, outcome: Outcome) =
    let id = idOf name
    match outcome with
    | PassedHere | PassedEarlier -> Some (mkResult id (TestResult.Passed (ts 5.0)))
    | FailedHere -> Some (mkResult id (TestResult.Failed (TestFailure.AssertionFailed "expected 1 got 2", ts 5.0)))
    | SkippedHere -> Some (mkResult id (TestResult.Skipped "ignored by the suite"))
    | NoResultHere -> Some (mkResult id (TestResult.NoResult NoResultReason.StreamEnded))
    | Silent -> None
  let here = outcomes |> List.filter (fun (_, o) -> o <> PassedEarlier && o <> Silent) |> List.choose resultFor
  let stamped = RequestedRuns.stampResults (Some session) here running
  let withResults =
    { stamped with
        LastResults =
          outcomes
          |> List.choose (fun o -> resultFor o |> Option.map (fun r -> r.TestId, r))
          |> Map.ofList
        DiscoveredTests = outcomes |> List.map (fst >> caseOf) |> Array.ofList }
  // An earlier pass carries an older generation than this run's.
  let earlierGeneration = RunGeneration.zero
  let withEarlier =
    outcomes
    |> List.filter (fun (_, o) -> o = PassedEarlier)
    |> List.fold (fun (s: LiveTestState) (name, _) ->
      { s with ResultGenerations = Map.add (idOf name) earlierGeneration s.ResultGenerations }) withResults
  RequestedRuns.completed session withEarlier

let private ran (receipt: RunReceipt) : RanReceipt =
  match receipt with
  | RunReceipt.Ran r -> r
  | other -> failtestf "expected Ran, got %A" other

[<Tests>]
let requestTests =
  testList "RequestedRuns.request records what was asked for" [

    testCase "WHY — the request remembers which tests it named, so a later read by request id can list every one, including those that never reported" <| fun _ ->
      let ids = [ idOf "a"; idOf "b" ]
      let state, _ = RequestedRuns.request (Some requestId) session ids LiveTestState.empty
      state.RunRequests
      |> Map.find requestId
      |> fun r -> r.RequestedTests
      |> Expect.equal "the named tests are on the record" ids
  ]

[<Tests>]
let observeTests =
  testList "TestRunReceipt.observe" [

    testCase "WHY — a request nobody has recorded (or one evicted from the bounded map) claims nothing" <| fun _ ->
      match TestRunReceipt.observe requestId LiveTestState.empty with
      | RunReceipt.Unattributable rid -> rid |> Expect.equal "names the request it cannot attribute" requestId
      | other -> failtestf "expected Unattributable, got %A" other

    testCase "WHY — a run the worker has not started yet is Pending, with how many tests it named" <| fun _ ->
      let state, _ = RequestedRuns.request (Some requestId) session [ idOf "a"; idOf "b" ] LiveTestState.empty
      match TestRunReceipt.observe requestId state with
      | RunReceipt.Pending (rid, count) ->
        rid |> Expect.equal "same request" requestId
        count |> Expect.equal "two named" 2
      | other -> failtestf "expected Pending, got %A" other

    testCase "WHY — a run the worker started but has not finished is Started, never a verdict" <| fun _ ->
      let requested, gen = RequestedRuns.request (Some requestId) session [ idOf "a" ] LiveTestState.empty
      let running = RequestedRuns.started session gen requested
      match TestRunReceipt.observe requestId running with
      | RunReceipt.Started (rid, count) ->
        rid |> Expect.equal "same request" requestId
        count |> Expect.equal "one named" 1
      | other -> failtestf "expected Started, got %A" other

    testCase "WHY — every requested test passed by THIS run is AllPassed, with counts" <| fun _ ->
      let receipt = finishedState [ "a", PassedHere; "b", PassedHere ] |> TestRunReceipt.observe requestId |> ran
      receipt.Verdict |> Expect.equal "all passed" RunVerdict.AllPassed
      receipt.Counts |> Expect.equal "two passed, nothing else" { Passing = 2; Failing = 0; Skipping = 0; Unreported = 0 }

    testCase "WHY — one failure makes the whole run SomeFailed, and the failing line carries the assertion message" <| fun _ ->
      let receipt = finishedState [ "a", PassedHere; "b", FailedHere ] |> TestRunReceipt.observe requestId |> ran
      receipt.Verdict |> Expect.equal "some failed" RunVerdict.SomeFailed
      let failed = receipt.Lines |> List.find (fun l -> l.Id = idOf "b")
      match failed.Outcome with
      | LineOutcome.Failed reason -> reason |> Expect.stringContains "says what failed" "expected 1 got 2"
      | other -> failtestf "expected Failed, got %A" other

    testCase "WHY — a pass left over from an EARLIER run is not this run's pass: the run is Incomplete, never AllPassed (the false green this exists to prevent)" <| fun _ ->
      let receipt = finishedState [ "a", PassedHere; "b", PassedEarlier ] |> TestRunReceipt.observe requestId |> ran
      receipt.Verdict |> Expect.equal "not all passed here" RunVerdict.Incomplete
      let b = receipt.Lines |> List.find (fun l -> l.Id = idOf "b")
      match b.Outcome with
      | LineOutcome.DidNotReport reason -> reason |> Expect.stringContains "says the pass is from an earlier run" "earlier run"
      | other -> failtestf "expected DidNotReport, got %A" other

    testCase "WHY — a requested test that never reported is named as not reported, not dropped from the count" <| fun _ ->
      let receipt = finishedState [ "a", PassedHere; "b", Silent ] |> TestRunReceipt.observe requestId |> ran
      receipt.Verdict |> Expect.equal "incomplete" RunVerdict.Incomplete
      receipt.Counts.Unreported |> Expect.equal "one did not report" 1
      receipt.Lines |> List.length |> Expect.equal "both tests are on the receipt" 2

    testCase "WHY — a test the run ended on before it reported carries the engine's own reason" <| fun _ ->
      let receipt = finishedState [ "a", NoResultHere ] |> TestRunReceipt.observe requestId |> ran
      match (List.head receipt.Lines).Outcome with
      | LineOutcome.DidNotReport reason ->
        reason |> Expect.stringContains "uses NoResultReason.describe" (NoResultReason.describe NoResultReason.StreamEnded)
      | other -> failtestf "expected DidNotReport, got %A" other

    testCase "WHY — a skipped test did not pass here, so the run is Incomplete and the reason is kept" <| fun _ ->
      let receipt = finishedState [ "a", SkippedHere ] |> TestRunReceipt.observe requestId |> ran
      receipt.Verdict |> Expect.equal "skips are not passes" RunVerdict.Incomplete
      match (List.head receipt.Lines).Outcome with
      | LineOutcome.Skipped reason -> reason |> Expect.stringContains "keeps the skip reason" "ignored by the suite"
      | other -> failtestf "expected Skipped, got %A" other

    testCase "WHY — a run that named no tests cannot be AllPassed: zero tests passing is nothing, not success" <| fun _ ->
      let requested, gen = RequestedRuns.request (Some requestId) session [] LiveTestState.empty
      let done' = RequestedRuns.started session gen requested |> RequestedRuns.completed session
      let receipt = TestRunReceipt.observe requestId done' |> ran
      receipt.Verdict |> Expect.equal "nothing ran" RunVerdict.Incomplete
  ]

let private allOutcomes = [| PassedHere; PassedEarlier; FailedHere; SkippedHere; NoResultHere; Silent |]

/// Up to six tests, each given an outcome picked by an integer, so FsCheck can shrink the mix.
let private outcomesOf (picks: int list) : (string * Outcome) list =
  picks
  |> List.truncate 6
  |> List.mapi (fun i pick -> sprintf "t%d" i, allOutcomes[abs (pick % allOutcomes.Length)])

[<Tests>]
let propertyTests =
  testList "TestRunReceipt properties" [

    testProperty "WHY — AllPassed exactly when tests were named and every one passed in this run; counts always add up to the lines" <| fun (picks: int list) ->
      let outcomes = outcomesOf picks
      let receipt = finishedState outcomes |> TestRunReceipt.observe requestId |> ran
      let everyPassedHere = (not (List.isEmpty outcomes)) && outcomes |> List.forall (fun (_, o) -> o = PassedHere)
      let c = receipt.Counts
      (receipt.Verdict = RunVerdict.AllPassed) = everyPassedHere
      && c.Passing + c.Failing + c.Skipping + c.Unreported = List.length receipt.Lines
      && List.length receipt.Lines = List.length outcomes

    testProperty "WHY — the cohort landing gate and the receipt agree on which tests failed, for any mix of outcomes: one engine, one answer" <| fun (picks: int list) ->
      let outcomes = outcomesOf picks
      let state = finishedState outcomes
      let receipt = TestRunReceipt.observe requestId state |> ran
      let ids = outcomes |> List.map (fst >> idOf)
      match CohortLandingVerify.advance ids (CohortLandingVerify.RunProgress.Started requestId) state with
      | CohortLandingVerify.RunProgress.Finished failing ->
        Set.ofList failing = Set.ofList (TestRunReceipt.notPassed receipt)
      | _ -> false
  ]

let private sessionTrusted = SessionTrust.Trusted session
let private someCases = [| caseOf "A.one"; caseOf "A.two" |]

[<Tests>]
let planTests =
  testList "RunReceipts.plan" [

    testCase "WHY — a session that cannot be believed refuses before anything is dispatched, and says why" <| fun _ ->
      match TestRunReceipt.plan (SessionTrust.WarmingUp session) someCases someCases "no filters" with
      | RunPlan.Refuse (RunRefusal.SessionNotTrusted (SessionTrust.WarmingUp s)) -> s |> Expect.equal "names the session" session
      | other -> failtestf "expected a trust refusal, got %A" other

    testCase "WHY — a trusted session with nothing discovered says so instead of running nothing" <| fun _ ->
      match TestRunReceipt.plan sessionTrusted [||] [||] "no filters" with
      | RunPlan.Refuse RunRefusal.NothingDiscovered -> ()
      | other -> failtestf "expected NothingDiscovered, got %A" other

    testCase "WHY — filters that match nothing are a refusal that names the filters, never a zero-test pass" <| fun _ ->
      match TestRunReceipt.plan sessionTrusted someCases [||] "pattern=zzz" with
      | RunPlan.Refuse (RunRefusal.NoTestMatched filters) -> filters |> Expect.stringContains "names the filter" "pattern=zzz"
      | other -> failtestf "expected NoTestMatched, got %A" other

    testCase "WHY — a trusted session with matching tests runs exactly the matched ones" <| fun _ ->
      match TestRunReceipt.plan sessionTrusted someCases [| someCases[0] |] "pattern=one" with
      | RunPlan.Run cases -> cases |> Expect.equal "only the matched test" [| someCases[0] |]
      | other -> failtestf "expected Run, got %A" other
  ]

[<Tests>]
let renderTests =
  testList "RunReceipts rendering" [

    testCase "WHY — an all-passed receipt says how many passed and in which run, and its JSON carries the tokens an agent branches on" <| fun _ ->
      let receipt = finishedState [ "a", PassedHere; "b", PassedHere ] |> TestRunReceipt.observe requestId
      TestRunReceipt.summarize receipt |> Expect.stringContains "counts" "2 passed"
      let json = TestRunReceipt.toJson receipt
      json["status"].GetValue<string>() |> Expect.equal "status token" "Ran"
      json["verdict"].GetValue<string>() |> Expect.equal "verdict token" "AllPassed"
      json["lines"].AsArray().Count |> Expect.equal "a line per requested test" 2

    testCase "WHY — an incomplete receipt never reads as a pass: its text says not every test passed and names what did not" <| fun _ ->
      let receipt = finishedState [ "a", PassedHere; "b", Silent ] |> TestRunReceipt.observe requestId
      let text = TestRunReceipt.summarize receipt
      text |> Expect.stringContains "says it is not green" "not every test passed"
      text |> Expect.stringContains "names the test that did not report" "b"
      let json = TestRunReceipt.toJson receipt
      json["verdict"].GetValue<string>() |> Expect.equal "verdict token" "Incomplete"

    testCase "WHY — a run still in flight hands back the request id and how to keep waiting, so the agent never has to guess" <| fun _ ->
      let state, _ = RequestedRuns.request (Some requestId) session [ idOf "a" ] LiveTestState.empty
      let receipt = TestRunReceipt.observe requestId state
      let json = TestRunReceipt.toJson receipt
      json["status"].GetValue<string>() |> Expect.equal "status token" "Pending"
      json["receiptId"].GetValue<string>() |> Expect.equal "request id for the next call" ((RunRequestId.value requestId).ToString())
      TestRunReceipt.summarize receipt |> Expect.stringContains "says how to wait" "receipt_id"

    testCase "WHY — a refusal carries a stable token plus the actionable message" <| fun _ ->
      let receipt = RunReceipt.Refused RunRefusal.NothingDiscovered
      let json = TestRunReceipt.toJson receipt
      json["status"].GetValue<string>() |> Expect.equal "status token" "Refused"
      json["reason"].GetValue<string>() |> Expect.equal "reason token" "NothingDiscovered"
      json["message"].GetValue<string>() |> Expect.stringContains "actionable" "live testing"

    testCase "WHY — a request nobody can account for says it cannot claim anything about it" <| fun _ ->
      TestRunReceipt.summarize (RunReceipt.Unattributable requestId) |> Expect.stringContains "no claim" "cannot"
  ]
