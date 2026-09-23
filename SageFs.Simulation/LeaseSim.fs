namespace SageFs.Simulation

open System
open SageFs
open SageFs.ExpensiveWorkLease

/// Deterministic Simulation Testing for `ExpensiveWorkLease`: several agents
/// requesting, holding, releasing and (sometimes) abandoning leases while
/// pressure rises and falls, folded through the REAL `request`. Same rules
/// as the other DST harnesses here: chaos is data (a seeded event list), the
/// real decision function is the subject, and `requestNeverExpiresTwin`
/// shows the "abandoned leases are eventually reclaimed" invariant has
/// teeth — it FAILS (deadlocks) against a pool that never reclaims.
module LeaseSim =

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// `agent` asks for a lease of `kind` — this IS the retry mechanism too:
    /// an agent that got `Wait` last time and asks again for the same
    /// (agent, kind) is just retrying.
    | Request of agent: string * kind: Kind
    /// `agent` releases the OLDEST lease it currently believes it holds, if
    /// any — a no-op if it holds none.
    | Release of agent: string
    /// `agent` "crashes": forgets about whatever it holds WITHOUT
    /// releasing. The lease stays in the pool until expiry (real) or
    /// forever (the never-expires twin).
    | Abandon of agent: string
    | PressureChange of MemoryPressure
    | PassSeconds of int

  /// Which decision function drives the pool.
  [<RequireQualifiedAccess>]
  type PoolBehavior =
    | Real
    | NeverExpiresTwin

  let private requestFnOf =
    function
    | PoolBehavior.Real -> request
    | PoolBehavior.NeverExpiresTwin -> requestNeverExpiresTwin

  /// One request's outcome, with enough context for invariants to check
  /// capacity and fairness against the EXACT pressure/pool at that moment.
  type DecisionRecord = {
    AtStep: int
    Clock: DateTimeOffset
    Pressure: MemoryPressure
    Holder: string
    Kind: Kind
    Decision: Decision
    /// Pool.Active AFTER this decision was applied — what a capacity check compares against.
    ActiveAfter: ActiveLease list
  }

  type State = {
    Step: int
    Clock: DateTimeOffset
    Pool: PoolState
    Pressure: MemoryPressure
    /// Leases each agent currently believes it holds, oldest first —
    /// `Release` pops the front, `Abandon` forgets everything without
    /// releasing.
    HeldByAgent: Map<string, LeaseId list>
    Decisions: DecisionRecord list
  }

  let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

  let initial : State = {
    Step = 0
    Clock = epoch
    Pool = empty
    Pressure = MemoryPressure.Normal
    HeldByAgent = Map.empty
    Decisions = []
  }

  let step (behavior: PoolBehavior) (s: State) (ev: SimEvent) : State =
    let s = { s with Step = s.Step + 1 }
    let requestFn = requestFnOf behavior
    match ev with
    | SimEvent.PassSeconds n -> { s with Clock = s.Clock.AddSeconds(float n) }
    | SimEvent.PressureChange p -> { s with Pressure = p }
    | SimEvent.Release agent ->
      match Map.tryFind agent s.HeldByAgent with
      | None
      | Some [] -> s
      | Some(leaseId :: rest) ->
        let pool', _ = release leaseId s.Pool
        { s with Pool = pool'; HeldByAgent = Map.add agent rest s.HeldByAgent }
    | SimEvent.Abandon agent -> { s with HeldByAgent = Map.remove agent s.HeldByAgent }
    | SimEvent.Request(agent, kind) ->
      let pool', decision = requestFn s.Clock s.Pressure s.Pool agent kind
      let held' =
        match decision with
        | Decision.Granted(leaseId, _) ->
          let existing = Map.tryFind agent s.HeldByAgent |> Option.defaultValue []
          Map.add agent (existing @ [ leaseId ]) s.HeldByAgent
        | Decision.Wait _
        | Decision.Refused _ -> s.HeldByAgent
      let record =
        { AtStep = s.Step
          Clock = s.Clock
          Pressure = s.Pressure
          Holder = agent
          Kind = kind
          Decision = decision
          ActiveAfter = pool'.Active }
      { s with Pool = pool'; HeldByAgent = held'; Decisions = s.Decisions @ [ record ] }

  type Scenario = { Seed: int; Events: SimEvent list }

  let trace (behavior: PoolBehavior) (scenario: Scenario) : State list = scenario.Events |> List.scan (step behavior) initial

  let private agents = [| "agent-a"; "agent-b"; "agent-c"; "agent-d" |]
  let private kinds = [| Kind.SessionCreateOrWarmup; Kind.Rebuild; Kind.FullBuild; Kind.TestSuiteRun; Kind.RunApp |]

  /// A pure function of `seed`. Sweeps requests, releases, abandons and
  /// pressure swings (both directions) so a sweep of seeds exercises every
  /// path, then winds down to Normal pressure with every agent releasing —
  /// a correctly-fair, non-deadlocking pool must end this with an EMPTY
  /// queue (see `LeaseSimInvariants.queueDrainsOnCooldown`).
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 30 + rng.Next 50
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 10 with
          | 0 | 1 | 2 -> SimEvent.Request(agents.[rng.Next agents.Length], kinds.[rng.Next kinds.Length])
          | 3 -> SimEvent.Release agents.[rng.Next agents.Length]
          | 4 -> SimEvent.Abandon agents.[rng.Next agents.Length]
          | 5 ->
            SimEvent.PressureChange(
              match rng.Next 3 with
              | 0 -> MemoryPressure.Normal
              | 1 -> MemoryPressure.Tight
              | _ -> MemoryPressure.Critical
            )
          | _ -> SimEvent.PassSeconds(1 + rng.Next 120) ]
    // The pool allows a holder at most ONE outstanding claim (active OR
    // queued) at a time — a second, different-kind ask while one is
    // already outstanding is Refused. The generator has no visibility into
    // WHICH kind (if any) each agent is actually still waiting on, so the
    // retry round asks with EVERY kind for every agent: whichever kind an
    // agent genuinely still has queued matches its own entry and is
    // properly retried; every other kind for that agent is harmlessly
    // Refused (a different-kind ask while one is outstanding), never
    // disturbing the real pending entry (Refused only ever drops the
    // REFUSED kind's own stale queue slot, never someone else's).
    let retryEveryKindForEveryAgent =
      [ for a in agents do
          for k in Kind.all -> SimEvent.Request(a, k) ]
    // `request`'s FIFO is STRICT and positional: a queued entry is only
    // ever promoted when its OWN holder retries AND it is exactly the
    // front of the queue at that moment — a later entry never jumps ahead
    // just because capacity would allow it too. So even with cap=4 and 4
    // agents, draining a full queue takes up to 4 SEPARATE retry rounds in
    // the worst case (one entry advances to the front and gets granted per
    // round), the same way real callers each keep retrying on their own
    // backoff until it's genuinely their turn. `agents.Length + 1` rounds
    // is a safe upper bound for however many agents this scenario uses.
    let retryRounds = List.replicate (agents.Length + 1) ()
    let coolDown =
      [ SimEvent.PressureChange MemoryPressure.Normal ]
      @ (agents |> Array.toList |> List.collect (fun a -> [ SimEvent.Release a; SimEvent.Release a ]))
      // Longer than every Kind's TTL (the longest, RunApp, is 4 hours) so
      // even an abandoned RunApp lease has genuinely expired by the retry
      // rounds below — otherwise a still-legitimately-active long lease
      // would look like a false "queue never drains" violation.
      @ [ SimEvent.PassSeconds 20_000 ]
      @ (retryRounds
         |> List.collect (fun () -> retryEveryKindForEveryAgent @ (agents |> Array.toList |> List.map SimEvent.Release)))
    { Seed = seed; Events = events @ coolDown }
