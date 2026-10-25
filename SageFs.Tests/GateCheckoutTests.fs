/// A gate that is killed leaves the checkout's build output half made for a commit the tree is no longer at, and the
/// next run died in zero seconds on a FileNotFoundException naming a SageFs.Core version the tree had moved past.
/// `SageFs.GateCheckout` decides what the next run does about it, and these pin the decision and the clearing.
module SageFs.Tests.GateCheckoutTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.GateCheckout

let private sha40 (c: char) = String(c, 40)
let private sha1 = sha40 'a'
let private sha2 = sha40 'b'

/// A temporary checkout, removed afterwards however the case ends.
let private withTree (build: string -> unit) (check: string -> unit) : unit =
  let root = Path.Combine(Path.GetTempPath(), "sagefs-gate-checkout-" + Guid.NewGuid().ToString "N")
  try
    Directory.CreateDirectory root |> ignore
    build root
    check root
  finally
    try Directory.Delete(root, true) with _ -> ()

let private mkdir (root: string) (relative: string) = Directory.CreateDirectory(Path.Combine(root, relative)) |> ignore
let private touch (root: string) (relative: string) = File.WriteAllText(Path.Combine(root, relative), "x")

[<Tests>]
let recordTests =
  testList "gate checkout record" [

    testCase "WHY — a run that began and never wrote its end is read back as interrupted, because that is exactly what a kill leaves" <| fun _ ->
      parseRecord (startedRecord sha1)
      |> Expect.equal "started with no finish is interrupted" (CheckoutBuild.Interrupted sha1)

    testCase "WHY — a run that wrote its end is read back as finished, whether it passed or failed" <| fun _ ->
      parseRecord (finishedRecord sha1)
      |> Expect.equal "finished is finished" (CheckoutBuild.Finished sha1)

    testCase "WHY — a record that says nothing usable is read as never recorded, so an unreadable file cannot pass for a clean one" <| fun _ ->
      [ ""; "   "; "garbage"; "started"; "finished "; "started a b" ]
      |> List.map parseRecord
      |> List.distinct
      |> Expect.equal "every unusable record reads the same way" [ CheckoutBuild.NeverRecorded ]

    testCase "WHY — every state writes a record that reads back as that state, the empty one included" <| fun _ ->
      [ CheckoutBuild.NeverRecorded; CheckoutBuild.Interrupted sha1; CheckoutBuild.Finished sha2 ]
      |> List.map (fun state -> state, parseRecord (recordOf state))
      |> List.filter (fun (written, readBack) -> written <> readBack)
      |> Expect.isEmpty "no state is changed by being written and read"

    testCase "WHY — whatever sha a record carries comes back out of it unchanged" <| fun _ ->
      let shaGen = Gen.elements [ 'a'; 'b'; 'c'; '0'; '9' ] |> Gen.map sha40
      let property =
        Prop.forAll (Arb.fromGen shaGen) (fun sha ->
          parseRecord (startedRecord sha) = CheckoutBuild.Interrupted sha
          && parseRecord (finishedRecord sha) = CheckoutBuild.Finished sha)
      Check.QuickThrowOnFailure property
  ]

[<Tests>]
let decisionTests =
  testList "gate checkout decision" [

    testCase "WHY — an interrupted run's build output is cleared before the next run, and the reason names the commit it was made for" <| fun _ ->
      match decide (CheckoutBuild.Interrupted sha1) with
      | OutputDecision.Clear reason -> reason |> Expect.stringContains "the reason names the interrupted commit" (sha1.Substring(0, 8))
      | OutputDecision.Keep -> failtest "an interrupted run's output must not be kept"

    testCase "WHY — a finished run's output is kept, because the warm build is the reason the checkout persists" <| fun _ ->
      decide (CheckoutBuild.Finished sha1)
      |> Expect.equal "a clean finish keeps the output" OutputDecision.Keep

    testCase "WHY — a checkout nobody recorded anything for is cleared, since nothing says its output is whole" <| fun _ ->
      match decide CheckoutBuild.NeverRecorded with
      | OutputDecision.Clear _ -> ()
      | OutputDecision.Keep -> failtest "unknown output must not be kept"

    testCase "WHY — a pipeline the clock or a signal ended is interrupted, and any other exit, a red one included, finished" <| fun _ ->
      afterPipeline true sha1 |> Expect.equal "cancelled interrupts" (CheckoutBuild.Interrupted sha1)
      afterPipeline false sha1 |> Expect.equal "anything else finishes" (CheckoutBuild.Finished sha1)
  ]

[<Tests>]
let clearingTests =
  testList "gate checkout clearing" [

    testCase "WHY — only the output beside a project file is found, and node_modules and a project-less bin are left alone" <| fun _ ->
      withTree
        (fun root ->
          mkdir root "Core/bin/Release"; mkdir root "Core/obj"; touch root "Core/Core.fsproj"
          mkdir root "Tests/fixtures/F/bin"; touch root "Tests/fixtures/F/F.csproj"
          mkdir root "node_modules/pkg/bin"; touch root "node_modules/pkg/pkg.csproj"
          mkdir root "docs/bin")
        (fun root ->
          outputDirs root
          |> List.map (fun dir -> Path.GetRelativePath(root, dir))
          |> List.sort
          |> Expect.equal "project output only" [ "Core/bin"; "Core/obj"; "Tests/fixtures/F/bin" ])

    testCase "WHY — an interrupted record clears the output, leaves the sources and node_modules, and says what it removed" <| fun _ ->
      let state = Path.Combine(Path.GetTempPath(), "sagefs-gate-state-" + Guid.NewGuid().ToString "N")
      try
        withTree
          (fun root ->
            mkdir root "Core/bin"; mkdir root "Core/obj"; touch root "Core/Core.fsproj"; touch root "Core/Lib.fs"
            mkdir root "node_modules/pkg/bin"; touch root "node_modules/pkg/pkg.csproj"
            record state root (CheckoutBuild.Interrupted sha1) |> Expect.isOk "the record is written")
          (fun root ->
            let result = prepare state root
            result.Cleared |> List.map (fun dir -> Path.GetRelativePath(root, dir)) |> List.sort
            |> Expect.equal "both output directories went" [ "Core/bin"; "Core/obj" ]
            result.Failed |> Expect.isEmpty "nothing refused to go"
            Directory.Exists(Path.Combine(root, "Core/bin")) |> Expect.isFalse "the output is gone"
            File.Exists(Path.Combine(root, "Core/Lib.fs")) |> Expect.isTrue "the source stays"
            Directory.Exists(Path.Combine(root, "node_modules/pkg/bin")) |> Expect.isTrue "the warm packages stay")
      finally
        try Directory.Delete(state, true) with _ -> ()

    testCase "WHY — a finished record keeps the output, so a clean run does not pay for a cold build" <| fun _ ->
      let state = Path.Combine(Path.GetTempPath(), "sagefs-gate-state-" + Guid.NewGuid().ToString "N")
      try
        withTree
          (fun root ->
            mkdir root "Core/bin"; touch root "Core/Core.fsproj"
            record state root (CheckoutBuild.Finished sha1) |> Expect.isOk "the record is written")
          (fun root ->
            let result = prepare state root
            result.Decision |> Expect.equal "kept" OutputDecision.Keep
            result.Cleared |> Expect.isEmpty "nothing was cleared"
            Directory.Exists(Path.Combine(root, "Core/bin")) |> Expect.isTrue "the output stays")
      finally
        try Directory.Delete(state, true) with _ -> ()

    testCase "WHY — a checkout with no record at all is cleared, because nothing says its output is whole" <| fun _ ->
      let state = Path.Combine(Path.GetTempPath(), "sagefs-gate-state-" + Guid.NewGuid().ToString "N")
      withTree
        (fun root -> mkdir root "Core/obj"; touch root "Core/Core.fsproj")
        (fun root ->
          let result = prepare state root
          result.Cleared |> List.length |> Expect.equal "the output went" 1
          Directory.Exists(Path.Combine(root, "Core/obj")) |> Expect.isFalse "the output is gone")
  ]
