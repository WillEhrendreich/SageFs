namespace SageFs.Simulation

open System
open SageFs.Features.LiveTesting
open SageFs.HostAgent.TestDebug

/// Deterministic Simulation Testing for debugging one test (`HostAgent.TestDebug`): an editor begins a hold, a debugger
/// attaches or never does, the expiry timer fires early, late or after the hold is gone, the test finishes in any order
/// against all of that, and the host dies mid-hold. The events are data (a seeded list), the subject is the REAL
/// `TestDebug.step`, and the shell's effects (arm an expiry, start the test) are carried out the way `DebugHold` carries
/// them out, except that the world delivers every timer and every test result when the scenario says so.
///
/// Two twins show the invariants have teeth: a hold whose expiry never fires (it parks a test for ever, and a late
/// release then runs it), and a hold that runs the test whether or not a debugger is attached. Each FAILS the invariant
/// built to catch it.
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
    /// The host process ends.
    | HostDies

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// The expiry never does anything: a hold nobody releases stays held.
    | NeverExpiresTwin
    /// A release runs the test whether or not a debugger is attached.
    | RunsUnattachedTwin

  [<RequireQualifiedAccess>]
  type HostLiveness =
    | Alive
    | Dead

  /// One application of the reducer.
  type Applied =
    { Event: HoldEvent
      Before: Hold
      After: Hold
      Effect: HoldEffect }

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
      Debugger: DebuggerPresence
      Host: HostLiveness
      Applied: Applied list
      Answers: Answer list }

  /// The bound the sim's reducer reports in NoDebuggerWithin.
  let bound = TimeSpan.FromTicks 1L

  let initial : State =
    { Hold = Hold.Idle
      Issued = []
      Armed = []
      Running = []
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
    let s = { s with Hold = after; Applied = s.Applied @ [ { Event = event; Before = s.Hold; After = after; Effect = effect } ] }
    match effect, event with
    | HoldEffect.ArmExpiry, HoldEvent.Begin(ticket, _) -> { s with Issued = s.Issued @ [ ticket ]; Armed = s.Armed @ [ ticket ] }
    | HoldEffect.StartTest(ticket, _), _ -> { s with Running = s.Running @ [ ticket ] }
    | _ -> s

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
    | HostLiveness.Alive, SimEvent.DebuggerAttaches -> { s with Debugger = DebuggerPresence.DebuggerAttached }
    | HostLiveness.Alive, SimEvent.DebuggerDetaches -> { s with Debugger = DebuggerPresence.NoDebugger }
    | HostLiveness.Alive, SimEvent.TimerFires pick ->
      match pickFrom pick s.Armed with
      | None -> s
      | Some ticket -> apply behavior { s with Armed = removeAt pick s.Armed } (HoldEvent.Expire ticket)
    | HostLiveness.Alive, SimEvent.TestFinishes pick ->
      match pickFrom pick s.Running with
      | None -> s
      | Some ticket -> apply behavior { s with Running = removeAt pick s.Running } (HoldEvent.Finished(ticket, simResult))
    | HostLiveness.Alive, SimEvent.HostDies ->
      let next = apply behavior s (HoldEvent.HostEnding "the host process ended")
      { next with Host = HostLiveness.Dead; Armed = []; Running = [] }

  type Scenario = { Seed: int; Events: SimEvent list }

  /// Every state the scenario passes through, the first being the start.
  let trace (behavior: Behavior) (scenario: Scenario) : State list = scenario.Events |> List.scan (step behavior) initial

  let final (behavior: Behavior) (scenario: Scenario) : State = trace behavior scenario |> List.last

  /// Enough rounds that every armed timer fires and every running test finishes, whatever the scenario did.
  let private drain (rounds: int) : SimEvent list =
    List.replicate rounds [ SimEvent.TimerFires 0; SimEvent.TestFinishes 0 ] |> List.concat

  /// A pure function of the seed: a run of begins, continues, debugger attaches and detaches, early and stale timer
  /// deliveries, finishes, and (rarely) the host dying, then a drain so the end state is the settled one.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 6 + rng.Next 30
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 20 with
          | 0 | 1 | 2 | 3 -> SimEvent.ClientBegin
          | 4 | 5 | 6 | 7 | 8 -> SimEvent.ClientContinue(rng.Next 100)
          | 9 | 10 -> SimEvent.DebuggerAttaches
          | 11 -> SimEvent.DebuggerDetaches
          | 12 | 13 | 14 -> SimEvent.TimerFires(rng.Next 100)
          | 15 | 16 | 17 -> SimEvent.TestFinishes(rng.Next 100)
          | 18 -> SimEvent.ClientContinue(rng.Next 100)
          | _ ->
            match rng.Next 8 with
            | 0 -> SimEvent.HostDies
            | _ -> SimEvent.ClientBegin ]
    { Seed = seed; Events = events @ drain (n + 1) }

  /// A hold nobody releases, and an expiry that then fires: the shape the never-expires twin gets wrong.
  let abandonedHold : Scenario =
    { Seed = 0; Events = [ SimEvent.ClientBegin; SimEvent.TimerFires 0 ] }

  /// A release with no debugger attached: the shape the runs-unattached twin gets wrong.
  let releaseWithoutDebugger : Scenario =
    { Seed = 0; Events = [ SimEvent.ClientBegin; SimEvent.ClientContinue 0 ] }

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
