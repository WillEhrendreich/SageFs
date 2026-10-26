namespace SageFs.Simulation

open SageFs
open SageFs.Simulation.ReferenceCacheSim

/// Named invariants over a `ReferenceCacheSim.Trace`. Same stable-id / `Holds`-vs-`Violated` shape as the other
/// simulations. Each reads the heap the simulation recorded and never how the holder reached its answer.
module ReferenceCacheInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let firstWhere (t: Trace) (bad: Snapshot -> bool) : Snapshot option =
    t.Snapshots |> List.tryFind bad

  let violated (t: Trace) (s: Snapshot) (message: string) : Outcome =
    Outcome.Violated(
      sprintf
        "%s (seed=%d, at=%.0fs, cached=%dMB, resident=%dMB, ceiling=%dMB, idle=%.0fs of %ds, pressure=%s)"
        message
        t.Scenario.Seed
        s.Now.TotalSeconds
        (s.CachedBytes / megabyte)
        (s.ResidentBytes / megabyte)
        (t.Scenario.CeilingBytes / megabyte)
        s.IdleSeconds
        t.Scenario.RetentionSeconds
        (MemoryPressure.describe s.Pressure)
    )

  let verdict (t: Trace) (found: Snapshot option) (message: string) : Outcome =
    match found with
    | None -> Outcome.Holds
    | Some s -> violated t s message

  /// an-answer-leaves-no-cache-over-the-ceiling: after every answer the compiler holds nothing, or the worker is within
  /// its ceiling. The cache is never what keeps a worker above it.
  let anAnswerLeavesNoCacheOverTheCeiling : Invariant =
    { Id = "an-answer-leaves-no-cache-over-the-ceiling"
      Description = "After every answer, the cache is empty or the worker's resident bytes are within the ceiling."
      Check = fun t ->
        verdict
          t
          (firstWhere t (fun s ->
            s.Moment = Moment.AfterAsk && s.CachedBytes > 0L && s.ResidentBytes > t.Scenario.CeilingBytes))
          "an answer left a cache behind that keeps the worker over its ceiling" }

  /// an-idle-cache-does-not-outlive-its-retention: at every sweep, a cache nobody asked for within the retention is gone.
  let anIdleCacheDoesNotOutliveItsRetention : Invariant =
    { Id = "an-idle-cache-does-not-outlive-its-retention"
      Description = "At every sweep, a cache idle for longer than the retention has been dropped."
      Check = fun t ->
        verdict
          t
          (firstWhere t (fun s ->
            s.Moment = Moment.AfterSweep
            && s.CachedBytes > 0L
            && s.IdleSeconds > float t.Scenario.RetentionSeconds))
          "a sweep left a cache that nobody asked for within the retention" }

  /// a-squeezed-machine-holds-no-cache: whenever the holder was consulted, pressure that is not normal leaves nothing cached.
  let aSqueezedMachineHoldsNoCache : Invariant =
    { Id = "a-squeezed-machine-holds-no-cache"
      Description = "After an answer or a sweep, memory pressure that is not normal means no cache is held."
      Check = fun t ->
        verdict
          t
          (firstWhere t (fun s ->
            s.CachedBytes > 0L
            && (match s.Pressure with
                | MemoryPressure.Normal -> false
                | MemoryPressure.Tight
                | MemoryPressure.Critical -> true)))
          "the machine is short on memory and a cache is still held" }

  /// the-whole-ask-fits-its-budget: a release and the rebuild after it are paid for from the same budget, so an ask
  /// never runs past it.
  let theWholeAskFitsItsBudget : Invariant =
    { Id = "the-whole-ask-fits-its-budget"
      Description = "An ask takes at most the budget by the simulated clock, whether or not a release happened inside it."
      Check = fun t ->
        verdict
          t
          (firstWhere t (fun s -> s.Moment = Moment.AfterAsk && s.AskSeconds > float t.Scenario.BudgetSeconds + 1e-9))
          "an ask ran past its budget" }

  let all : Invariant list =
    [ anAnswerLeavesNoCacheOverTheCeiling
      anIdleCacheDoesNotOutliveItsRetention
      aSqueezedMachineHoldsNoCache
      theWholeAskFitsItsBudget ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
