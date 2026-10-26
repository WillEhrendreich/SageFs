module SageFs.Tests.ReferenceCacheSimTests

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Simulation
open SageFs.Simulation.ReferenceCacheSim

/// DST for the memory `list_references` leaves behind: after an answer the compiler's cache fits the ceiling, a cache
/// nobody asks for goes after the retention, a machine short on memory holds none, and a release is paid for inside the
/// ask's own budget. See `SageFs.Simulation/ReferenceCacheSim.fs` for the heap, the real holder it drives, and the twins.

let simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let assertHolds (t: Trace) =
  match ReferenceCacheInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Steps=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Steps vs

let violatedIds (t: Trace) : string list = ReferenceCacheInvariants.violations t |> List.map fst

let cachedAtEnd (t: Trace) : int64 =
  match List.tryLast t.Snapshots with
  | Some s -> s.CachedBytes
  | None -> 0L

[<Tests>]
let tests =
  testList "DST reference cache" [

    testList "the real holder holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded afternoons — the cache fits the ceiling after an answer, goes after its retention, is empty on a squeezed machine, and no ask passes its budget"
        <| fun (seed: int) -> assertHolds (run Policy.Real (ReferenceCacheGenerators.fromSeed seed))

      testCase "WHY — growsPastTheCeiling: the second answer leaves the worker within its ceiling and the release is named" <| fun _ ->
        let t = run Policy.Real ReferenceCacheGenerators.growsPastTheCeiling
        assertHolds t
        match t.Releases with
        | [ ReleaseReason.OverResidentCeiling _ ] -> ()
        | other -> failtestf "expected exactly one ceiling release, got %A" other

      testCase "WHY — goesQuiet: after the retention a sweep has dropped the cache" <| fun _ ->
        let t = run Policy.Real ReferenceCacheGenerators.goesQuiet
        assertHolds t
        cachedAtEnd t |> Expect.equal "nothing is cached at the end" 0L
        match t.Releases with
        | [ ReleaseReason.IdlePastRetention _ ] -> ()
        | other -> failtestf "expected exactly one idle release, got %A" other

      testCase "WHY — machineTightens: a sweep on a squeezed machine empties the cache" <| fun _ ->
        let t = run Policy.Real ReferenceCacheGenerators.machineTightens
        assertHolds t
        cachedAtEnd t |> Expect.equal "nothing is cached at the end" 0L

      testCase "WHY — staysWarm: inside the retention and under the ceiling nothing is released and the second ask is cheaper" <| fun _ ->
        let t = run Policy.Real ReferenceCacheGenerators.staysWarm
        assertHolds t
        t.Releases |> Expect.isEmpty "no release"
        let asks = t.Snapshots |> List.filter (fun s -> s.Moment = Moment.AfterAsk)
        match asks with
        | [ first; second ] -> (second.AskSeconds < first.AskSeconds) |> Expect.isTrue "the warm ask is faster"
        | other -> failtestf "expected two asks, got %d" other.Length
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — the never-releases twin keeps a multi-gigabyte cache past the ceiling and past the retention" <| fun _ ->
        let over = run Policy.NeverReleases ReferenceCacheGenerators.growsPastTheCeiling
        violatedIds over |> Expect.contains "bytes over the ceiling must be caught" "an-answer-leaves-no-cache-over-the-ceiling"
        let quiet = run Policy.NeverReleases ReferenceCacheGenerators.goesQuiet
        violatedIds quiet |> Expect.contains "an idle cache must be caught" "an-idle-cache-does-not-outlive-its-retention"
        let tight = run Policy.NeverReleases ReferenceCacheGenerators.machineTightens
        violatedIds tight |> Expect.contains "a squeezed machine must be caught" "a-squeezed-machine-holds-no-cache"

      testCase "REPRODUCED — the ignores-ceiling twin is caught by the ceiling invariant" <| fun _ ->
        let t = run Policy.IgnoresCeiling ReferenceCacheGenerators.growsPastTheCeiling
        violatedIds t |> Expect.contains "caught" "an-answer-leaves-no-cache-over-the-ceiling"

      testCase "REPRODUCED — the ignores-idle twin is caught by the retention invariant" <| fun _ ->
        let t = run Policy.IgnoresIdle ReferenceCacheGenerators.goesQuiet
        violatedIds t |> Expect.contains "caught" "an-idle-cache-does-not-outlive-its-retention"

      testCase "REPRODUCED — the ignores-pressure twin is caught by the pressure invariant" <| fun _ ->
        let t = run Policy.IgnoresPressure ReferenceCacheGenerators.machineTightens
        violatedIds t |> Expect.contains "caught" "a-squeezed-machine-holds-no-cache"

      testCase "the invariants have teeth: some seeded afternoon violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 300 ]
        let anyViolates policy =
          seeds |> List.exists (fun seed -> ReferenceCacheInvariants.violations (run policy (ReferenceCacheGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates Policy.NeverReleases |> Expect.isTrue "never releasing is caught by a generated afternoon"
        anyViolates Policy.IgnoresCeiling |> Expect.isTrue "ignoring the ceiling is caught by a generated afternoon"
        anyViolates Policy.IgnoresIdle |> Expect.isTrue "ignoring idleness is caught by a generated afternoon"
        anyViolates Policy.IgnoresPressure |> Expect.isTrue "ignoring pressure is caught by a generated afternoon"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical snapshots and releases (real holder)" <| fun (seed: int) ->
        let a = run Policy.Real (ReferenceCacheGenerators.fromSeed seed)
        let b = run Policy.Real (ReferenceCacheGenerators.fromSeed seed)
        a.Snapshots = b.Snapshots && a.Releases = b.Releases
    ]
  ]
