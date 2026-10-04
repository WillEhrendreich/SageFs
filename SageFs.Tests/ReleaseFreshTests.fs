/// How a ship or a gate asks the pipeline to ignore its tier pass records (`--fresh`), and where the records live.
/// The pure parts are scripts/ReleaseRules.fs (linked into this project); ship.fsx and local-gate.fsx only do the IO.
module SageFs.Tests.ReleaseFreshTests

open Expecto
open Expecto.Flip
open FsCheck
open ReleaseRules

[<Tests>]
let tests =
  testList "Release fresh" [
    testCase "a gate runs the pipeline with every stage, and adds --fresh only when asked" <| fun _ ->
      pipelineArgs AllowReuse |> Expect.equal "default" [ "fsi"; "ci-pipeline.fsx"; "--"; "ci"; "release" ]
      pipelineArgs ForceFresh |> Expect.equal "fresh" [ "fsi"; "ci-pipeline.fsx"; "--"; "ci"; "release"; "--fresh" ]

    testCase "--fresh is taken from anywhere in the arguments" <| fun _ ->
      splitFresh [] |> Expect.equal "none" (AllowReuse, [])
      splitFresh [ "--fresh" ] |> Expect.equal "alone" (ForceFresh, [])
      splitFresh [ "abc123"; "--fresh" ] |> Expect.equal "after the commit" (ForceFresh, [ "abc123" ])
      splitFresh [ "--fresh"; "abc123" ] |> Expect.equal "before the commit" (ForceFresh, [ "abc123" ])

    testProperty "everything else keeps its order, and --fresh is gone" <|
      fun (args: string list) ->
        let kept = args |> List.filter (fun a -> a <> "--fresh")
        let wanted = match List.contains "--fresh" args with | true -> ForceFresh | false -> AllowReuse
        splitFresh args = (wanted, kept)

    testCase "a forced gate (--force) never reuses a record, because it exists to redo everything" <| fun _ ->
      freshnessOfForce true |> Expect.equal "force is fresh" ForceFresh
      freshnessOfForce false |> Expect.equal "otherwise records may be reused" AllowReuse

    testCase "the console shows which tiers took a record and which ran, so a reuse is never silent on screen" <| fun _ ->
      isProgressLine "── tier --ratchets-net10             reused: green in 8s on 2026-10-04T17:30:22-05:00, the same commit and the same bytes (record 027cc30db122)"
      |> Expect.isTrue "a reused tier"
      isProgressLine "pass records: --integration-host[2/5]     runs (changed: closure)"
      |> Expect.isTrue "a tier that runs, with why"
      isProgressLine "pass records: every tier took a record, nothing to run (--fresh runs them all)"
      |> Expect.isTrue "the whole stage skipped"

    testCase "the records live in the gate's own home, beside its passes, never in the checkout" <| fun _ ->
      tierPassesDirectory "/home/u/.local/share/sagefs-gate"
      |> Expect.equal "under the gate home" "/home/u/.local/share/sagefs-gate/tier-passes"
  ]
