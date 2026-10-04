/// The `--integration-lt` tier as four tiers, each on a copy of the sample of its own.
///
/// Every live-testing case edits `Hello.fs` of the FromCSharp sample in place and waits for builds and restarts, so
/// the 12 cases ran one after another in one tier (290 to 330 s). A tier now takes a share of the cases (`--shard k/n`,
/// the partition the host tier already uses) and runs them on a daemon, a session and a COPY of the sample that are its
/// own, so n shards edit n different `Hello.fs` and run side by side. The checked-in sample is never written.
module SageFs.Tests.LiveTestingShardsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Build
open SageFs.Tests.LiveTestingShards

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private shardOf (index: int) (count: int) : TierPlan.Shard = { Index = index; Count = count }

/// Every case the registry holds for the tier, by name.
let private registeredLtCases () : string list =
  Integration.registered ()
  |> List.choose (fun (runner, test) ->
    match runner with
    | Integration.Dedicated "--integration-lt" -> Some test
    | Integration.Dedicated _ | Integration.Host -> None)
  |> List.collect (fun test -> Expecto.Test.toTestCodeList test |> List.map (fun flat -> String.concat "." flat.name))

[<Tests>]
let tests =
  testList "Live-testing shards" [
    testCase "the runner's tree holds every case registered for the tier, once: a case missing from it would run nowhere" <| fun _ ->
      let inTree = caseNames (tree ()) |> List.sort
      inTree |> Expect.equal "the tree and the registry hold the same cases" (registeredLtCases () |> List.sort)
      inTree |> List.distinct |> List.length |> Expect.equal "each is named once" (List.length inTree)

    testCase "the shards of any count together run every case exactly once" <| fun _ ->
      let all = caseNames (tree ())
      for count in [ 1; 2; 3; 4; 5; 7 ] do
        let shares = [ for index in 1 .. count -> caseNames (share (Some (shardOf index count)) Map.empty (tree ())) ]
        shares |> List.concat |> List.sort |> Expect.equal (sprintf "%d shards cover the tree, with no case twice" count) (List.sort all)

    testCase "with no shard the whole tree runs, as before" <| fun _ ->
      caseNames (share None Map.empty (tree ())) |> Expect.equal "everything, in order" (caseNames (tree ()))

    testCase "a share keeps the order of the tree: the journeys first, then the browser cases, the latency measurement last" <| fun _ ->
      let all = caseNames (tree ())
      for index in 1 .. 3 do
        let mine = caseNames (share (Some (shardOf index 3)) Map.empty (tree ()))
        mine
        |> List.map (fun name -> List.findIndex ((=) name) all)
        |> Expect.equal "in the tree's order" (mine |> List.map (fun name -> List.findIndex ((=) name) all) |> List.sort)

    testCase "recorded durations split the heavy cases: the two longest are never in one shard of two" <| fun _ ->
      let all = caseNames (tree ())
      let heavy = [ all[0]; all[1] ]
      let durations = all |> List.map (fun name -> name, (match List.contains name heavy with | true -> 200.0 | false -> 5.0)) |> Map.ofList
      let first = caseNames (share (Some (shardOf 1 2)) durations (tree ()))
      let second = caseNames (share (Some (shardOf 2 2)) durations (tree ()))
      [ first; second ]
      |> List.map (fun shard -> shard |> List.filter (fun name -> List.contains name heavy) |> List.length)
      |> Expect.equal "one heavy case in each" [ 1; 1 ]

    testCase "an empty share is an empty tree, which the trust row calls NothingRan and not green" <| fun _ ->
      let count = List.length (caseNames (tree ())) + 3
      caseNames (share (Some (shardOf count count)) Map.empty (tree ()))
      |> Expect.isEmpty "more shards than cases leaves a shard with nothing"

    testCase "the sample copy is the sample's sources under the repo, without what a build wrote, and editing it leaves the sample alone" <| fun _ ->
      let repoRoot = RepoPaths.repoPathFull [||]
      let sample = Path.Combine(repoRoot, "samples", "from-csharp", "SageFs.Samples.FromCSharp")
      let helloBefore = File.ReadAllText(Path.Combine(sample, "Hello.fs"))
      let copy = sampleCopy repoRoot
      try
        copy |> Expect.stringStarts "under the repo, where its central package versions apply" repoRoot
        (copy = sample) |> Expect.isFalse "and not the sample itself"
        let names = Directory.GetFiles(copy, "*", SearchOption.AllDirectories) |> Array.map (fun f -> Path.GetRelativePath(copy, f)) |> Array.sort
        names |> Array.contains "Hello.fs" |> Expect.isTrue "Hello.fs is there"
        names |> Array.contains "SageFs.Samples.FromCSharp.fsproj" |> Expect.isTrue "so is the project"
        names |> Array.filter (fun name -> name.StartsWith "bin" || name.StartsWith "obj") |> Expect.isEmpty "no build output comes along"
        File.WriteAllText(Path.Combine(copy, "Hello.fs"), "// edited by a case")
        File.ReadAllText(Path.Combine(sample, "Hello.fs")) |> Expect.equal "the checked-in sample is untouched" helloBefore
      finally
        try Directory.Delete(copy, true) with _ -> ()
  ]
