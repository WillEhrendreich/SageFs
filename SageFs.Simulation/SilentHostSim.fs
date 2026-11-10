namespace SageFs.Simulation

open System
open SageFs.Features.LiveTesting
open SageFs.HostAgent.TestDebug
open SageFs.SilentHost

/// Deterministic Simulation Testing for a host that is alive and answers nothing (`SilentHost`, and the wait in
/// `FsiHostClient.AgentDebugContinue` that folds it).
///
/// The host is a sim that can freeze and thaw at any tick, the way a debugger stopped at a breakpoint, an inspector, or a
/// killed debugger leaves it: while frozen it has read what it was sent and does nothing, no reply, no timer. When it thaws it
/// handles what it was sent in order and replies. The hold inside it is the REAL `TestDebug.step`. The client is the real
/// `SilentHost.Wait` (first event ends a wait) and `SilentHost.Pulse` (what the daemon knows about the host's voice), with the
/// ceiling the product computes (`Wait.ceilingFor`). Time is ticks the scenario delivers; a seed chooses when the host
/// freezes and thaws, and a scenario that never thaws is a debugger that was killed and left the runtime stopped.
///
/// Three twins show the invariants have teeth: a wait with no ceiling (the bug: the release never answers), a pulse that is
/// never cleared (the session reads degraded for ever after the host is back), and a ceiling that forgot the park (a live
/// host mid-wait is called silent).
module SilentHostSim =

  /// What the host owes a continue before it answers: the park of the product (`Timeouts.debugContinuePark`), in ticks.
  let parkTicks = 4

  /// The grace past the park (`Timeouts.debugHostAnswerGrace`), in ticks.
  let graceTicks = 2

  /// How long a released test waits for a debugger that is still attaching, in ticks.
  let attachTicks = 2

  /// The ceiling a continue is given, in ticks: the same sum the product makes, on the same function.
  let ceilingTicks : int =
    int (Wait.ceilingFor (TimeSpan.FromTicks(int64 parkTicks)) (TimeSpan.FromTicks(int64 graceTicks))).Ticks

  [<RequireQualifiedAccess>]
  type SimEvent =
    | ClientContinue
    | Tick
    | Freeze
    | Thaw
    | DebuggerAttaches
    | DebuggerDetaches
    | TestFinishes

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// The client waits for the host with no ceiling.
    | UnboundedWaitTwin
    /// The pulse ignores the host speaking, so a silent mark is never taken back.
    | NeverClearsPulseTwin
    /// The ceiling is the grace alone, as if the host owed nothing: a host that is working through its park is called silent.
    | CeilingForgetsParkTwin

  type Call =
    { Id: int
      SentAt: int
      Wait: Wait<DebugProgress>
      SettledAt: int option
      /// Whether the host was frozen at any tick between the send and the end of the wait.
      FrozenDuring: bool }

  type State =
    { Now: int
      Hold: Hold
      Frozen: bool
      Debugger: DebuggerPresence
      /// Requests the host has read and not handled yet (it was frozen).
      Inbox: int list
      /// Replies the host owes: the request, and the tick it is due.
      Due: (int * int) list
      Windows: (DebugTicket * int) list
      Running: DebugTicket list
      Calls: Call list
      Pulse: Pulse
      NextId: int }

  let ticket = DebugTicket "sim-1"

  let private simTest : TestCase =
    { Id = TestId.create "sim.test" TestFramework.Expecto
      FullName = "sim.test"
      DisplayName = "sim.test"
      NamePath = []
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit }

  let private simResult : TestResult = TestResult.Passed(TimeSpan.FromTicks 1L)

  let private bound = TimeSpan.FromTicks 1L

  let private silence : HostSilence =
    { HostPid = 1; Waited = TimeSpan.FromTicks(int64 ceilingTicks); Holder = HostHolder.NotFound }

  /// The hold begun, and nothing else yet.
  let initial : State =
    let hold, _ = step bound Hold.Idle (HoldEvent.Begin(ticket, simTest))
    { Now = 0
      Hold = hold
      Frozen = false
      Debugger = DebuggerPresence.NoDebugger
      Inbox = []
      Due = []
      Windows = []
      Running = []
      Calls = []
      Pulse = Pulse.Responsive
      NextId = 1 }

  let private ceilingOf (behavior: Behavior) : int =
    match behavior with
    | Behavior.CeilingForgetsParkTwin -> graceTicks
    | _ -> ceilingTicks

  let private settleOf (behavior: Behavior) (wait: Wait<DebugProgress>) (event: WaitEvent<DebugProgress>) : Wait<DebugProgress> =
    match behavior, event with
    | Behavior.UnboundedWaitTwin, WaitEvent.CeilingSpent -> wait
    | _ -> Wait.settle wait event

  let private pulseOf (behavior: Behavior) (pulse: Pulse) (event: PulseEvent) : Pulse =
    match behavior, event with
    | Behavior.NeverClearsPulseTwin, PulseEvent.HostSpoke -> pulse
    | _ -> Pulse.step pulse event

  /// Apply one hold event with the REAL reducer, and carry out its effect the way the shell does.
  let private hold (s: State) (event: HoldEvent) : State =
    let after, effect = step bound s.Hold event
    let s = { s with Hold = after }
    match effect with
    | HoldEffect.StartTest(started, _) ->
      { s with Running = s.Running @ [ started ]; Windows = s.Windows |> List.filter (fun (t, _) -> t <> started) }
    | HoldEffect.AwaitAttach waiting -> { s with Windows = s.Windows @ [ waiting, s.Now + attachTicks ] }
    | HoldEffect.ArmExpiry
    | HoldEffect.RefuseOpen _
    | HoldEffect.NoEffect -> s

  /// A reply reaches the client: the host spoke, and the call it answers (if still waiting) ends with it.
  let private deliver (behavior: Behavior) (s: State) (request: int) : State =
    let progress = observe ticket s.Hold
    let heard = { s with Pulse = pulseOf behavior s.Pulse PulseEvent.HostSpoke }
    let calls =
      heard.Calls
      |> List.map (fun c ->
        match c.Id = request, c.Wait with
        | true, Wait.Open -> { c with Wait = settleOf behavior c.Wait (WaitEvent.Answer progress); SettledAt = Some heard.Now }
        | _ -> c)
    { heard with Calls = calls }

  /// The host handles one request it has read: release the hold, then owe an answer by the end of its park, or at once if the
  /// hold is over.
  let private handle (s: State) (request: int) : State =
    let released = hold s (HoldEvent.Release(ticket, s.Debugger))
    let dueAt =
      match observe ticket released.Hold with
      | DebugProgress.Ended _ -> released.Now
      | DebugProgress.StillRunning
      | DebugProgress.HostSilent _ -> released.Now + parkTicks
    { released with Due = released.Due @ [ request, dueAt ] }

  /// Replies the host owes that have come due, if it can speak.
  let private emitDue (behavior: Behavior) (s: State) : State =
    match s.Frozen with
    | true -> s
    | false ->
      let due, later = s.Due |> List.partition (fun (_, at) -> at <= s.Now)
      due |> List.fold (fun acc (request, _) -> deliver behavior acc request) { s with Due = later }

  /// Waits whose ceiling has passed end as "no answer", and the host is marked silent.
  let private spendCeilings (behavior: Behavior) (s: State) : State =
    let ceiling = ceilingOf behavior
    let spent =
      s.Calls |> List.filter (fun c -> c.Wait = Wait.Open && s.Now - c.SentAt >= ceiling)
    match spent with
    | [] -> s
    | _ ->
      let calls =
        s.Calls
        |> List.map (fun c ->
          match List.exists (fun (x: Call) -> x.Id = c.Id) spent with
          | false -> c
          | true ->
            match settleOf behavior c.Wait WaitEvent.CeilingSpent with
            | Wait.Open -> c
            | settled -> { c with Wait = settled; SettledAt = Some s.Now })
      // Unbounded twin: nothing settled, so nothing is said.
      let said = calls |> List.exists (fun c -> c.Wait = Wait.Settled WaitEnd.NoAnswer && c.SettledAt = Some s.Now)
      let pulse = match said with | true -> pulseOf behavior s.Pulse (PulseEvent.WentSilent silence) | false -> s.Pulse
      { s with Calls = calls; Pulse = pulse }

  let step (behavior: Behavior) (s: State) (event: SimEvent) : State =
    match event with
    | SimEvent.ClientContinue ->
      let id = s.NextId
      let call = { Id = id; SentAt = s.Now; Wait = Wait.Open; SettledAt = None; FrozenDuring = s.Frozen }
      let sent = { s with NextId = id + 1; Calls = s.Calls @ [ call ] }
      match sent.Frozen with
      | true -> { sent with Inbox = sent.Inbox @ [ id ] }
      | false -> emitDue behavior (handle sent id)
    | SimEvent.Freeze ->
      { s with
          Frozen = true
          Calls = s.Calls |> List.map (fun c -> match c.Wait with | Wait.Open -> { c with FrozenDuring = true } | _ -> c) }
    | SimEvent.Thaw ->
      match s.Frozen with
      | false -> s
      | true ->
        let thawed = { s with Frozen = false; Inbox = [] }
        let handled = s.Inbox |> List.fold handle thawed
        // A debugger that attached while the host was stopped is found by the first look after it runs again.
        let looked =
          match handled.Debugger with
          | DebuggerPresence.DebuggerAttached -> handled.Windows |> List.fold (fun acc (t, _) -> hold acc (HoldEvent.DebuggerArrived t)) handled
          | DebuggerPresence.NoDebugger -> handled
        emitDue behavior looked
    | SimEvent.DebuggerAttaches ->
      let attached = { s with Debugger = DebuggerPresence.DebuggerAttached }
      match s.Frozen with
      | true -> attached
      | false -> attached.Windows |> List.fold (fun acc (t, _) -> hold acc (HoldEvent.DebuggerArrived t)) attached
    | SimEvent.DebuggerDetaches -> { s with Debugger = DebuggerPresence.NoDebugger }
    | SimEvent.TestFinishes ->
      match s.Frozen, s.Running with
      | false, running :: rest -> hold { s with Running = rest } (HoldEvent.Finished(running, simResult))
      | _ -> s
    | SimEvent.Tick ->
      let ticked = { s with Now = s.Now + 1 }
      let windowed =
        match ticked.Frozen with
        | true -> ticked
        | false ->
          let spent = ticked.Windows |> List.filter (fun (_, deadline) -> deadline <= ticked.Now) |> List.map fst
          let closed = { ticked with Windows = ticked.Windows |> List.filter (fun (t, _) -> not (List.contains t spent)) }
          spent |> List.fold (fun acc t -> hold acc (HoldEvent.AttachWindowSpent t)) closed
      windowed |> emitDue behavior |> spendCeilings behavior

  type Scenario = { Seed: int; Events: SimEvent list }

  let trace (behavior: Behavior) (scenario: Scenario) : State list = scenario.Events |> List.scan (step behavior) initial

  let final (behavior: Behavior) (scenario: Scenario) : State = trace behavior scenario |> List.last

  /// Enough for every wait to reach its ceiling, every window to run out and every owed reply to come due.
  let private drain : SimEvent list =
    [ for _ in 1 .. ceilingTicks + parkTicks + attachTicks + 2 -> SimEvent.Tick ]

  /// A pure function of the seed: continues, ticks, freezes and thaws (the host sometimes stays frozen to the end), debugger
  /// attaches and detaches, and test finishes, then a drain so the end state is the settled one.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 10 + rng.Next 40
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 20 with
          | 0 | 1 | 2 | 3 -> SimEvent.ClientContinue
          | 4 | 5 | 6 | 7 | 8 | 9 -> SimEvent.Tick
          | 10 | 11 -> SimEvent.Freeze
          | 12 | 13 -> SimEvent.Thaw
          | 14 | 15 -> SimEvent.DebuggerAttaches
          | 16 -> SimEvent.DebuggerDetaches
          | _ -> SimEvent.TestFinishes ]
    { Seed = seed; Events = events @ drain }

  /// A debugger stops the host, the release is sent, and the debugger lets go some ticks after the ceiling: the seed picks how
  /// many. A second release after the thaw must be answered by the host.
  let freezeThenThaw (seed: int) : Scenario =
    let rng = Random seed
    { Seed = seed
      Events =
        [ SimEvent.DebuggerAttaches; SimEvent.ClientContinue; SimEvent.Freeze; SimEvent.ClientContinue ]
        @ List.replicate (rng.Next(0, ceilingTicks * 3)) SimEvent.Tick
        @ [ SimEvent.Thaw; SimEvent.ClientContinue ]
        @ drain }

  /// A debugger that was killed and left the runtime stopped: it freezes and never thaws, and the editor keeps asking.
  let frozenForGood : Scenario =
    { Seed = 0
      Events =
        [ SimEvent.DebuggerAttaches; SimEvent.ClientContinue; SimEvent.Freeze; SimEvent.ClientContinue ]
        @ List.replicate (ceilingTicks + 1) SimEvent.Tick
        @ [ SimEvent.ClientContinue ]
        @ drain }

  /// The host never froze, and a release is working through its park.
  let neverFrozen : Scenario =
    { Seed = 0; Events = [ SimEvent.DebuggerAttaches; SimEvent.ClientContinue ] @ drain }

module SilentHostInvariants =
  open SageFs.HostAgent.TestDebug
  open SageFs.SilentHost
  open SilentHostSim

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: State list -> Outcome }

  let private last (states: State list) = List.last states

  let private firstViolation (found: string option) : Outcome =
    match found with
    | Some message -> Outcome.Violated message
    | None -> Outcome.Holds

  /// every-call-ends-within-the-ceiling: no release waits past its ceiling, whatever the host did.
  let everyCallEndsWithinTheCeiling : Invariant =
    { Id = "every-call-ends-within-the-ceiling"
      Description = "Every call is settled, and no later than its ceiling after it was sent."
      Check = fun states ->
        (last states).Calls
        |> List.tryPick (fun c ->
          match c.SettledAt with
          | None -> Some(sprintf "call %d sent at tick %d never ended" c.Id c.SentAt)
          | Some at when at - c.SentAt > ceilingTicks -> Some(sprintf "call %d took %d ticks, ceiling %d" c.Id (at - c.SentAt) ceilingTicks)
          | Some _ -> None)
        |> firstViolation }

  /// silence-is-never-false: a call is only ended as "no answer" when the host really was frozen during it.
  let silenceIsNeverFalse : Invariant =
    { Id = "silence-is-never-false"
      Description = "A call that ended with no answer overlapped a freeze of the host."
      Check = fun states ->
        (last states).Calls
        |> List.tryPick (fun c ->
          match c.Wait, c.FrozenDuring with
          | Wait.Settled WaitEnd.NoAnswer, false -> Some(sprintf "call %d was called silent and the host never froze" c.Id)
          | _ -> None)
        |> firstViolation }

  /// the-pulse-never-lies: the host is marked silent only while it is frozen, so once it thaws and speaks the mark is gone.
  let pulseNeverLies : Invariant =
    { Id = "the-pulse-never-lies"
      Description = "At the end the pulse says silent only if the host is frozen at the end."
      Check = fun states ->
        let s = last states
        match s.Pulse, s.Frozen with
        | Pulse.Silent _, false -> Outcome.Violated "the host is back and answering, and the session still reads it as silent"
        | _ -> Outcome.Holds }

  /// a-held-ticket-stays-valid: whatever the freezes did, no answer says the ticket is unknown: the hold is the host's and the
  /// silence did not touch it.
  let heldTicketStaysValid : Invariant =
    { Id = "a-held-ticket-stays-valid"
      Description = "No answer from the host says the hold is gone."
      Check = fun states ->
        (last states).Calls
        |> List.tryPick (fun c ->
          match c.Wait with
          | Wait.Settled(WaitEnd.Replied(DebugProgress.Ended DebugEnd.NoSuchHold)) -> Some(sprintf "call %d was told the hold is gone" c.Id)
          | _ -> None)
        |> firstViolation }

  let all : Invariant list =
    [ everyCallEndsWithinTheCeiling; silenceIsNeverFalse; pulseNeverLies; heldTicketStaysValid ]

  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun invariant ->
      match invariant.Check states with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some(invariant.Id, message))
