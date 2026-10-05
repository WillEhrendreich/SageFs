namespace SageFs.Simulation

open System
open SageFs.Features.LiveTesting
open SageFs.HostAgent.TestDebug

/// Deterministic Simulation Testing for debugging one test (`HostAgent.TestDebug`): an editor begins a hold, a debugger
/// attaches (early, late, or never), the expiry timer fires early, late or after the hold is gone, the test finishes in any
/// order against all of that, and the host dies mid-hold. The events are data (a seeded list), the subject is the REAL
/// `TestDebug.step`, and the shell's effects (arm an expiry, start the test, watch for a debugger that is still attaching)
/// are carried out the way `DebugHold` carries them out, except that the world delivers every timer and every test result
/// when the scenario says so.
///
/// Time is a count of ticks the scenario delivers. A hold released with no debugger attached waits `graceTicks` ticks for
/// one that is mid-attach: a debugger that attaches inside the wait runs the test, one that does not is told it never came.
/// The attach latency is the number of ticks the scenario puts between the release and the attach, so a seed decides it.
///
/// Four twins show the invariants have teeth: a hold whose expiry never fires (it parks a test for ever, and a late
/// release then runs it), a hold that runs the test whether or not a debugger is attached, a hold that refuses at once
/// instead of waiting for the debugger that is mid-attach (the bug this wait fixes), a wait that never ends, a wait that
/// does not hear the debugger arrive, and a wait that does not hear the host die. Each FAILS the invariant built to catch it.
module TestDebugSim =

  /// What the world does next. A pick is an index taken modulo how many candidates there are, so any list of events is a
  /// valid scenario.
  [<RequireQualifiedAccess>]
  type SimEvent =
    /// The editor asks to debug a test.
    | ClientBegin
    /// The editor continues a ticket: one it was given, or (the last pick) one nobody issued.
    | ClientContinue of pick: int
    | DebuggerAttaches
    | DebuggerDetaches
    /// An armed expiry timer fires. One whose hold is already over is still delivered: a timer cannot be unscheduled in time.
    | TimerFires of pick: int
    /// A running test finishes.
    | TestFinishes of pick: int
    /// One tick of the clock the wait for a debugger is measured in.
    | Tick
    /// The host process ends.
    | HostDies

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// The expiry never does anything: a hold nobody releases stays held.
    | NeverExpiresTwin
    /// A release runs the test whether or not a debugger is attached.
    | RunsUnattachedTwin
    /// A release with no debugger refuses at once, as the host did before it waited for a debugger that is mid-attach.
    | RefusesAtOnceTwin
    /// The wait for a debugger never ends: its time running out is not heard.
    | WaitNeverEndsTwin
    /// The wait does not hear a debugger arrive, so it only ever ends by running out.
    | DeafToArrivalTwin
    /// The wait does not hear the host die.
    | DeafToHostDeathTwin

  [<RequireQualifiedAccess>]
  type HostLiveness =
    | Alive
    | Dead

  /// One application of the reducer.
  type Applied =
    { Event: HoldEvent
      Before: Hold
      After: Hold
      Effect: HoldEffect
      /// Whether a debugger was attached at the moment the event was applied.
      DebuggerThen: DebuggerPresence
      /// The tick the event was applied on.
      At: int }

  /// What the editor was told.
  [<RequireQualifiedAccess>]
  type Answer =
    | Continued of ticket: DebugTicket * progress: DebugProgress
    | BeginRefused of holder: DebugTicket
    /// The host is dead, so the editor's call fails and its client reports the host lost.
    | Unreachable

  type State =
    { Hold: Hold
      Issued: DebugTicket list
      Armed: DebugTicket list
      Running: DebugTicket list
      /// The waits for a debugger that are open: the ticket and the tick its time runs out on.
      Windows: (DebugTicket * int) list
      /// Every wait that was ever opened and the tick it opened on, kept so the bound can be checked after the fact.
      Opened: (DebugTicket * int) list
      Now: int
      Debugger: DebuggerPresence
      Host: HostLiveness
      Applied: Applied list
      Answers: Answer list }

  /// The bound the sim's reducer reports in NoDebuggerWithin.
  let bound = TimeSpan.FromTicks 1L

  /// How many ticks the wait for a debugger that is mid-attach lasts.
  let graceTicks = 3

  let initial : State =
    { Hold = Hold.Idle
      Issued = []
      Armed = []
      Running = []
      Windows = []
      Opened = []
      Now = 0
      Debugger = DebuggerPresence.NoDebugger
      Host = HostLiveness.Alive
      Applied = []
      Answers = [] }

  let private stepOf (behavior: Behavior) : TimeSpan -> Hold -> HoldEvent -> Hold * HoldEffect =
    match behavior with
    | Behavior.Real -> step
    | Behavior.NeverExpiresTwin ->
      fun bound hold event ->
        match event with
        | HoldEvent.Expire _ -> hold, HoldEffect.NoEffect
        | other -> step bound hold other
    | Behavior.RunsUnattachedTwin ->
      fun bound hold event ->
        match event with
        | HoldEvent.Release(ticket, _) -> step bound hold (HoldEvent.Release(ticket, DebuggerPresence.DebuggerAttached))
        | other -> step bound hold other
    | Behavior.RefusesAtOnceTwin ->
      fun bound hold event ->
        match event, hold with
        | HoldEvent.Release(ticket, DebuggerPresence.NoDebugger), Hold.Holding(held, _) when ticket = held ->
          Hold.Ended(ticket, DebugEnd.ReleasedWithoutDebugger), HoldEffect.NoEffect
        | _ -> step bound hold event
    | Behavior.WaitNeverEndsTwin ->
      fun bound hold event ->
        match event with
        | HoldEvent.AttachWindowSpent _ -> hold, HoldEffect.NoEffect
        | other -> step bound hold other
    | Behavior.DeafToArrivalTwin ->
      fun bound hold event ->
        match event with
        | HoldEvent.DebuggerArrived _ -> hold, HoldEffect.NoEffect
        | HoldEvent.Release(_, DebuggerPresence.DebuggerAttached) when (match hold with Hold.Awaiting _ -> true | _ -> false) ->
          hold, HoldEffect.NoEffect
        | other -> step bound hold other
    | Behavior.DeafToHostDeathTwin ->
      fun bound hold event ->
        match event, hold with
        | HoldEvent.HostEnding _, Hold.Awaiting _ -> hold, HoldEffect.NoEffect
        | _ -> step bound hold event

  let private simTest : TestCase =
    { Id = TestId.create "sim.test" TestFramework.Expecto
      FullName = "sim.test"
      DisplayName = "sim.test"
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit }

  /// The result every simulated test finishes with.
  let simResult : TestResult = TestResult.Passed(TimeSpan.FromTicks 1L)

  let private pickFrom (pick: int) (items: 'a list) : 'a option =
    match items with
    | [] -> None
    | _ -> Some(List.item (abs (pick % items.Length)) items)

  let private removeAt (pick: int) (items: 'a list) : 'a list =
    match items with
    | [] -> []
    | _ ->
      let index = abs (pick % items.Length)
      items |> List.indexed |> List.filter (fun (i, _) -> i <> index) |> List.map snd

  /// Apply one reducer event and carry out its effect the way the shell does.
  let private apply (behavior: Behavior) (s: State) (event: HoldEvent) : State =
    let after, effect = stepOf behavior bound s.Hold event
    let applied = { Event = event; Before = s.Hold; After = after; Effect = effect; DebuggerThen = s.Debugger; At = s.Now }
    let s = { s with Hold = after; Applied = s.Applied @ [ applied ] }
    match effect, event with
    | HoldEffect.ArmExpiry, HoldEvent.Begin(ticket, _) -> { s with Issued = s.Issued @ [ ticket ]; Armed = s.Armed @ [ ticket ] }
    | HoldEffect.StartTest(ticket, _), _ ->
      // The wait ends the moment the test starts, whichever event started it.
      { s with Running = s.Running @ [ ticket ]; Windows = s.Windows |> List.filter (fun (t, _) -> t <> ticket) }
    | HoldEffect.AwaitAttach ticket, _ ->
      { s with Windows = s.Windows @ [ ticket, s.Now + graceTicks ]; Opened = s.Opened @ [ ticket, s.Now ] }
    | _ -> s

  /// The windows whose time has run out by `now`, in the order they opened.
  let private spentBy (now: int) (s: State) : DebugTicket list =
    s.Windows |> List.filter (fun (_, deadline) -> deadline <= now) |> List.map fst

  let step (behavior: Behavior) (s: State) (event: SimEvent) : State =
    match s.Host, event with
    | HostLiveness.Dead, (SimEvent.ClientBegin | SimEvent.ClientContinue _) -> { s with Answers = s.Answers @ [ Answer.Unreachable ] }
    | HostLiveness.Dead, _ -> s
    | HostLiveness.Alive, SimEvent.ClientBegin ->
      let ticket = DebugTicket(sprintf "sim-%d" (s.Issued.Length + 1))
      let next = apply behavior s (HoldEvent.Begin(ticket, simTest))
      match List.last next.Applied with
      | { Effect = HoldEffect.RefuseOpen holder } -> { next with Answers = next.Answers @ [ Answer.BeginRefused holder ] }
      | _ -> next
    | HostLiveness.Alive, SimEvent.ClientContinue pick ->
      let neverIssued = DebugTicket "sim-never-issued"
      let ticket = pickFrom pick (s.Issued @ [ neverIssued ]) |> Option.defaultValue neverIssued
      let next = apply behavior s (HoldEvent.Release(ticket, s.Debugger))
      { next with Answers = next.Answers @ [ Answer.Continued(ticket, observe ticket next.Hold) ] }
    | HostLiveness.Alive, SimEvent.DebuggerAttaches ->
      // The shell is watching, so every open wait hears the attach the moment it happens.
      let attached = { s with Debugger = DebuggerPresence.DebuggerAttached }
      attached.Windows
      |> List.map fst
      |> List.fold (fun (acc: State) ticket -> apply behavior acc (HoldEvent.DebuggerArrived ticket)) attached
    | HostLiveness.Alive, SimEvent.DebuggerDetaches -> { s with Debugger = DebuggerPresence.NoDebugger }
    | HostLiveness.Alive, SimEvent.TimerFires pick ->
      match pickFrom pick s.Armed with
      | None -> s
      | Some ticket -> apply behavior { s with Armed = removeAt pick s.Armed } (HoldEvent.Expire ticket)
    | HostLiveness.Alive, SimEvent.TestFinishes pick ->
      match pickFrom pick s.Running with
      | None -> s
      | Some ticket -> apply behavior { s with Running = removeAt pick s.Running } (HoldEvent.Finished(ticket, simResult))
    | HostLiveness.Alive, SimEvent.Tick ->
      let ticked = { s with Now = s.Now + 1 }
      let spent = spentBy ticked.Now ticked
      let closed = { ticked with Windows = ticked.Windows |> List.filter (fun (t, _) -> not (List.contains t spent)) }
      spent |> List.fold (fun (acc: State) ticket -> apply behavior acc (HoldEvent.AttachWindowSpent ticket)) closed
    | HostLiveness.Alive, SimEvent.HostDies ->
      let next = apply behavior s (HoldEvent.HostEnding "the host process ended")
      { next with Host = HostLiveness.Dead; Armed = []; Running = []; Windows = [] }

  type Scenario = { Seed: int; Events: SimEvent list }

  /// Every state the scenario passes through, the first being the start.
  let trace (behavior: Behavior) (scenario: Scenario) : State list = scenario.Events |> List.scan (step behavior) initial

  let final (behavior: Behavior) (scenario: Scenario) : State = trace behavior scenario |> List.last

  /// Enough rounds that every armed timer fires, every wait runs out and every running test finishes, whatever the
  /// scenario did.
  let private drainEvents (rounds: int) : SimEvent list =
    [ for _ in 1 .. rounds do
        yield SimEvent.TimerFires 0
        yield SimEvent.TestFinishes 0
      for _ in 1 .. graceTicks + 1 do
        yield SimEvent.Tick
      for _ in 1 .. rounds do
        yield SimEvent.TestFinishes 0 ]

  /// A pure function of the seed: a run of begins, continues, debugger attaches and detaches, early and stale timer
  /// deliveries, ticks, finishes, and (rarely) the host dying, then a drain so the end state is the settled one.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 8 + rng.Next 36
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 26 with
          | 0 | 1 | 2 | 3 -> SimEvent.ClientBegin
          | 4 | 5 | 6 | 7 | 8 -> SimEvent.ClientContinue(rng.Next 100)
          | 9 | 10 -> SimEvent.DebuggerAttaches
          | 11 -> SimEvent.DebuggerDetaches
          | 12 | 13 | 14 -> SimEvent.TimerFires(rng.Next 100)
          | 15 | 16 | 17 -> SimEvent.TestFinishes(rng.Next 100)
          | 18 | 19 | 20 | 21 | 22 -> SimEvent.Tick
          | 23 -> SimEvent.ClientContinue(rng.Next 100)
          | _ ->
            match rng.Next 8 with
            | 0 -> SimEvent.HostDies
            | _ -> SimEvent.ClientBegin ]
    { Seed = seed; Events = events @ drainEvents (n + 1) }

  /// How many ticks after the release a debugger attaches, for a seed: from none to well past the end of the wait.
  let latencyOf (seed: int) : int = Random(seed).Next(0, graceTicks * 3)

  /// A debugger that is mid-attach: the editor begins, releases with no debugger yet, and the debugger arrives
  /// `latencyOf seed` ticks later.
  let attachRace (seed: int) : Scenario =
    { Seed = seed
      Events =
        [ SimEvent.ClientBegin; SimEvent.ClientContinue 0 ]
        @ List.replicate (latencyOf seed) SimEvent.Tick
        @ [ SimEvent.DebuggerAttaches ]
        @ drainEvents 3 }

  /// The host dies somewhere inside the wait, or just after it: the seed picks how many ticks in.
  let hostDeathRace (seed: int) : Scenario =
    let rng = Random seed
    { Seed = seed
      Events =
        [ SimEvent.ClientBegin; SimEvent.ClientContinue 0 ]
        @ List.replicate (rng.Next(0, graceTicks + 1)) SimEvent.Tick
        @ [ SimEvent.HostDies; SimEvent.ClientContinue 0 ]
        @ drainEvents 3 }

  /// A debugger that attaches inside the wait, after which the clock runs on past the end of the wait: a wait that did not
  /// hear the debugger arrive would then refuse it.
  let attachedThenTimePasses : Scenario =
    { Seed = 0
      Events =
        [ SimEvent.ClientBegin; SimEvent.ClientContinue 0; SimEvent.Tick; SimEvent.DebuggerAttaches ]
        @ List.replicate (graceTicks + 1) SimEvent.Tick }

  /// A hold nobody releases, and an expiry that then fires: the shape the never-expires twin gets wrong.
  let abandonedHold : Scenario =
    { Seed = 0; Events = [ SimEvent.ClientBegin; SimEvent.TimerFires 0 ] }

  /// A release with no debugger attached, then the wait running out: the shape the runs-unattached twin gets wrong.
  let releaseWithoutDebugger : Scenario =
    { Seed = 0
      Events = [ SimEvent.ClientBegin; SimEvent.ClientContinue 0 ] @ List.replicate (graceTicks + 1) SimEvent.Tick }

  /// A release that lands before the debugger finishes attaching, which then does, inside the wait.
  let attachesInsideTheWait : Scenario =
    { Seed = 0
      Events = [ SimEvent.ClientBegin; SimEvent.ClientContinue 0; SimEvent.Tick; SimEvent.DebuggerAttaches; SimEvent.TestFinishes 0; SimEvent.ClientContinue 0 ] }

  /// A release with no debugger, and a debugger that arrives only after the wait has run out.
  let attachesAfterTheWait : Scenario =
    { Seed = 0
      Events =
        [ SimEvent.ClientBegin; SimEvent.ClientContinue 0 ]
        @ List.replicate (graceTicks + 1) SimEvent.Tick
        @ [ SimEvent.DebuggerAttaches; SimEvent.ClientContinue 0 ] }

  /// A release with no debugger, then the host dies in the middle of the wait.
  let hostDiesWhileWaiting : Scenario =
    { Seed = 0
      Events = [ SimEvent.ClientBegin; SimEvent.ClientContinue 0; SimEvent.Tick; SimEvent.HostDies; SimEvent.ClientContinue 0 ] }

  /// The editor attaches, releases, and the test finishes: the clean path.
  let attachedRun : Scenario =
    { Seed = 0
      Events = [ SimEvent.DebuggerAttaches; SimEvent.ClientBegin; SimEvent.ClientContinue 0; SimEvent.TestFinishes 0; SimEvent.ClientContinue 0 ] }

  /// The expiry fires first, and a release then arrives for the dropped hold: it must not run the test.
  let lateRelease : Scenario =
    { Seed = 0
      Events = [ SimEvent.DebuggerAttaches; SimEvent.ClientBegin; SimEvent.TimerFires 0; SimEvent.ClientContinue 0 ] }

  /// The host dies with the test running.
  let hostDiesWhileRunning : Scenario =
    { Seed = 0
      Events = [ SimEvent.DebuggerAttaches; SimEvent.ClientBegin; SimEvent.ClientContinue 0; SimEvent.HostDies; SimEvent.ClientContinue 0 ] }
