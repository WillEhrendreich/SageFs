namespace SageFs.Simulation

open System
open SageFs.Simulation.StreamingProxySim

/// Seeded, dependency-free generators for `StreamingProxySim` scenarios
/// (mirrors `FileReloadRoutingGenerators`). Chaos is data: `run (fromSeed n)`
/// replays identically forever.
module StreamingProxyGenerators =

  /// A general single-run scenario: a short, varied mix of ops. Same seed
  /// => identical list, forever.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(1, 8)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 6) with
          | 0 -> RunOp.BodyStarts
          | 1 -> RunOp.SchedulerTick
          | 2 -> RunOp.LineArrives
          | 3 -> RunOp.StreamDone
          | 4 -> RunOp.ReadTimeoutFires
          | _ -> RunOp.CallerCancels ]
    { Seed = seed; Ops = ops }

  // ── Named minimal scenarios — the five races named in the brief ────────

  /// Cancel before start: the caller cancels before the body has ever run.
  /// The sharpest form of the pre-start race — the exact scenario that
  /// reproduced 600/600 in the REPL against the pre-fix composition.
  let cancelBeforeStart : Scenario =
    { Seed = 1; Ops = [ RunOp.CallerCancels ] }

  /// Cancel during the first read: the body starts, then the caller cancels
  /// before any line has arrived.
  let cancelDuringFirstRead : Scenario =
    { Seed = 2; Ops = [ RunOp.BodyStarts; RunOp.CallerCancels ] }

  /// Cancel between reads: several lines have already streamed in before
  /// the caller cancels — the mid-stream case the ORIGINAL (still-correct)
  /// StreamingProxyTests.fs test targeted.
  let cancelBetweenReads : Scenario =
    { Seed = 3
      Ops = [ RunOp.BodyStarts; RunOp.LineArrives; RunOp.LineArrives; RunOp.CallerCancels ] }

  /// Cancel racing completion: the stream has ALREADY reported done by the
  /// time a (stale, e.g. superseded-run) cancel signal arrives — the late
  /// cancel must not retroactively flip an already-decided Completed.
  let cancelRacingCompletion : Scenario =
    { Seed = 4
      Ops = [ RunOp.BodyStarts; RunOp.LineArrives; RunOp.StreamDone; RunOp.CallerCancels ] }

  /// A pool that starts the body late: many unrelated scheduler ticks
  /// (thread-pool contention) precede BodyStarts, with the cancel landing
  /// even earlier still — models a busy CI runner where the thread pool
  /// hasn't dequeued the streaming work item yet.
  let poolStartsBodyLate : Scenario =
    { Seed = 5
      Ops =
        [ RunOp.CallerCancels
          RunOp.SchedulerTick
          RunOp.SchedulerTick
          RunOp.SchedulerTick
          RunOp.BodyStarts ] }

  // ── Multi-run interleavings (attribution) ───────────────────────────────

  /// Two runs interleaved: run 0 is superseded (cancelled) while run 1, the
  /// successor, is still mid-flight and finishes cleanly — the exact shape
  /// of a live-testing rebuild superseding an in-flight run. Neither run's
  /// events may influence the other's outcome.
  let twoRunsSupersedeThenComplete : MultiRunScenario =
    { Seed = 10
      InterleavedOps =
        [ 0, RunOp.BodyStarts
          0, RunOp.LineArrives
          1, RunOp.BodyStarts
          0, RunOp.CallerCancels // run 0 superseded
          1, RunOp.LineArrives
          1, RunOp.StreamDone ] }

  /// A general interleaved multi-run scenario over a small pool of RunIds —
  /// same seed => identical timeline, forever.
  let multiFromSeed (seed: int) : MultiRunScenario =
    let rnd = Random(seed)
    let runCount = rnd.Next(2, 4)
    let n = rnd.Next(4, 16)
    let ops =
      [ for _ in 1 .. n ->
          let runId = rnd.Next(0, runCount)
          let op =
            match rnd.Next(0, 6) with
            | 0 -> RunOp.BodyStarts
            | 1 -> RunOp.SchedulerTick
            | 2 -> RunOp.LineArrives
            | 3 -> RunOp.StreamDone
            | 4 -> RunOp.ReadTimeoutFires
            | _ -> RunOp.CallerCancels
          runId, op ]
    { Seed = seed; InterleavedOps = ops }
