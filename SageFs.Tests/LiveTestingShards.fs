/// The `--integration-lt` tier as shards, each on a copy of the sample of its own.
///
/// Every live-testing case edits `Hello.fs` of the FromCSharp sample in place and waits for builds and restarts, so the
/// 12 cases ran one after another on one daemon and one session. A tier now takes a share of the cases (`--shard k/n`,
/// the partition the host tier uses: balanced by recorded seconds, the same on every shard with no coordination) and runs
/// it on a daemon, a session and a copy of the sample that are its own. Inside a share the order is the tree's, as it
/// was: the journeys first, the browser cases next, the latency measurement last from a settled session.
module SageFs.Tests.LiveTestingShards

open System
open System.Collections.Generic
open System.IO
open Expecto
open SageFs.Build

/// The tree the `--integration-lt` runner runs. One daemon and one session per shard, so a shard's cases are in sequence.
let tree () : Test =
  testSequenced (
    testList
      "Live testing against the FromCSharp sample"
      [ LiveTestingJourneyTests.journeyTests
        LiveTestingBrowserTests.tests
        LiveTestingLatencyTests.latencyTests ])

/// The name of a case: its own, which is unique across the tier.
let nameOf (flat: FlatTest) : string = List.last flat.name

/// Every case of `tree`, by name, in the tree's order.
let caseNames (tree: Test) : string list =
  Test.toTestCodeList tree |> List.map nameOf

/// `tree` kept to the cases of `shard`, in the tree's order. `durations` are the recorded seconds by case name, which the
/// partition balances on; a case with none counts as the mean of the known ones. With no shard the tree is whole.
let share (shard: TierPlan.Shard option) (durations: Map<string, float>) (tree: Test) : Test =
  match shard with
  | None -> tree
  | Some mine ->
    let flat = Test.toTestCodeList tree
    let plan = TierPlan.assign mine.Count durations (flat |> List.map nameOf)
    let bodies = HashSet<obj>(HashIdentity.Reference)
    for case in flat do
      match plan[nameOf case] = mine.Index with
      | true -> bodies.Add(box case.test) |> ignore
      | false -> ()
    TestInfrastructure.Integration.filterLeaves (fun code -> bodies.Contains(box code)) tree

/// The recorded seconds per case the pipeline hands every tier (the same file the host tier balances on).
[<Literal>]
let DurationsVariable = "SAGEFS_SUITE_DURATIONS"

/// Where a tier writes the seconds each case took, for the pipeline to merge into the durations file.
[<Literal>]
let TimingsVariable = "SAGEFS_SUITE_TIMINGS_OUT"

let readDurations () : Map<string, float> =
  match Environment.GetEnvironmentVariable DurationsVariable with
  | null | "" -> Map.empty
  | path ->
    try System.Text.Json.JsonSerializer.Deserialize<Map<string, float>>(File.ReadAllText path)
    with _ -> Map.empty

/// Write the seconds each case of this run took, by case name.
let recordCaseTimings (summary: Impl.TestRunSummary) : unit =
  match Environment.GetEnvironmentVariable TimingsVariable with
  | null | "" -> ()
  | path ->
    let timings =
      summary.passed @ summary.failed @ summary.errored @ summary.ignored
      |> List.map (fun (flat, result) -> List.last flat.name, result.duration.TotalSeconds)
      |> Map.ofList
    try File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize timings) with _ -> ()

/// What a build wrote into a sample, and nothing else a copy should carry.
let buildOutputs = [ "bin"; "obj" ]

/// Copy the FromCSharp sample's sources into a new dir under the repo and return it. Under the repo on purpose: the
/// sample's package versions are central (`Directory.Packages.props`), so a copy under /tmp could not restore. Under a
/// fixture's own git-ignored `.runs`, as the hot-reload fixtures are, and not under `samples/`, which the pipeline and the
/// tests walk for projects. The checked-in sample is only read.
let sampleCopy (repoRoot: string) : string =
  let source = Path.Combine(repoRoot, "samples", "from-csharp", "SageFs.Samples.FromCSharp")
  let target = Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "LiveTestingSample", ".runs", Guid.NewGuid().ToString "N")
  Directory.CreateDirectory target |> ignore
  for file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
    let relative = Path.GetRelativePath(source, file)
    let top = relative.Split(Path.DirectorySeparatorChar).[0]
    match List.contains top buildOutputs with
    | true -> ()
    | false ->
      let destination = Path.Combine(target, relative)
      Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
      File.Copy(file, destination)
  target
