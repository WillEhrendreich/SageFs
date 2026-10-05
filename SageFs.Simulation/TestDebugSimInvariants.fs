namespace SageFs.Simulation

open SageFs.HostAgent.TestDebug
open SageFs.Simulation.TestDebugSim

/// Named invariants over a `TestDebugSim` trace. The oracle reads the recorded applications and the answers the editor
/// got, and never re-derives a decision itself.
module TestDebugSimInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: State list -> Outcome }

  let private lastState (states: State list) = List.last states

  let private startsOf (applied: Applied list) : (DebugTicket * Applied) list =
    applied
    |> List.choose (fun a ->
      match a.Effect with
      | HoldEffect.StartTest(ticket, _) -> Some(ticket, a)
      | _ -> None)

  let private firstViolation (found: string option) : Outcome =
    match found with
    | Some message -> Outcome.Violated message
    | None -> Outcome.Holds

  /// test-runs-only-with-a-debugger: a test starts only when a debugger is attached at that moment, whether the release
  /// found it there or it arrived while the host waited. Nothing runs for a debugger that is not there.
  let testRunsOnlyWithDebugger : Invariant =
    { Id = "test-runs-only-with-a-debugger"
      Description = "A StartTest effect only ever happens while a debugger is attached, on a Release that saw it or on its arrival."
      Check = fun states ->
        startsOf (lastState states).Applied
        |> List.tryPick (fun (ticket, a) ->
          match a.Event, a.DebuggerThen with
          | HoldEvent.Release(_, DebuggerPresence.DebuggerAttached), DebuggerPresence.DebuggerAttached -> None
          | HoldEvent.DebuggerArrived _, DebuggerPresence.DebuggerAttached -> None
          | other, _ -> Some(sprintf "test for %A started on %A with the debugger %A" ticket other a.DebuggerThen))
        |> firstViolation }

  /// a-ticket-runs-its-test-at-most-once: however many times the editor continues, the test starts once.
  let atMostOneRunPerTicket : Invariant =
    { Id = "a-ticket-runs-its-test-at-most-once"
      Description = "No ticket is ever started twice."
      Check = fun states ->
        startsOf (lastState states).Applied
        |> List.countBy fst
        |> List.tryFind (fun (_, count) -> count > 1)
        |> Option.map (fun (ticket, count) -> sprintf "%A started %d times" ticket count)
        |> firstViolation }

  /// an-expired-hold-never-runs: once the bound has dropped a hold, no later release starts its test.
  let expiredHoldNeverRuns : Invariant =
    { Id = "an-expired-hold-never-runs"
      Description = "A hold that ended in NoDebuggerWithin never has its test started."
      Check = fun states ->
        let applied = (lastState states).Applied
        let expired =
          applied
          |> List.choose (fun a ->
            match a.After with
            | Hold.Ended(ticket, DebugEnd.NoDebuggerWithin _) -> Some ticket
            | _ -> None)
          |> Set.ofList
        startsOf applied
        |> List.tryFind (fun (ticket, _) -> Set.contains ticket expired)
        |> Option.map (fun (ticket, _) -> sprintf "%A expired and was then run" ticket)
        |> firstViolation }

  /// a-refused-begin-changes-nothing: a second hold is turned away and the first is left exactly as it was.
  let refusedBeginChangesNothing : Invariant =
    { Id = "a-refused-begin-changes-nothing"
      Description = "A Begin that is refused leaves the slot as it found it, still held by the ticket named in the refusal."
      Check = fun states ->
        (lastState states).Applied
        |> List.tryPick (fun a ->
          match a.Effect with
          | HoldEffect.RefuseOpen holder ->
            match a.Before = a.After, a.Before with
            | true, Hold.Holding(held, _)
            | true, Hold.Awaiting(held, _)
            | true, Hold.Running held when held = holder -> None
            | _ -> Some(sprintf "a refused Begin changed the slot or named the wrong holder: %A -> %A (holder %A)" a.Before a.After holder)
          | _ -> None)
        |> firstViolation }

  /// every-hold-settles: after the scenario's drain (every timer delivered, every wait run out, every test finished) no
  /// hold is left parked, waiting or running. A hold that cannot end would pin a test, and a door the host opened, for ever.
  let everyHoldSettles : Invariant =
    { Id = "every-hold-settles"
      Description = "After the drain the slot is Idle or Ended, never Holding, Awaiting or Running."
      Check = fun states ->
        match (lastState states).Hold with
        | Hold.Holding(ticket, _) -> Outcome.Violated(sprintf "%A is still held after every timer fired" ticket)
        | Hold.Awaiting(ticket, _) -> Outcome.Violated(sprintf "%A is still waiting for a debugger after every wait ran out" ticket)
        | Hold.Running ticket -> Outcome.Violated(sprintf "%A is still running after every test finished" ticket)
        | Hold.Idle
        | Hold.Ended _ -> Outcome.Holds }

  /// a-dead-host-holds-nothing: once the host has ended, whatever it held is Ended, and nothing was started after.
  let deadHostHoldsNothing : Invariant =
    { Id = "a-dead-host-holds-nothing"
      Description = "After HostEnding the slot is never Holding, Awaiting or Running, and no test starts afterwards."
      Check = fun states ->
        let applied = (lastState states).Applied
        match applied |> List.tryFindIndex (fun a -> match a.Event with HoldEvent.HostEnding _ -> true | _ -> false) with
        | None -> Outcome.Holds
        | Some index ->
          let died = List.item index applied
          let afterwards = applied |> List.skip (index + 1)
          match died.After, startsOf afterwards with
          | (Hold.Holding _ | Hold.Awaiting _ | Hold.Running _), _ -> Outcome.Violated(sprintf "the host ended but the slot is %A" died.After)
          | _, (ticket, _) :: _ -> Outcome.Violated(sprintf "%A started after the host ended" ticket)
          | _ -> Outcome.Holds }

  /// never-refuses-a-debugger-that-is-attached: when the wait runs out and the editor is told no debugger came, none was
  /// attached at that moment. A debugger that attaches inside the wait runs the test.
  let neverRefusesAnAttachedDebugger : Invariant =
    { Id = "never-refuses-a-debugger-that-is-attached"
      Description = "A hold is never ended as ReleasedWithoutDebugger while a debugger is attached."
      Check = fun states ->
        (lastState states).Applied
        |> List.tryPick (fun a ->
          match a.After, a.DebuggerThen with
          | Hold.Ended(ticket, DebugEnd.ReleasedWithoutDebugger), DebuggerPresence.DebuggerAttached when a.Before <> a.After ->
            Some(sprintf "%A was refused for having no debugger while one was attached (event %A)" ticket a.Event)
          | _ -> None)
        |> firstViolation }

  /// a-refusal-comes-only-from-a-spent-wait: the editor is told no debugger came only when the wait for one ran out, never
  /// the instant the test is released.
  let refusalComesOnlyFromASpentWait : Invariant =
    { Id = "a-refusal-comes-only-from-a-spent-wait"
      Description = "A hold ends as ReleasedWithoutDebugger only on AttachWindowSpent."
      Check = fun states ->
        (lastState states).Applied
        |> List.tryPick (fun a ->
          match a.After, a.Event with
          | Hold.Ended(_, DebugEnd.ReleasedWithoutDebugger), HoldEvent.AttachWindowSpent _ -> None
          | Hold.Ended(ticket, DebugEnd.ReleasedWithoutDebugger), event when a.Before <> a.After ->
            Some(sprintf "%A was refused on %A, not when its wait ran out" ticket event)
          | _ -> None)
        |> firstViolation }

  /// never-waits-past-the-bound: a hold waits for a debugger for no more than the wait lasts, however the ticks fall.
  let neverWaitsPastTheBound : Invariant =
    { Id = "never-waits-past-the-bound"
      Description = "No state has a hold still waiting for a debugger once its wait has lasted graceTicks ticks."
      Check = fun states ->
        states
        |> List.tryPick (fun s ->
          match s.Hold with
          | Hold.Awaiting(ticket, _) ->
            match s.Opened |> List.tryFind (fun (t, _) -> t = ticket) with
            | Some(_, openedAt) when s.Now - openedAt >= graceTicks ->
              Some(sprintf "%A has waited %d ticks, the wait lasts %d" ticket (s.Now - openedAt) graceTicks)
            | _ -> None
          | _ -> None)
        |> firstViolation }

  /// answers-are-backed-by-what-happened: the editor is told a test ran under the debugger only if one did and finished,
  /// that no debugger came only if the wait for one ran out, and that the host was lost only if it ended.
  let answersAreTruthful : Invariant =
    { Id = "answers-are-backed-by-what-happened"
      Description = "Attached needs a finished run, NoDebuggerWithin needs an expiry, ReleasedWithoutDebugger needs a wait that ran out, HostLost needs a host that ended."
      Check = fun states ->
        let applied = (lastState states).Applied
        let happened (matches: Applied -> bool) = applied |> List.exists matches
        (lastState states).Answers
        |> List.tryPick (fun answer ->
          match answer with
          | Answer.Continued(ticket, DebugProgress.Ended(DebugEnd.Attached _)) ->
            let ran = happened (fun a -> match a.Event, a.Before with HoldEvent.Finished(t, _), Hold.Running r -> t = ticket && r = ticket | _ -> false)
            match ran with
            | true -> None
            | false -> Some(sprintf "%A was told Attached but no run of it finished" ticket)
          | Answer.Continued(ticket, DebugProgress.Ended(DebugEnd.NoDebuggerWithin _)) ->
            let expired = happened (fun a -> match a.Event, a.Before with HoldEvent.Expire t, Hold.Holding(h, _) -> t = ticket && h = ticket | _ -> false)
            match expired with
            | true -> None
            | false -> Some(sprintf "%A was told NoDebuggerWithin but its hold never expired" ticket)
          | Answer.Continued(ticket, DebugProgress.Ended DebugEnd.ReleasedWithoutDebugger) ->
            let spent = happened (fun a -> match a.Event, a.Before with HoldEvent.AttachWindowSpent t, Hold.Awaiting(h, _) -> t = ticket && h = ticket | _ -> false)
            match spent with
            | true -> None
            | false -> Some(sprintf "%A was told ReleasedWithoutDebugger but its wait for a debugger never ran out" ticket)
          | Answer.Continued(ticket, DebugProgress.Ended(DebugEnd.HostLost _)) ->
            let ended = happened (fun a -> match a.Event with HoldEvent.HostEnding _ -> true | _ -> false)
            match ended with
            | true -> None
            | false -> Some(sprintf "%A was told HostLost but the host never ended" ticket)
          | _ -> None)
        |> firstViolation }

  let all : Invariant list =
    [ testRunsOnlyWithDebugger
      atMostOneRunPerTicket
      expiredHoldNeverRuns
      refusedBeginChangesNothing
      everyHoldSettles
      deadHostHoldsNothing
      neverRefusesAnAttachedDebugger
      refusalComesOnlyFromASpentWait
      neverWaitsPastTheBound
      answersAreTruthful ]

  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun invariant ->
      match invariant.Check states with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some(invariant.Id, message))
