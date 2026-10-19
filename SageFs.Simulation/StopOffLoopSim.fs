namespace SageFs.Simulation

/// DST for the claim "a stop never holds the session manager's mailbox".
///
/// THE FAILURE (roast R-SERVER-3, issues #152/#154): `StopSession` ran `StopWorker` INSIDE the one mailbox loop.
/// A stop costs about 5 s because the worker never leaves inside the 2 + 3 s it is given, so while it ran every
/// list, create, restart, get and other stop waited behind it. Four agents stopping a session each queued to
/// 22 s, and the fourth was told "the daemon's session mailbox may be stuck" while its stop was still running.
///
/// THE SHAPE: a scenario is a seeded, ordered list of operations posted to the mailbox, plus `Release s`, the
/// instant session `s`'s worker finally leaves. Nothing sleeps: a stop is held open until the scenario releases
/// it, so the same seed produces the same trace forever. A step records whether the operation was ANSWERED in the
/// step it was posted in ("within one scheduler step") or left PENDING behind a busy loop.
///
/// TWO SUBJECTS share the one `Trace` shape and the one set of invariants:
///   * this file's model, with the loop handling a stop either way (`run`, the spec, reaps aside; `runAwaitsInline`,
///     the twin that blocks the loop the old way), and
///   * the REAL SessionManager mailbox, driven through the same scenarios by a fake runtime whose StopWorker waits on
///     a gate (SageFs.Tests/SessionManagerStopOffMailboxTests.fs).
/// The twin proves the invariants can fail; the real mailbox proves the production code takes the spec's shape.
module StopOffLoopSim =

  /// One operation posted to the mailbox. Sessions are numbered from 1 in creation order.
  [<RequireQualifiedAccess>]
  type Op =
    | Stop of session: int
    | List
    | Get of session: int
    | Create
    | Restart of session: int
    /// Not a command: session `s`'s worker finally leaves, so its stop can end.
    | Release of session: int

  /// Whether the mailbox answered an operation in the step it was posted in.
  [<RequireQualifiedAccess>]
  type Reply =
    | Answered
    | Pending

  type Scenario =
    { Seed: int
      /// Sessions that exist before the first operation (1 .. Sessions).
      Sessions: int
      Ops: Op list }

  /// What one step looked like from outside.
  type Step =
    { Op: Op
      Reply: Reply
      /// Sessions whose worker was leaving (a stop begun, not yet released) when the operation was posted.
      InFlightBefore: Set<int>
      /// The same, once the operation had been handled as far as it could be.
      InFlightAfter: Set<int>
      /// For a `List` that was answered: the sessions it listed. Empty otherwise.
      Listed: Set<int>
      /// Sessions whose stop had been answered by the end of this step.
      StoppedAnswered: Set<int>
      /// Every session created by the end of this step (the initial ones included).
      Created: Set<int> }

  type Trace =
    { Scenario: Scenario
      Steps: Step list
      Reducer: string }

  /// How the model's loop handles a `Stop`.
  [<RequireQualifiedAccess>]
  type Strategy =
    /// The spec: drop the session from the registry, answer at once, let the worker leave on a side task.
    | ReapAside
    /// The twin: the old handler. The loop waits for the worker to leave before it answers or takes anything else.
    | AwaitInline

  /// The model's state. `Queue` only ever holds operations under `AwaitInline`.
  type World =
    { Registered: Set<int>
      StoppedAnswered: Set<int>
      InFlight: Set<int>
      Created: Set<int>
      Next: int
      /// `AwaitInline`: the session whose stop the loop is waiting on.
      Blocked: int option
      /// Workers released before their stop began (the gate was already open).
      PreReleased: Set<int>
      Queue: Op list }

  let initial (s: Scenario) : World =
    let all = Set.ofList [ 1 .. s.Sessions ]
    { Registered = all
      StoppedAnswered = Set.empty
      InFlight = Set.empty
      Created = all
      Next = s.Sessions + 1
      Blocked = None
      PreReleased = Set.empty
      Queue = [] }

  let private stopped (s: int) (w: World) : World =
    { w with Registered = Set.remove s w.Registered; StoppedAnswered = Set.add s w.StoppedAnswered }

  /// Handle one command on a loop that is free to take it. The world, what a `List` listed, and whether it answered.
  let private handle (strategy: Strategy) (w: World) (op: Op) : World * Set<int> * Reply =
    match op, strategy with
    | Op.Create, _ ->
      { w with Registered = Set.add w.Next w.Registered; Created = Set.add w.Next w.Created; Next = w.Next + 1 }, Set.empty, Reply.Answered
    | Op.List, _ -> w, w.Registered, Reply.Answered
    | (Op.Get _ | Op.Restart _), _ -> w, Set.empty, Reply.Answered
    | Op.Stop s, Strategy.ReapAside ->
      let begun =
        match Set.contains s w.PreReleased with
        | true -> w
        | false -> { w with InFlight = Set.add s w.InFlight }
      stopped s begun, Set.empty, Reply.Answered
    | Op.Stop s, Strategy.AwaitInline ->
      match Set.contains s w.PreReleased with
      | true -> stopped s w, Set.empty, Reply.Answered
      | false -> { w with Blocked = Some s; InFlight = Set.add s w.InFlight }, Set.empty, Reply.Pending
    | Op.Release s, _ -> { w with PreReleased = Set.add s w.PreReleased; InFlight = Set.remove s w.InFlight }, Set.empty, Reply.Answered

  /// Take what queued behind a blocked loop, in order, until it blocks again or the queue is empty.
  let rec private drain (strategy: Strategy) (w: World) : World =
    match w.Blocked, w.Queue with
    | None, next :: rest ->
      let after, _, _ = handle strategy { w with Queue = rest } next
      drain strategy after
    | _ -> w

  /// A release lets a loop that was waiting on that worker finish the stop and drain its queue.
  let private afterRelease (strategy: Strategy) (w: World) : World =
    match w.Blocked with
    | Some b when Set.contains b w.PreReleased -> drain strategy (stopped b { w with Blocked = None })
    | Some _ | None -> w

  let private runWith (name: string) (strategy: Strategy) (s: Scenario) : Trace =
    let step (w: World, steps) (op: Op) =
      let before = w.InFlight
      let isRelease = match op with Op.Release _ -> true | _ -> false
      let after, listed, reply =
        match w.Blocked.IsSome && not isRelease with
        | true -> { w with Queue = w.Queue @ [ op ] }, Set.empty, Reply.Pending
        | false ->
          let handled, listed, reply = handle strategy w op
          afterRelease strategy handled, listed, reply
      let recorded =
        { Op = op
          Reply = reply
          InFlightBefore = before
          InFlightAfter = after.InFlight
          Listed = listed
          StoppedAnswered = after.StoppedAnswered
          Created = after.Created }
      after, recorded :: steps
    let _, steps = s.Ops |> List.fold step (initial s, [])
    { Scenario = s; Steps = List.rev steps; Reducer = name }

  /// The spec: a stop is answered at once and its worker leaves on a side task.
  let run (s: Scenario) : Trace = runWith "spec: reap aside" Strategy.ReapAside s

  /// TWIN: the old handler, which waits for the worker inside the loop.
  let runAwaitsInline (s: Scenario) : Trace = runWith "twin: awaits the stop inside the loop" Strategy.AwaitInline s
