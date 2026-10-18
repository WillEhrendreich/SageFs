module SageFs.Tests.HostCoverageListingWiringTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs.Tests.TestInfrastructure

/// The trust report's "--integration-host (coverage)" row says every registered host case was handed to a shard. It
/// needs the number of host cases, which comes from one `--integration-host --list-tests` process that `runTiers`
/// starts beside the first units. `runTiers` is only handed the tiers that will RUN, so when every host shard was
/// reused from a pass record the listing never started, the count stayed "the host tier was not listed", and the row
/// went red (CoverageUnknown) in the 2026-10-18 local gate on a pass where nothing about the host tier had changed.
/// A reused shard is still a shard the coverage check has to count, so the listing starts when ANY host tier is
/// planned, run or reused.
let hostCoverageListingWiringTests =
  let pipeline = lazy (File.ReadAllText(Path.Combine(RepoPaths.repoPathFull [||], "ci-pipeline.fsx")))

  /// The text that decides whether the host listing starts: from `let hostTierPlanned` to where it builds the log path.
  let listingDecision () =
    let text = pipeline.Value
    match text.IndexOf "let hostTierPlanned" with
    | -1 -> ""
    | start ->
      let tail = text.Substring start
      match tail.IndexOf "let listLog" with
      | -1 -> tail
      | stop -> tail.Substring(0, stop)

  testList "Host coverage listing wiring" [
    testCase "the decision to list the host tier exists" <| fun _ ->
      listingDecision ()
      |> Expect.isNotEmpty "ci-pipeline.fsx decides whether to list the host tier (`let hostTierPlanned` before `let listLog`)"

    testCase "the listing also starts when every host shard was reused from a pass record" <| fun _ ->
      listingDecision ()
      |> Expect.stringContains "the decision looks at reusedTiers as well as the tiers about to run" "reusedTiers"

    testCase "reusedTiers is declared before runTiers, so runTiers can read it" <| fun _ ->
      let text = pipeline.Value
      let declared = text.IndexOf "let reusedTiers"
      let used = text.IndexOf "let runTiers"
      (declared >= 0 && used >= 0 && declared < used)
      |> Expect.isTrue "`let reusedTiers` comes before `let runTiers` in ci-pipeline.fsx"
  ]

[<Tests>]
let tests =
  let ratchet (test: Test) = TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant test
  ratchet hostCoverageListingWiringTests
