module SageFs.Tests.SourceStateSimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.RunReceipts
open SageFs.Simulation.SourceStateSim

/// DST for the claim a receipt makes about its source. The decision under test is the real `SourceState.decide`; see
/// `SageFs.Simulation/SourceStateSim.fs` for the virtual disk and worker around it, the op order that serves as the scheduler,
/// and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private violatedIds (t: Trace) : string list = violations t |> List.map fst

let private lastStep (t: Trace) : Step = List.last t.Steps

/// Every seed that makes the twin break an invariant, out of the first `n`.
let private caughtBy (twin: Scenario -> Trace) (id: string) (n: int) : int =
  [ 1 .. n ] |> List.filter (fun seed -> violatedIds (twin (Generators.fromSeed seed)) |> List.contains id) |> List.length

[<Tests>]
let sourceStateSimTests =
  testList "DST source state" [

    testList "the real decision holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios: never green over stale, a rebuild is never in sync, unknown is never in sync, in sync is earned"
        <| fun (seed: int) -> assertHolds (run (Generators.fromSeed seed))

      testCase "untouched: nothing changed since the build, so the source is in sync and the run is AllPassed" <| fun _ ->
        let t = run Generators.untouched
        (lastStep t).Verdict |> Expect.equal "plain AllPassed" RunVerdict.AllPassed
        assertHolds t

      testCase "editedNotRebuilt: the Nehemiah case: an edit with no rebuild is passed-on-stale, not AllPassed" <| fun _ ->
        let t = run Generators.editedNotRebuilt
        (lastStep t).Verdict |> Expect.equal "passed on stale source" RunVerdict.PassedOnStaleSource
        assertHolds t

      testCase "rebuiltAfterEdit: after the rebuild finishes and the worker loads it, the source is in sync again" <| fun _ ->
        let t = run Generators.rebuiltAfterEdit
        (lastStep t).Verdict |> Expect.equal "AllPassed after a rebuild" RunVerdict.AllPassed
        assertHolds t

      testCase "rebuildInFlight: while the rebuild runs the source is Rebuilding" <| fun _ ->
        let t = run Generators.rebuildInFlight
        (lastStep t).Verdict |> Expect.equal "passed while rebuilding" RunVerdict.PassedWhileRebuilding
        assertHolds t

      testCase "unreadable: a file that cannot be read makes the source Unknown" <| fun _ ->
        let t = run Generators.unreadable
        (lastStep t).Verdict |> Expect.equal "passed on an unknown source" RunVerdict.PassedOnUnknownSource
        assertHolds t

      testCase "builtNotLoaded: a build that finished while the worker kept its old one is stale" <| fun _ ->
        let t = run Generators.builtNotLoaded
        (lastStep t).Verdict |> Expect.equal "passed on stale source" RunVerdict.PassedOnStaleSource
        assertHolds t

      testCase "externalBuild: an outside build is a newer build than the worker loaded, so stale" <| fun _ ->
        let t = run Generators.externalBuild
        (lastStep t).Verdict |> Expect.equal "passed on stale source" RunVerdict.PassedOnStaleSource
        assertHolds t

      testCase "silentWorker: a worker that did not say when it loaded leaves the source Unknown" <| fun _ ->
        let t = run Generators.silentWorker
        (lastStep t).Verdict |> Expect.equal "passed on an unknown source" RunVerdict.PassedOnUnknownSource
        assertHolds t

      testCase "recordSaysBuilt: a rebuild record that says finished does not hide a later edit" <| fun _ ->
        let t = run Generators.recordSaysBuilt
        (lastStep t).Verdict |> Expect.equal "passed on stale source" RunVerdict.PassedOnStaleSource
        assertHolds t

      testCase "buildFails: a build that fails changes nothing the worker runs, and the edit is still stale" <| fun _ ->
        let t = run Generators.buildFails
        (lastStep t).Verdict |> Expect.equal "passed on stale source" RunVerdict.PassedOnStaleSource
        assertHolds t
    ]

    testList "the twins are caught" [
      testCase "twin-ignores-mtime: an edit after the build reads as in sync, so a run is AllPassed over stale source" <| fun _ ->
        violatedIds (runIgnoresMtime Generators.editedNotRebuilt) |> Expect.contains "caught by never-green-over-stale" "never-green-over-stale"
        (caughtBy runIgnoresMtime "never-green-over-stale" 300, 10) |> Expect.isGreaterThan "caught on many seeds, not one lucky one"

      testCase "twin-trusts-last-build-stamp: believing the rebuild record over the files lets an edit after the build through" <| fun _ ->
        violatedIds (runTrustsLastBuildStamp Generators.recordSaysBuilt) |> Expect.contains "caught by never-green-over-stale" "never-green-over-stale"
        (caughtBy runTrustsLastBuildStamp "never-green-over-stale" 300, 10) |> Expect.isGreaterThan "caught on many seeds"

      testCase "twin-swallows-read-failure: a file that cannot be read reads as built, so the source is InSync" <| fun _ ->
        violatedIds (runSwallowsReadFailure Generators.unreadable) |> Expect.contains "caught by unknown-is-never-insync" "unknown-is-never-insync"
        (caughtBy runSwallowsReadFailure "unknown-is-never-insync" 300, 10) |> Expect.isGreaterThan "caught on many seeds"

      testCase "twin-ignores-mtime also misses a rebuild that never reached the worker" <| fun _ ->
        violatedIds (runIgnoresMtime Generators.builtNotLoaded) |> Expect.contains "caught by never-green-over-stale" "never-green-over-stale"
    ]
  ]
