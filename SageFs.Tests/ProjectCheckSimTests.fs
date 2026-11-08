module SageFs.Tests.ProjectCheckSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.ProjectCheckSim

/// DST for `check_project`: the arguments a call uses are never older than a file that shaped them, a cancelled run keeps
/// nothing, calls at once share one MSBuild run, and a referenced project is built exactly when it is behind. See
/// `SageFs.Simulation/ProjectCheckSim.fs` for the fold, the independent truth it is checked against, and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match ProjectCheckInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private violatedIds (t: Trace) : string list = ProjectCheckInvariants.violations t |> List.map fst

let private summaries (t: Trace) : RunSummary list = t.Snapshots |> List.choose (fun s -> s.Ran)

[<Tests>]
let tests =
  testList "DST project check" [

    testList "the real decisions hold the invariants" [
      testPropertyWithConfig simConfig
        "seeded histories of edits, builds, checks, cancels and simultaneous calls — arguments are never stale, a cancelled run keeps nothing, calls at once share one run, a reference is built exactly when behind"
        <| fun (seed: int) -> assertHolds (run (ProjectCheckGenerators.fromSeed seed))

      testCase "packagesChangeBetweenChecks: the second check reads the arguments again" <| fun _ ->
        let t = run ProjectCheckGenerators.packagesChangeBetweenChecks
        summaries t |> List.map (fun r -> r.MsBuildRuns) |> Expect.equal "cold, then read again" [ 1; 1 ]
        assertHolds t

      testCase "a check with nothing edited keeps the arguments and runs MSBuild once over three calls" <| fun _ ->
        let t = run { Seed = 410; Ops = [ Op.Check("app", Cancel.NoCancel); Op.Check("app", Cancel.NoCancel); Op.Check("app", Cancel.NoCancel) ] }
        summaries t |> List.map (fun r -> r.MsBuildRuns) |> Expect.equal "one run, then kept" [ 1; 0; 0 ]
        assertHolds t

      testCase "cancelledArgumentsRun: the cancelled run keeps nothing, so the next check reads the arguments" <| fun _ ->
        let t = run ProjectCheckGenerators.cancelledArgumentsRun
        summaries t |> List.map (fun r -> r.MsBuildRuns) |> Expect.equal "both runs happened" [ 1; 1 ]
        assertHolds t

      testCase "twoCallsAtOnce: one MSBuild run serves both" <| fun _ ->
        let t = run ProjectCheckGenerators.twoCallsAtOnce
        (summaries t |> List.head).MsBuildRuns |> Expect.equal "one run" 1
        assertHolds t

      testCase "referencedSourceEdited: core and what stands on it are built once, then nothing" <| fun _ ->
        let t = run ProjectCheckGenerators.referencedSourceEdited
        summaries t |> List.map (fun r -> r.Built) |> Expect.equal "core, lib, then none" [ [ "lib"; "core" ] |> List.rev; [] ]
        assertHolds t

      testCase "externalBuildOfAReference: a reference built from outside leaves what stands on it behind it, so it is built" <| fun _ ->
        let t = run ProjectCheckGenerators.externalBuildOfAReference
        (summaries t |> List.head).Built |> Expect.equal "lib is built against the new core" [ "lib" ]
        assertHolds t

      testCase "buildPropsAppears: a props file created after the arguments were kept refreshes them" <| fun _ ->
        let t = run ProjectCheckGenerators.buildPropsAppears
        summaries t |> List.map (fun r -> r.MsBuildRuns) |> Expect.equal "read again after the props appeared" [ 1; 1 ]
        assertHolds t

      testCase "cancelledReferenceBuild: a cancel while a reference builds stops before any build, and the next check builds" <| fun _ ->
        let t = run ProjectCheckGenerators.cancelledReferenceBuild
        summaries t |> List.map (fun r -> r.Built) |> Expect.equal "none, then core and lib" [ []; [ "core"; "lib" ] ]
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — a cache that ignores Directory.Packages.props checks with stale arguments" <| fun _ ->
        violatedIds (runIgnoresPackagesProps ProjectCheckGenerators.packagesChangeBetweenChecks)
        |> Expect.contains "stale arguments must be caught" "arguments-are-never-older-than-a-file-that-shaped-them"

      testCase "REPRODUCED — a cancelled run that is still recorded changes what the next call sees" <| fun _ ->
        violatedIds (runKeepsCancelledRuns ProjectCheckGenerators.cancelledArgumentsRun)
        |> Expect.contains "a kept half run must be caught" "a-cancelled-run-keeps-nothing"

      testCase "REPRODUCED — calls at once that each start a run are two runs" <| fun _ ->
        violatedIds (runNoCoalescing ProjectCheckGenerators.twoCallsAtOnce)
        |> Expect.contains "two runs for one key must be caught" "calls-at-once-share-one-run"

      testCase "REPRODUCED — a staleness rule that always says current leaves a reference behind" <| fun _ ->
        violatedIds (runNeverBehind ProjectCheckGenerators.referencedSourceEdited)
        |> Expect.contains "a check over a stale reference must be caught" "a-referenced-project-is-built-only-when-behind-and-never-left-behind"

      testCase "REPRODUCED — building every reference on every check builds what was not behind" <| fun _ ->
        violatedIds (runBuildsEverything { Seed = 411; Ops = [ Op.Check("app", Cancel.NoCancel) ] })
        |> Expect.contains "needless builds must be caught" "a-referenced-project-is-built-only-when-behind-and-never-left-behind"

      testCase "the invariants have teeth: some seeded history violates them under each twin" <| fun _ ->
        let seeds = [ 1..300 ]
        let anyViolates runTwin =
          seeds |> List.exists (fun seed -> ProjectCheckInvariants.violations (runTwin (ProjectCheckGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runIgnoresPackagesProps |> Expect.isTrue "ignoring the package versions is caught by a generated history"
        anyViolates runKeepsCancelledRuns |> Expect.isTrue "keeping a cancelled run is caught by a generated history"
        anyViolates runNoCoalescing |> Expect.isTrue "not coalescing is caught by a generated history"
        anyViolates runNeverBehind |> Expect.isTrue "never finding a reference behind is caught by a generated history"
        anyViolates runBuildsEverything |> Expect.isTrue "building everything is caught by a generated history"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical snapshots (real decisions)" <| fun (seed: int) ->
        let a = run (ProjectCheckGenerators.fromSeed seed)
        let b = run (ProjectCheckGenerators.fromSeed seed)
        a.Snapshots = b.Snapshots
    ]
  ]
