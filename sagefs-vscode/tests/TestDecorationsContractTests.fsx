// WHY — outcome-gate-sweep.md Gap G: `TestDecorations.fs:95-101` computed
// `passedRanges`/`failedRanges`/`runningRanges` and the one-based→zero-based
// `newRange (line - 1) ...` line conversion inline, next to Fable-only
// `vscode.window` calls, with ZERO test references. An off-by-one there, or
// an `Errored` outcome silently routed into `passedRanges`, would ship
// green — nothing outside a real Electron host ever called
// `applyToEditor`/`decorationRange`, and `VscLiveTestStateTests` only
// exercises the reducer that feeds this code, never the decoration mapping
// itself. This is the most visible promise in the README (the ✓/✗/○ gutter
// markers) with the least coverage.
//
// This pins the extracted pure decision (`TestDecorationsPure.fs`) with:
//   - an exhaustive, named example per `VscTestOutcome` case (so a case that
//     silently changes bucket is a one-line diff away from a red test);
//   - a property, over ALL of `VscTestOutcome` via FsCheck generation, that
//     every outcome lands in exactly one of the three buckets, and that
//     `Failed`/`Errored` NEVER land in `Passed` — the specific defect class
//     named in the sweep;
//   - a property that the one-based→zero-based line conversion round-trips
//     for every line SageFs could plausibly report.
//
// Runs under plain `dotnet fsi` (no Fable, no VS Code, no Electron host) —
// mirroring the other 14 `sagefs-vscode/tests/*.fsx` contract tests.
// `@vscode/test-electron` decoration introspection is expensive and
// historically flaky (`VscodeExtensionTests.fs:618-628` — the CDP journeys
// "never passed once since being wired into CI"). This proves the data
// handed to `setDecorations` is right; it does not prove VS Code painted
// it. That last inch is not worth an Electron harness — the layer below it,
// where the actual bugs live, is what this covers.
#r "nuget: Expecto, 11.0.0-alpha8"
#r "nuget: Expecto.FsCheck, 11.0.0-alpha8"
#r "nuget: FsCheck, 3.3.2"
#load "../src/LiveTestingTypes.fs"
#load "../src/TestDecorationsPure.fs"

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.TestDecorationsPure

// ── Fixtures ──────────────────────────────────────────────────────

let private mkTest (id: string) (filePath: string) (line: int option) : VscTestInfo =
  { Id = VscTestId.create id
    DisplayName = id
    FullName = id
    FilePath = Some filePath
    Line = line }

let private mkResult (id: string) (outcome: VscTestOutcome) : VscTestResult =
  { Id = VscTestId.create id; Outcome = outcome; DurationMs = None; Output = None }

let private stateWith (tests: VscTestInfo list) (results: VscTestResult list) : VscLiveTestState =
  { VscLiveTestState.empty with
      Tests = tests |> List.map (fun t -> t.Id, t) |> Map.ofList
      Results = results |> List.map (fun r -> r.Id, r) |> Map.ofList }

let private allBuckets (d: FileDecorations) : DecorationBucket list =
  [ for e in d.Passed -> e.Bucket
    for e in d.Failed -> e.Bucket
    for e in d.Running -> e.Bucket ]

// ── Example-based: one case per VscTestOutcome, named explicitly ──

let outcomeCases =
  testList "every VscTestOutcome case routes to exactly one named bucket" [

    testCase "Passed -> Passed bucket, no Failed, no Running" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] [ mkResult "t1" VscTestOutcome.Passed ]
      let d = decorationsForFile state "f.fs"
      d.Passed |> List.length |> Expect.equal "one passed entry" 1
      d.Failed |> Expect.isEmpty "no failed entries"
      d.Running |> Expect.isEmpty "no running entries"

    testCase "Failed -> Failed bucket, NEVER Passed" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] [ mkResult "t1" (VscTestOutcome.Failed "boom") ]
      let d = decorationsForFile state "f.fs"
      d.Failed |> List.length |> Expect.equal "one failed entry" 1
      d.Passed |> Expect.isEmpty "Failed must never land in Passed"
      d.Running |> Expect.isEmpty "no running entries"

    testCase "Errored -> Failed bucket, NEVER Passed — the exact defect named in the sweep" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] [ mkResult "t1" (VscTestOutcome.Errored "kaboom") ]
      let d = decorationsForFile state "f.fs"
      d.Failed |> List.length |> Expect.equal "one failed entry" 1
      d.Passed |> Expect.isEmpty "Errored must never silently land in Passed"
      d.Running |> Expect.isEmpty "no running entries"

    testCase "Running -> Running bucket" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] [ mkResult "t1" VscTestOutcome.Running ]
      let d = decorationsForFile state "f.fs"
      d.Running |> List.length |> Expect.equal "one running entry" 1
      d.Passed |> Expect.isEmpty "no passed entries"
      d.Failed |> Expect.isEmpty "no failed entries"

    testCase "Skipped -> Passed bucket (a skip is not a failure)" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] [ mkResult "t1" (VscTestOutcome.Skipped "not applicable") ]
      let d = decorationsForFile state "f.fs"
      d.Passed |> List.length |> Expect.equal "one passed entry" 1
      d.Failed |> Expect.isEmpty "a skip is not a failure"

    testCase "Stale -> Running bucket" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] [ mkResult "t1" VscTestOutcome.Stale ]
      let d = decorationsForFile state "f.fs"
      d.Running |> List.length |> Expect.equal "one running entry" 1

    testCase "PolicyDisabled -> Passed bucket" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] [ mkResult "t1" VscTestOutcome.PolicyDisabled ]
      let d = decorationsForFile state "f.fs"
      d.Passed |> List.length |> Expect.equal "one passed entry" 1

    testCase "NotYetRun -> Running bucket" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] [ mkResult "t1" VscTestOutcome.NotYetRun ]
      let d = decorationsForFile state "f.fs"
      d.Running |> List.length |> Expect.equal "one running entry" 1

    testCase "no result at all (known test, never run) -> Running bucket, same as NotYetRun" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 10) ] []
      let d = decorationsForFile state "f.fs"
      d.Running |> List.length |> Expect.equal "one running entry" 1
      d.Running.Head.HoverText |> Expect.stringContains "not yet run text" "not yet run"

    testCase "a test with no known line produces no decoration at all" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" None ] [ mkResult "t1" VscTestOutcome.Passed ]
      let d = decorationsForFile state "f.fs"
      allBuckets d |> Expect.isEmpty "line-less test decorates nothing"

    testCase "a test in a different file is not decorated for this file" <| fun _ ->
      let state = stateWith [ mkTest "t1" "other.fs" (Some 10) ] [ mkResult "t1" (VscTestOutcome.Failed "x") ]
      let d = decorationsForFile state "f.fs"
      allBuckets d |> Expect.isEmpty "cross-file leakage"
  ]

// ── Property: the line mapping round-trips ─────────────────────────

let lineRoundTrip =
  testList "one-based -> zero-based line mapping round-trips" [

    testProperty "toOneBasedLine (toZeroBasedLine n) = n, for every plausible one-based line" <|
      fun () ->
        Prop.forAll (Arb.fromGen (Gen.choose (1, 1_000_000))) (fun (oneBasedLine: int) ->
          toOneBasedLine (toZeroBasedLine oneBasedLine) = oneBasedLine)

    testCase "line 1 (the first line of a file) maps to zero-based line 0" <| fun _ ->
      toZeroBasedLine 1 |> Expect.equal "first line is index 0" 0

    testCase "a decoration entry's ZeroBasedLine is always exactly one less than its Line" <| fun _ ->
      let state = stateWith [ mkTest "t1" "f.fs" (Some 42) ] [ mkResult "t1" VscTestOutcome.Passed ]
      let d = decorationsForFile state "f.fs"
      let entry = d.Passed.Head
      entry.Line |> Expect.equal "reported line" 42
      entry.ZeroBasedLine |> Expect.equal "VS Code line" 41
  ]

// ── Property: total, exclusive bucket routing over ALL of VscTestOutcome ──

let genOutcome : Gen<VscTestOutcome> =
  Gen.oneof [
    Gen.constant VscTestOutcome.Passed
    Gen.map VscTestOutcome.Failed (ArbMap.defaults |> ArbMap.generate<string>)
    Gen.map VscTestOutcome.Skipped (ArbMap.defaults |> ArbMap.generate<string>)
    Gen.constant VscTestOutcome.Running
    Gen.map VscTestOutcome.Errored (ArbMap.defaults |> ArbMap.generate<string>)
    Gen.constant VscTestOutcome.Stale
    Gen.constant VscTestOutcome.PolicyDisabled
    Gen.constant VscTestOutcome.NotYetRun
  ]

let genFreshness : Gen<VscResultFreshness> =
  Gen.elements [
    VscResultFreshness.Fresh
    VscResultFreshness.StaleCodeEdited
    VscResultFreshness.StaleWrongGeneration
  ]

/// True for the outcomes that represent an actual failure — the only two
/// cases that must land in `DecorationBucket.Failed`, and must NEVER land
/// anywhere else.
let private isFailureOutcome (outcome: VscTestOutcome) =
  match outcome with
  | VscTestOutcome.Failed _ | VscTestOutcome.Errored _ -> true
  | _ -> false

let genOutcomeAndFreshness : Gen<VscTestOutcome * VscResultFreshness> =
  gen {
    let! outcome = genOutcome
    let! freshness = genFreshness
    return outcome, freshness
  }

let bucketRoutingProperties =
  testList "bucketForOutcome is total and exclusive over VscTestOutcome" [

    testProperty "every outcome routes to exactly one bucket (bucketForOutcome is a total function)" <|
      Prop.forAll
        (Arb.fromGen genOutcomeAndFreshness)
        (fun (outcome, freshness) ->
          // A total function returning a single DU value already can't
          // land in two buckets at once; the meaningful assertion is that
          // it always returns SOME recognized bucket and never throws —
          // i.e. the match is genuinely exhaustive for every generated case.
          let bucket, _text = bucketForOutcome "t" freshness "" None outcome
          match bucket with
          | DecorationBucket.Passed | DecorationBucket.Failed | DecorationBucket.Running -> true)

    testProperty "Failed and Errored ALWAYS route to Failed, and nothing else ever does" <|
      Prop.forAll
        (Arb.fromGen genOutcomeAndFreshness)
        (fun (outcome, freshness) ->
          let bucket, _text = bucketForOutcome "t" freshness "" None outcome
          match isFailureOutcome outcome, bucket with
          | true, DecorationBucket.Failed -> true
          | true, _ -> false // a failure outcome escaped the Failed bucket — the sweep's named defect
          | false, DecorationBucket.Failed -> false // something non-failing was misrouted into Failed
          | false, _ -> true)

    testProperty "decorationsForFile never puts a Failed/Errored test's entry in Passed or Running" <|
      Prop.forAll (Arb.fromGen genOutcome) (fun outcome ->
        let state = stateWith [ mkTest "t1" "f.fs" (Some 5) ] [ mkResult "t1" outcome ]
        let d = decorationsForFile state "f.fs"
        match isFailureOutcome outcome with
        | true -> d.Failed.Length = 1 && d.Passed.IsEmpty && d.Running.IsEmpty
        | false -> d.Failed.IsEmpty)

    testProperty "every decorated test lands in exactly one bucket (union count = decorated-test count)" <|
      Prop.forAll (Arb.fromGen genOutcome) (fun outcome ->
        let state = stateWith [ mkTest "t1" "f.fs" (Some 5) ] [ mkResult "t1" outcome ]
        let d = decorationsForFile state "f.fs"
        (d.Passed.Length + d.Failed.Length + d.Running.Length) = 1)
  ]

let tests =
  testList "TestDecorationsPure contract" [
    outcomeCases
    lineRoundTrip
    bucketRoutingProperties
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
