namespace SageFs.Simulation

open SageFs
open SageFs.WorkerProtocol

/// Deterministic Simulation Testing for the claim a throwaway worker needs to be safe: it is retired on every way a
/// confirmation can end, whatever order the world answers in.
///
/// Confirmations want a throwaway worker, workers finish starting (late, after a newer confirmation took the place, or
/// after the generation ended), starts fail, generations end, and sessions go, in any order. The fold is the REAL book
/// (`ThrowawayBook.step`). The world around it is what the daemon's owner does with the effects: the processes that
/// really exist (a worker is a process from the moment it starts, before the book hears of it), and the callers still
/// waiting for an answer.
///
/// Same design rules as `BuildConfirmationSim`: chaos is data (an op list is the scheduler), ground truth is kept by
/// the sim's own record, and twins put the bugs back.
module ConfirmThrowawaySim =

  [<RequireQualifiedAccess>]
  type Op =
    /// A confirmation of `generation` in session number `session` wants a throwaway.
    | Want of session: int * generation: int
    /// A started worker (by order of start, modulo those not yet up) comes up.
    | StartDone of which: int
    /// A started worker (the same way) fails to come up.
    | StartFails of which: int
    | End of session: int * generation: int
    | Gone of session: int

  type Scenario = { Seed: int; Ops: Op list }

  /// Three sessions are enough for confirmations to collide.
  let sessionOf (n: int) : SessionId =
    match SessionId.validate (sprintf "%08x" (abs n % 3)) with
    | Ok session -> session
    | Error reason -> invalidOp reason

  type Step =
    { Op: Op
      Before: ThrowawayBook.Book
      After: ThrowawayBook.Book
      Effects: ThrowawayBook.Effect list
      /// The workers that really exist once the effects were performed.
      Live: Map<int, SessionId>
      /// Callers still waiting for an answer once the effects were performed.
      Waiting: Set<SessionId * int64> }

  type Trace =
    { Scenario: Scenario
      Steps: Step list
      Final: ThrowawayBook.Book
      FinalLive: Map<int, SessionId>
      FinalWaiting: Set<SessionId * int64>
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real book.
    | Real
    /// TWIN: a worker that turns up after its generation ended is left running.
    | LeaksLateStart
    /// TWIN: a newer confirmation takes the place without retiring the worker that held it.
    | KeepsSuperseded
    /// TWIN: a newer confirmation takes the place without telling the caller waiting on the older one.
    | StrandsWaiter
    /// TWIN: a session that goes leaves its worker running.
    | IgnoresSessionGone

  /// A start in flight: the worker process exists from the moment it starts, under this handle.
  type private Pending = { Session: SessionId; Generation: int64; Handle: int }

  type private World =
    { Book: ThrowawayBook.Book
      Pending: Pending list
      /// The processes that really exist.
      Live: Map<int, SessionId>
      Waiting: Set<SessionId * int64>
      NextHandle: int
      Steps: Step list }

  let private initial : World =
    { Book = ThrowawayBook.empty; Pending = []; Live = Map.empty; Waiting = Set.empty; NextHandle = 100; Steps = [] }

  let private stepWith (policy: Policy) (book: ThrowawayBook.Book) (event: ThrowawayBook.Event) : ThrowawayBook.Book * ThrowawayBook.Effect list =
    let next, effects = ThrowawayBook.step book event
    let drop (keep: ThrowawayBook.Effect -> bool) = next, effects |> List.filter keep
    match policy, event with
    | Policy.LeaksLateStart, ThrowawayBook.Event.Started _ -> drop (function ThrowawayBook.Effect.Retire _ -> false | _ -> true)
    | Policy.KeepsSuperseded, ThrowawayBook.Event.Wanted _ -> drop (function ThrowawayBook.Effect.Retire _ -> false | _ -> true)
    | Policy.StrandsWaiter, ThrowawayBook.Event.Wanted _ -> drop (function ThrowawayBook.Effect.Fail _ -> false | _ -> true)
    | Policy.IgnoresSessionGone, ThrowawayBook.Event.SessionGone _ -> drop (function ThrowawayBook.Effect.Retire _ -> false | _ -> true)
    | _ -> next, effects

  let private nth (index: int) (items: 'a list) : 'a voption =
    match items with
    | [] -> ValueNone
    | _ -> ValueSome (List.item (((index % List.length items) + List.length items) % List.length items) items)

  /// What the owner does with the effects: starts a worker, stops one, answers a caller.
  let private perform (world: World) (effects: ThrowawayBook.Effect list) : World =
    effects
    |> List.fold
      (fun w effect ->
        match effect with
        | ThrowawayBook.Effect.Start (session, generation) ->
          { w with Pending = w.Pending @ [ { Session = session; Generation = generation; Handle = w.NextHandle } ]; NextHandle = w.NextHandle + 1 }
        | ThrowawayBook.Effect.Retire handle -> { w with Live = Map.remove handle w.Live }
        | ThrowawayBook.Effect.Serve (session, generation, _)
        | ThrowawayBook.Effect.Fail (session, generation, _) -> { w with Waiting = Set.remove (session, generation) w.Waiting })
      world

  let private runEvent (policy: Policy) (op: Op) (world: World) (event: ThrowawayBook.Event) : World =
    let after, effects = stepWith policy world.Book event
    let performed = perform { world with Book = after } effects
    let step =
      { Op = op; Before = world.Book; After = after; Effects = effects; Live = performed.Live; Waiting = performed.Waiting }
    { performed with Steps = step :: performed.Steps }

  let private runOp (policy: Policy) (world: World) (op: Op) : World =
    match op with
    | Op.Want (session, generation) ->
      let key = sessionOf session, int64 generation
      runEvent policy op { world with Waiting = Set.add key world.Waiting } (ThrowawayBook.Event.Wanted (sessionOf session, int64 generation))
    | Op.StartDone which ->
      match nth which world.Pending with
      | ValueNone -> world
      | ValueSome started ->
        // The process is up before anyone has been told.
        let up = { world with Pending = world.Pending |> List.filter (fun p -> p.Handle <> started.Handle); Live = Map.add started.Handle started.Session world.Live }
        runEvent policy op up (ThrowawayBook.Event.Started (started.Session, started.Generation, started.Handle))
    | Op.StartFails which ->
      match nth which world.Pending with
      | ValueNone -> world
      | ValueSome started ->
        let gone = { world with Pending = world.Pending |> List.filter (fun p -> p.Handle <> started.Handle) }
        runEvent policy op gone (ThrowawayBook.Event.StartFailed (started.Session, started.Generation, "the build failed"))
    | Op.End (session, generation) -> runEvent policy op world (ThrowawayBook.Event.Ended (sessionOf session, int64 generation))
    | Op.Gone session -> runEvent policy op world (ThrowawayBook.Event.SessionGone (sessionOf session))

  /// Fairness: every start finishes and every generation ends, so nothing is left pending.
  let private drainOnce (policy: Policy) (world: World) : World =
    let afterStarts = world.Pending |> List.fold (fun w _ -> runOp policy w (Op.StartDone 0)) world
    afterStarts.Book
    |> Map.toList
    |> List.fold
      (fun w (session, entry) -> runEvent policy (Op.End (0, int entry.Generation)) w (ThrowawayBook.Event.Ended (session, entry.Generation)))
      afterStarts

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let folded = scenario.Ops |> List.fold (runOp policy) initial
    let rec drain (world: World) (rounds: int) =
      match rounds, world.Pending, Map.isEmpty world.Book with
      | 0, _, _ -> world
      | _, [], true -> world
      | _ -> drain (drainOnce policy world) (rounds - 1)
    let final = drain folded (List.length scenario.Ops + 8)
    { Scenario = scenario
      Steps = List.rev final.Steps
      Final = final.Book
      FinalLive = final.Live
      FinalWaiting = final.Waiting
      Reducer = name }

  /// The real book.
  let run (scenario: Scenario) : Trace = runWith "real (ThrowawayBook.step)" Policy.Real scenario

  /// TWIN: a worker that turns up late is left running.
  let runLeaksLateStart (scenario: Scenario) : Trace = runWith "twin-leaks-late-start" Policy.LeaksLateStart scenario

  /// TWIN: a replaced worker is left running.
  let runKeepsSuperseded (scenario: Scenario) : Trace = runWith "twin-keeps-superseded" Policy.KeepsSuperseded scenario

  /// TWIN: the caller waiting on a replaced generation is never answered.
  let runStrandsWaiter (scenario: Scenario) : Trace = runWith "twin-strands-waiter" Policy.StrandsWaiter scenario

  /// TWIN: a worker whose session went is left running.
  let runIgnoresSessionGone (scenario: Scenario) : Trace = runWith "twin-ignores-session-gone" Policy.IgnoresSessionGone scenario
