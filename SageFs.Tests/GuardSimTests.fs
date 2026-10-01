module SageFs.Tests.GuardSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.GuardSim
open SageFs.Simulation.GuardSimInvariants

/// DST for the timing-sensitive part of a click's guards: the watchdog's stop against the loop it stops, a getter that
/// ends just at the deadline or in the instant before the click gives up, a click abandoning a thread that still runs
/// guarded code, two clicks sharing a helper, and hot reload re-pointing a method while a click holds it. The REAL
/// pieces are folded (the reachability walk, the patch registry, the cells and global stop count, the click lifecycle
/// rule); the op order IS the scheduler. See `SageFs.Simulation/GuardSim.fs` for the model and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 400 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) - replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

/// What was patched right after the step that did `op` (the first such step).
let private patchedAfter (t: Trace) (op: Op) : Set<int> =
  (t.Steps |> List.find (fun s -> s.Op = op)).Observed.Patched

let private firedBy (runTwin: Scenario -> Trace) (scenario: Scenario) : string list =
  violations (runTwin scenario) |> List.map fst

let private anySeedViolates (runTwin: Scenario -> Trace) : bool =
  [ 1 .. 200 ] |> List.exists (fun seed -> not (List.isEmpty (violations (runTwin (GuardSimGenerators.fromSeed seed)))))

[<Tests>]
let tests =
  // The global stop count is shared by everything in the process, so none of this runs beside the other guard cases.
  testSequenced (
    testList "DST getter guards" [

      testList "the real pieces hold the invariants" [
        testPropertyWithConfig simConfig
          "seeded scenarios - a method is patched exactly while a click holds it, stops are counted once and reach only their thread, a reload is never undone"
          (fun (seed: int) -> assertHolds (run (GuardSimGenerators.fromSeed seed)))

        testCase "abandonedThreadKeepsGuards: the click gave up and the thread still runs, so everything it reached is still guarded" <| fun _ ->
          let t = run GuardSimGenerators.abandonedThreadKeepsGuards
          patchedAfter t (Op.GiveUp 0) |> Expect.equal "all four methods the getter reaches" (Set.ofList [ 0; 1; 2; 3 ])
          t.Final.Patched |> Expect.isEmpty "and nothing once the thread has ended"
          assertHolds t

        testCase "endsJustAtTheDeadline: a getter that finishes right after the stop was asked for leaves nothing behind" <| fun _ ->
          let t = run GuardSimGenerators.endsJustAtTheDeadline
          patchedAfter t (Op.ThreadEnds 0) |> Expect.equal "still held until the click looks" (Set.ofList [ 0; 1; 2; 3 ])
          t.Final.Patched |> Expect.isEmpty "released after the click looked"
          t.Final.Pending |> Expect.equal "the stop was let go" 0
          assertHolds t

        testCase "endsBeforeTheVerdict: a thread that ended in the instant before the click gave up was never abandoned" <| fun _ ->
          let t = run GuardSimGenerators.endsBeforeTheVerdict
          t.Final.Patched |> Expect.isEmpty "the click released at its verdict"
          assertHolds t

        testCase "sharedHelperTwoClicks: the first click's release leaves the shared helper guarded for the second" <| fun _ ->
          let t = run GuardSimGenerators.sharedHelperTwoClicks
          (patchedAfter t (Op.Finish 0)).Contains 3 |> Expect.isTrue "method 3 is still patched for the click that still holds it"
          t.Final.Patched |> Expect.isEmpty "and gone when the last click is done"
          assertHolds t

        testCase "reloadDuringClick: a reload that lands while a click holds the method takes the guard off and it never goes back" <| fun _ ->
          let t = run GuardSimGenerators.reloadDuringClick
          (patchedAfter t (Op.Reload 3)).Contains 3 |> Expect.isFalse "the reloaded method is clean"
          t.Final.Patched |> Expect.isEmpty "and stays clean"
          assertHolds t
      ]

      testList "the twins reproduce the bugs the invariants exist for" [
        testCase "REPRODUCED - releasing at give-up leaves an abandoned thread running unguarded code" <| fun _ ->
          firedBy runReleaseAtGiveUp GuardSimGenerators.abandonedThreadKeepsGuards
          |> Expect.contains "abandoned-thread-stays-guarded must fire" "abandoned-thread-stays-guarded"

        testCase "REPRODUCED - never retiring a cell leaks a stop that every later check pays for" <| fun _ ->
          firedBy runNeverRetires GuardSimGenerators.endsJustAtTheDeadline
          |> Expect.contains "pending-count-is-exact must fire" "pending-count-is-exact"

        testCase "REPRODUCED - unpatching without counting holders takes a shared helper's guards off under the other click" <| fun _ ->
          firedBy runUnpatchesShared GuardSimGenerators.sharedHelperTwoClicks
          |> Expect.contains "patched-iff-held must fire" "patched-iff-held"

        testCase "REPRODUCED - a reload that does not take the guard off first leaves a patch on a re-pointed method" <| fun _ ->
          firedBy runReloadKeepsPatch GuardSimGenerators.reloadDuringClick
          |> Expect.contains "reloaded-never-patched must fire" "reloaded-never-patched"

        testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
          anySeedViolates runReleaseAtGiveUp |> Expect.isTrue "release-at-give-up is caught by a generated scenario"
          anySeedViolates runNeverRetires |> Expect.isTrue "never-retires is caught by a generated scenario"
          anySeedViolates runUnpatchesShared |> Expect.isTrue "unpatches-shared is caught by a generated scenario"
          anySeedViolates runReloadKeepsPatch |> Expect.isTrue "reload-keeps-patch is caught by a generated scenario"
      ]

      testList "determinism / replay" [
        testProperty "same seed => identical trace (real pieces)" <| fun (seed: int) ->
          let a = run (GuardSimGenerators.fromSeed seed)
          let b = run (GuardSimGenerators.fromSeed seed)
          a.Steps |> List.map (fun s -> s.Observed) = (b.Steps |> List.map (fun s -> s.Observed))
      ]
    ])
