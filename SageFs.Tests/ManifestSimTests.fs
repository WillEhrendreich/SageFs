module SageFs.Tests.ManifestSimTests

open Expecto
open Expecto.Flip
open SageFs.Features.DaemonManifest
open SageFs.Simulation
open SageFs.Simulation.ManifestSim
open SageFs.Simulation.ManifestInvariants

/// Phase 3 DST (Brief B5): the daemon manifest owner's concurrency
/// semantics, reproduced deterministically. See
/// SageFs.Simulation/ManifestSim.fs.
///
/// The payoff is twofold, mirroring Phase 2's worker-lifecycle harness:
///   * Every named invariant HOLDS over generated scenarios when folded
///     through the REAL, faithful single-owner reducer (`ManifestSim.run`,
///     which gates and applies exactly as `SageFs.Core`'s
///     `ManifestOwner.commit` / `ManifestMutation.apply` do) — the owner is
///     provably free of the read-merge-write lost-update race the roast
///     rated HIGH risk (§4).
///   * The SAME `no-lost-update` invariant is VIOLATED, deterministically,
///     when the identical command stream is folded through
///     `ManifestSim.runReadMergeWrite` — a twin modeling the pre-fix shape
///     (independent writers each reading a possibly-stale snapshot of
///     daemon.sagefm and writing their own merge back over it, with no
///     lock). That is the historical race caught, with the exact minimal
///     replay scenario, and the proof the invariant has teeth rather than
///     passing vacuously.
///
/// Pure/in-memory only: this file does no filesystem IO, starts no daemon,
/// and never touches the real ~/.SageFs/daemon.sagefm.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// Assert every invariant holds for a trace, else fail with a replayable dump.
let private assertHolds (t: ManifestTrace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Commands=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Commands vs

[<Tests>]
let tests =
  testList "DST manifest-owner concurrency" [

    testList "the real single-owner reducer holds every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios — no committed mutation is ever lost (real ManifestOwner)" <|
        fun (seed: int) ->
          // fromSeed is a pure function of the seed: this case replays exactly.
          assertHolds (ManifestSim.run (ManifestGenerators.fromSeed seed))

      testCase "clean lifecycle holds" <| fun _ ->
        assertHolds (ManifestSim.run ManifestGenerators.cleanLifecycle)

      testCase "the lost-update-shaped scenario is race-free under the real owner" <| fun _ ->
        let t = ManifestSim.run ManifestGenerators.lostUpdateInterleave
        // Both removes land cumulatively — no session resurrected.
        (List.last t.Steps).StateAfter.Current.Sessions
        |> Expect.isEmpty "both sess-A and sess-B end up removed"
        assertHolds t

      testCase "AfterShutdown sealed: a late periodic-save straggler is rejected, Current untouched" <| fun _ ->
        let t = ManifestSim.run ManifestGenerators.syncAfterShutdown
        let lastStep = List.last t.Steps
        lastStep.Outcome
        |> Expect.equal "the post-shutdown SyncLive-Running straggler is rejected" (Some ApplyOutcome.RejectedAfterShutdown)
        lastStep.StateAfter.Current
        |> Expect.equal "Current is untouched by the rejected straggler" lastStep.StateBefore.Current
        assertHolds t

      testCase "corrupt base fail-closed: no committed history is dropped while unreadable" <| fun _ ->
        let t = ManifestSim.run ManifestGenerators.commitOverCorruptBase
        let rejected = t.Steps |> List.find (fun s -> s.Outcome = Some ApplyOutcome.RejectedBaseUnreadable)
        rejected.StateAfter.Current
        |> Expect.equal "the rejected Remove left Current exactly as it was" rejected.StateBefore.Current
        // The final Read still observes sess-A: nothing was dropped.
        let lastRead = t.Steps |> List.last
        lastRead.ReadResult
        |> Expect.equal "the final Read still sees sess-A — history preserved" (Some rejected.StateBefore.Current)
        assertHolds t
    ]

    testList "the read-merge-write (pre-fix) twin reproduces the lost update" [

      testCase "REPRODUCED — a racing writer's merge clobbers the other's committed removal" <| fun _ ->
        let t = ManifestSim.runReadMergeWrite ManifestGenerators.lostUpdateInterleave
        // sess-A's removal is lost: the twin resurrects it because the second
        // writer (Remove sess-B) merged onto the SAME stale pre-run snapshot
        // that still had sess-A, overwriting the first writer's result.
        (List.last t.Steps).StateAfter.Current.Sessions
        |> Map.containsKey "sess-A"
        |> Expect.isTrue "the twin resurrects sess-A — its removal was silently lost to the racing writer"
        noLostUpdate.Check t
        |> function
           | Outcome.Violated msg -> msg |> Expect.stringContains "the violation names the exact lost mutation" "sess-A"
           | Outcome.Holds -> failtest "expected the read-merge-write twin to VIOLATE no-lost-update"

      testPropertyWithConfig simConfig "the invariant has teeth: some seeded scenario violates no-lost-update under the twin" <|
        fun () ->
          // Across the seed sweep, the generator's back-to-back Apply runs
          // (Read/fault commands are frequent enough to leave real gaps, but
          // dense enough Apply traffic still produces adjacent runs) make the
          // twin violate the invariant; this asserts the harness is not
          // vacuously green. (A single confirmed reproduction is enough.)
          let violated =
            [ 1 .. 60 ]
            |> List.exists (fun seed ->
              match noLostUpdate.Check (ManifestSim.runReadMergeWrite (ManifestGenerators.fromSeed seed)) with
              | Outcome.Violated _ -> true
              | Outcome.Holds -> false)
          violated
          |> Expect.isTrue "at least one seeded scenario must expose the read-merge-write lost-update race"
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical trace (real reducer)" <|
        fun (seed: int) ->
          let a = ManifestSim.run (ManifestGenerators.fromSeed seed)
          let b = ManifestSim.run (ManifestGenerators.fromSeed seed)
          a.Steps |> Expect.equal "replaying the same seed yields the identical trace" b.Steps

      testProperty "same seed => identical trace (read-merge-write twin)" <|
        fun (seed: int) ->
          let a = ManifestSim.runReadMergeWrite (ManifestGenerators.fromSeed seed)
          let b = ManifestSim.runReadMergeWrite (ManifestGenerators.fromSeed seed)
          a.Steps |> Expect.equal "replaying the same seed yields the identical trace" b.Steps
    ]

    testList "ManifestMutation.apply unit decisions (the real pure reducer, sanity-checked directly)" [

      testCase "Remove on an absent session id is a no-op" <| fun _ ->
        ManifestMutation.apply (ManifestMutation.Remove "nope") DaemonManifestState.empty
        |> Expect.equal "no-op" DaemonManifestState.empty

      testCase "SyncLive Running keeps live sessions alive and stamps vanished ones stopped" <| fun _ ->
        let at = System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero)
        let recA : DaemonSessionRecord =
          { SessionId = "A"; Projects = []; WorkingDir = "/w/a"; CreatedAt = at; StoppedAt = None }
        let seeded = ManifestMutation.apply (ManifestMutation.SyncLive([ recA ], None, at, LiveSync.Running)) DaemonManifestState.empty
        let reconciled = ManifestMutation.apply (ManifestMutation.SyncLive([], None, at, LiveSync.Running)) seeded
        reconciled.Sessions.["A"].StoppedAt
        |> Expect.isSome "a session missing from the live list is stamped stopped, not deleted"
    ]
  ]
