module SageFs.Tests.WatcherLifecycleSimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.WatcherLifecycleSim
open SageFs.Simulation.WatcherLifecycleInvariants

/// DST for the two watcher bugs from the roast: a stopped session's
/// directory claim must be fully released (the 148,077-inotify-watches
/// leak) and a watch-buffer overflow must never be folded into an ordinary
/// change (the swallowed hot-reload verdict). See
/// `SageFs.Simulation/WatcherLifecycleSim.fs` for the fold and its twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

[<Tests>]
let tests =
  testList "DST watcher lifecycle" [

    testList "the real fold holds the invariants" [

      testPropertyWithConfig simConfig
        "seeded scenarios — the real fold never orphans a watch, never silences an overflow, and never claims an unseen file"
        <| fun (seed: int) -> assertHolds (run (WatcherLifecycleGenerators.fromSeed seed))

      testCase "threeSessionsCreateThenStopAll: every claim releases, no watch survives past the last stop" <| fun _ ->
        let t = run WatcherLifecycleGenerators.threeSessionsCreateThenStopAll
        t.Final.Live |> Expect.isEmpty "no session is left live"
        t.Final.OpenWatches
        |> Set.filter (fun d -> d <> "/sim/lifecycle/fallback")
        |> Expect.isEmpty "every claimed directory's watch was released when its session stopped"
        assertHolds t

      testCase "sharedDirReleasedOnlyWhenBothStop: the shared dir's watch survives until BOTH claimants stop" <| fun _ ->
        let t = run WatcherLifecycleGenerators.sharedDirReleasedOnlyWhenBothStop
        t.Final.OpenWatches
        |> Set.filter (fun d -> d <> "/sim/lifecycle/fallback")
        |> Expect.isEmpty "both sessions stopped, so the shared directory's watch is released too"
        assertHolds t

      testCase "saveThenOverflowThenSave: each op gets its own outcome, overflow is distinct from either save" <| fun _ ->
        let t = run WatcherLifecycleGenerators.saveThenOverflowThenSave
        t.Final.Outcomes |> List.length |> Expect.equal "three ops, three outcomes" 3
        let overflowOutcome = t.Final.Outcomes |> List.filter (fun o -> o.WasOverflow) |> List.exactlyOne
        match overflowOutcome.Action with
        | FileWatcher.FileChangeAction.RecoverFromOverflow _ -> ()
        | other -> failtestf "expected RecoverFromOverflow, got %A" other
        assertHolds t

      testCase "overflowOnUnclaimedDir: an overflow with no live claimant still produces an honest outcome" <| fun _ ->
        let t = run WatcherLifecycleGenerators.overflowOnUnclaimedDir
        let o = t.Final.Outcomes |> List.exactlyOne
        match o.Action with
        | FileWatcher.FileChangeAction.RecoverFromOverflow _ -> ()
        | other -> failtestf "an unclaimed dir's overflow must still be reported, got %A" other
        assertHolds t

      testCase "rapidCreateStopCycles: repeated create/stop never accumulates an open watch" <| fun _ ->
        let t = run WatcherLifecycleGenerators.rapidCreateStopCycles
        t.Final.OpenWatches
        |> Set.filter (fun d -> d <> "/sim/lifecycle/fallback")
        |> Expect.isEmpty "eight create/stop cycles on the same dir leave nothing open"
        assertHolds t
    ]

    testList "the twins reproduce the actual historical bugs" [

      testCase "REPRODUCED — leaky-stop twin orphans a watch after every session has stopped" <| fun _ ->
        let t = runLeakyStop WatcherLifecycleGenerators.threeSessionsCreateThenStopAll
        violations t
        |> List.map fst
        |> Expect.contains "no-orphaned-watch must fire" "no-orphaned-watch"

      testCase "REPRODUCED — stale-overflow twin folds an overflow into an ordinary SoftReset" <| fun _ ->
        let t = runStaleOverflow WatcherLifecycleGenerators.overflowOnUnclaimedDir
        let o = t.Final.Outcomes |> List.exactlyOne
        o.Action |> Expect.equal "the stale twin reports the same SoftReset a real .fsproj edit would" FileWatcher.FileChangeAction.SoftReset
        violations t
        |> List.map fst
        |> Expect.contains "overflow-never-silent must fire" "overflow-never-silent"

      testPropertyWithConfig simConfig "the invariants have teeth: some seeded scenario violates them under each twin" <|
        fun () ->
          let seeds = [ 1 .. 80 ]
          let anyViolates run' =
            seeds |> List.exists (fun seed -> violations (run' (WatcherLifecycleGenerators.fromSeed seed)) |> List.isEmpty |> not)
          (anyViolates runLeakyStop || anyViolates runStaleOverflow)
          |> Expect.isTrue "at least one seeded scenario must expose a bug under at least one twin"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical final state (real reducer)" <|
        fun (seed: int) ->
          let a = run (WatcherLifecycleGenerators.fromSeed seed)
          let b = run (WatcherLifecycleGenerators.fromSeed seed)
          a.Final |> Expect.equal "replaying the same seed yields the identical final fold state" b.Final
    ]
  ]
