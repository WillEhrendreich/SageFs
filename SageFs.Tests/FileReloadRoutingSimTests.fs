module SageFs.Tests.FileReloadRoutingSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.FileReloadRoutingSim
open SageFs.Simulation.FileReloadRoutingInvariants

/// DST replacement for two slow real-`FileSystemWatcher` Integration tests
/// removed from `DaemonStateChangeContractTests.fs` — file-reload
/// claim-routing (which sessions get FileReloaded for a save under a
/// claimed dir), folded through the REAL
/// `SageFs.LiveTestWatcherCore.apply` / `isUnderWatchedDir` /
/// `sessionsForPath` in milliseconds instead of a real FileSystemWatcher +
/// 75ms debounce + up-to-10s poll.
///
/// See `SageFs.Core/LiveTestWatcherCore.fs` (already a pure decision core —
/// no extraction was needed) and `SageFs.Simulation/FileReloadRoutingSim.fs`
/// for the fold and its two routing twins (broadcast-to-all, double-fire).
/// `SageFs.Tests/LiveTestWatcherCoreTests.fs` stays: it covers the bare pure
/// functions directly (T1-T4 plus properties); this DST folds full
/// multi-op scenarios through the same functions and adds routing-specific
/// invariants plus twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// Assert every invariant holds for a trace, else fail with a replayable dump.
let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

[<Tests>]
let tests =
  testList "DST file-reload claim-routing" [

    testList "the real routing holds the invariants" [

      testPropertyWithConfig simConfig
        "seeded scenarios — the real router never violates each-owning-session-exactly-once or unclaimed-sessions-get-none"
        <| fun (seed: int) -> assertHolds (run (FileReloadRoutingGenerators.fromSeed seed))

      testCase "sharedDirBothOwnersReload: both owning sessions are reloaded, no one else" <| fun _ ->
        let t = run FileReloadRoutingGenerators.sharedDirBothOwnersReload
        t.Final.Drains
        |> List.exactlyOne
        |> fun d -> d.ExpectedOwners |> Expect.equal "both sessions 0 and 1 own the shared dir" (Set.ofList [ 0; 1 ])
        assertHolds t

      testCase "removingOneClaimStopsOnlyItsEvents: only the remaining claimant is reloaded" <| fun _ ->
        let t = run FileReloadRoutingGenerators.removingOneClaimStopsOnlyItsEvents
        let d = t.Final.Drains |> List.exactlyOne
        d.SessionIdxs |> Expect.equal "only session 0 (still claiming) is notified" [ 0 ]
        assertHolds t

      testCase "fallbackSaveReloadsNoOne: a save under the session-less fallback reloads nobody" <| fun _ ->
        let t = run FileReloadRoutingGenerators.fallbackSaveReloadsNoOne
        let d = t.Final.Drains |> List.exactlyOne
        d.SessionIdxs |> Expect.equal "the fallback dir claims no session" []
        assertHolds t

      testCase "staleClaimRemovedDuringDebounce: a claim removed before the drain drops the reload" <| fun _ ->
        let t = run FileReloadRoutingGenerators.staleClaimRemovedDuringDebounce
        let d = t.Final.Drains |> List.exactlyOne
        d.SessionIdxs |> Expect.equal "the claim was gone by drain time, so nobody is reloaded" []
        assertHolds t

      testCase "twoDirsIndependentClaims: no cross-dir leakage between claimants" <| fun _ ->
        let t = run FileReloadRoutingGenerators.twoDirsIndependentClaims
        t.Final.Drains |> List.length |> Expect.equal "both saves drained" 2
        for d in t.Final.Drains do
          d.SessionIdxs |> Expect.equal (sprintf "dir #%d's save is owned only by its own claimant" d.DirIdx) (Set.toList d.ExpectedOwners)
        assertHolds t
    ]

    testList "the twins reproduce plausible attribution bugs" [

      testCase "REPRODUCED — broadcast-to-all twin leaks a save to a non-owning session" <| fun _ ->
        let t = runBroadcastToAll FileReloadRoutingGenerators.twoDirsIndependentClaims
        violations t
        |> List.map fst
        |> Expect.contains "unclaimed-sessions-get-none must fire" "unclaimed-sessions-get-none"

      testCase "REPRODUCED — double-fire twin notifies an owner twice" <| fun _ ->
        let t = runDoubleFire FileReloadRoutingGenerators.sharedDirBothOwnersReload
        violations t
        |> List.map fst
        |> Expect.contains "each-owning-session-gets-exactly-one-FileReloaded-per-save must fire" "each-owning-session-gets-exactly-one-FileReloaded-per-save"

      testPropertyWithConfig simConfig "the invariants have teeth: some seeded scenario violates them under each twin" <|
        fun () ->
          let seeds = [ 1 .. 80 ]
          let anyViolates run' =
            seeds |> List.exists (fun seed -> violations (run' (FileReloadRoutingGenerators.fromSeed seed)) |> List.isEmpty |> not)
          (anyViolates runBroadcastToAll || anyViolates runDoubleFire)
          |> Expect.isTrue "at least one seeded scenario must expose a bug under at least one twin"
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical final state (real reducer)" <|
        fun (seed: int) ->
          let a = run (FileReloadRoutingGenerators.fromSeed seed)
          let b = run (FileReloadRoutingGenerators.fromSeed seed)
          a.Final |> Expect.equal "replaying the same seed yields the identical final fold state" b.Final
    ]
  ]
