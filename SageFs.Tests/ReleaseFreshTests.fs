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

    testCase "the records live in the gate's own home, beside its passes, never in the checkout" <| fun _ ->
      tierPassesDirectory "/home/u/.local/share/sagefs-gate"
      |> Expect.equal "under the gate home" "/home/u/.local/share/sagefs-gate/tier-passes"
  ]
