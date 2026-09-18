namespace SageFs.Simulation

open System
open SageFs.Features.DaemonManifest
open SageFs.Simulation.ManifestSim

/// Seeded, dependency-free generators for manifest-owner scenarios (mirrors
/// Phase 2's `MgrGenerators`). Chaos is data: `run (fromSeed n)` replays
/// identically forever. A small, realistic session-id pool means Removes and
/// SyncLives actually interact — most Removes target a session a prior
/// SyncLive actually introduced.
module ManifestGenerators =

  let private sessionPool = [ "sess-A"; "sess-B"; "sess-C"; "sess-D" ]

  let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

  let private record (sid: string) (at: DateTimeOffset) : DaemonSessionRecord =
    { SessionId = sid
      Projects = [ sprintf "%s.fsproj" sid ]
      WorkingDir = sprintf "/work/%s" sid
      CreatedAt = at
      StoppedAt = None }

  /// A mixed stream of Apply(SyncLive/Remove/StampAllStopped)/Read/Shutdown/
  /// faults over `sessionPool`, seeded by `System.Random` so replay is exact.
  let fromSeed (seed: int) : ManifestScenario =
    let rnd = Random(seed)
    let mutable tick = 0
    let nextAt () =
      tick <- tick + 1
      epoch.AddSeconds(float tick)
    // A non-empty-biased subset of the pool, so SyncLive usually has
    // something to reconcile against.
    let livePool () =
      match sessionPool |> List.filter (fun _ -> rnd.Next(0, 2) = 0) with
      | [] -> [ sessionPool.[rnd.Next(0, sessionPool.Length)] ]
      | xs -> xs
    let pickSid () = sessionPool.[rnd.Next(0, sessionPool.Length)]
    let cmds = ResizeArray<OwnerCmd>()
    // Always seed with a SyncLive so early Removes have something to bite.
    cmds.Add(
      OwnerCmd.Apply(
        ManifestMutation.SyncLive(
          sessionPool |> List.map (fun sid -> record sid (nextAt ())),
          None,
          nextAt (),
          LiveSync.Running)))
    let n = rnd.Next(4, 20)
    for _ in 1 .. n do
      match rnd.Next(0, 12) with
      | 0 | 1 ->
        cmds.Add(
          OwnerCmd.Apply(
            ManifestMutation.SyncLive(
              livePool () |> List.map (fun sid -> record sid (nextAt ())),
              None,
              nextAt (),
              LiveSync.Running)))
      | 2 | 3 -> cmds.Add(OwnerCmd.Apply(ManifestMutation.Remove(pickSid ())))
      | 4 -> cmds.Add(OwnerCmd.Apply(ManifestMutation.StampAllStopped(nextAt ())))
      | 5 | 6 -> cmds.Add(OwnerCmd.Read)
      | 7 -> cmds.Add(OwnerCmd.CorruptBase)
      | 8 -> cmds.Add(OwnerCmd.WriteFault)
      | 9 -> cmds.Add(OwnerCmd.Restore)
      | 10 -> cmds.Add(OwnerCmd.Shutdown)
      | _ -> cmds.Add(OwnerCmd.Read)
    { Seed = seed; Commands = List.ofSeq cmds }

  // ── Named canonical race scenarios (worked examples) ──────────────────────

  let private recA = record "sess-A"
  let private recB = record "sess-B"

  /// Two independent writers race the same pre-run Durable snapshot. Real
  /// single-owner: HOLDS — both removes land, cumulatively (session A and B
  /// both end up gone). Read-merge-write twin: VIOLATES `no-lost-update` —
  /// the second writer's merge overwrites the first's, resurrecting session
  /// A (its removal is silently lost).
  let lostUpdateInterleave : ManifestScenario =
    let t0 = epoch
    { Seed = -201
      Commands =
        [ OwnerCmd.Apply(ManifestMutation.SyncLive([ recA t0; recB t0 ], None, t0, LiveSync.Running))
          OwnerCmd.Read // sync point: both racing writers' snapshot starts fresh here
          OwnerCmd.Apply(ManifestMutation.Remove "sess-A")
          OwnerCmd.Apply(ManifestMutation.Remove "sess-B") ] }

  /// A live sync arrives after the shutdown sync already committed — a late
  /// periodic-save timer tick racing the daemon's own shutdown save. Real
  /// owner: the straggler is rejected (AfterShutdown), Current untouched.
  let syncAfterShutdown : ManifestScenario =
    let t0 = epoch
    let t1 = epoch.AddSeconds(1.0)
    { Seed = -202
      Commands =
        [ OwnerCmd.Apply(ManifestMutation.SyncLive([ recA t0 ], None, t0, LiveSync.Running))
          OwnerCmd.Apply(ManifestMutation.SyncLive([], None, t1, LiveSync.ShuttingDown)) // the shutdown save
          OwnerCmd.Apply(ManifestMutation.SyncLive([ recA t0 ], None, t1, LiveSync.Running)) ] } // late periodic-save straggler

  /// A commit is attempted while the base is unreadable (a corrupt file not
  /// yet quarantined). Real owner: rejected, fail-closed — the previously
  /// committed history is never dropped or reset toward empty.
  let commitOverCorruptBase : ManifestScenario =
    let t0 = epoch
    { Seed = -203
      Commands =
        [ OwnerCmd.Apply(ManifestMutation.SyncLive([ recA t0 ], None, t0, LiveSync.Running))
          OwnerCmd.CorruptBase
          OwnerCmd.Apply(ManifestMutation.Remove "sess-A")
          OwnerCmd.Read ] }

  /// A clean, race-free lifecycle: seed two sessions, remove one, shut down.
  /// Holds under BOTH reducers — the control that shows the invariants are
  /// not vacuous.
  let cleanLifecycle : ManifestScenario =
    let t0 = epoch
    let t1 = epoch.AddSeconds(1.0)
    { Seed = -204
      Commands =
        [ OwnerCmd.Apply(ManifestMutation.SyncLive([ recA t0; recB t0 ], None, t0, LiveSync.Running))
          OwnerCmd.Apply(ManifestMutation.Remove "sess-A")
          OwnerCmd.Read
          OwnerCmd.Apply(ManifestMutation.SyncLive([], None, t1, LiveSync.ShuttingDown)) ] }
