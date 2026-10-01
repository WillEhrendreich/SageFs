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

  let private startsOf (applied: Applied list) : (DebugTicket * HoldEvent) list =
    applied
    |> List.choose (fun a ->
      match a.Effect with
      | HoldEffect.StartTest(ticket, _) -> Some(ticket, a.Event)
      | _ -> None)

  let private firstViolation (found: string option) : Outcome =
    match found with
    | Some message -> Outcome.Violated message
    | None -> Outcome.Holds

  /// test-runs-only-with-a-debugger: a test starts only on a release that found a debugger attached. Nothing runs for a
  /// debugger that is not there.
  let testRunsOnlyWithDebugger : Invariant =
    { Id = "test-runs-only-with-a-debugger"
      Description = "A StartTest effect only ever follows a Release that saw a debugger attached."
      Check = fun states ->
        startsOf (lastState states).Applied
        |> List.tryPick (fun (ticket, event) ->
          match event with
          | HoldEvent.Release(_, DebuggerPresence.DebuggerAttached) -> None
          | other -> Some(sprintf "test for %A started on %A, which did not see a debugger" ticket other))
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
            | true, Hold.Running held when held = holder -> None
            | _ -> Some(sprintf "a refused Begin changed the slot or named the wrong holder: %A -> %A (holder %A)" a.Before a.After holder)
          | _ -> None)
        |> firstViolation }

  /// every-hold-settles: after the scenario's drain (every timer delivered, every test finished) no hold is left parked or
  /// running. A hold that cannot end would pin a test, and a door the host opened, for ever.
  let everyHoldSettles : Invariant =
    { Id = "every-hold-settles"
      Description = "After the drain the slot is Idle or Ended, never Holding or Running."
      Check = fun states ->
        match (lastState states).Hold with
        | Hold.Holding(ticket, _) -> Outcome.Violated(sprintf "%A is still held after every timer fired" ticket)
        | Hold.Running ticket -> Outcome.Violated(sprintf "%A is still running after every test finished" ticket)
        | Hold.Idle
        | Hold.Ended _ -> Outcome.Holds }

  /// a-dead-host-holds-nothing: once the host has ended, whatever it held is Ended, and nothing was started after.
  let deadHostHoldsNothing : Invariant =
    { Id = "a-dead-host-holds-nothing"
      Description = "After HostEnding the slot is never Holding or Running, and no test starts afterwards."
      Check = fun states ->
        let applied = (lastState states).Applied
        match applied |> List.tryFindIndex (fun a -> match a.Event with HoldEvent.HostEnding _ -> true | _ -> false) with
        | None -> Outcome.Holds
        | Some index ->
          let died = List.item index applied
          let afterwards = applied |> List.skip (index + 1)
          match died.After, startsOf afterwards with
          | (Hold.Holding _ | Hold.Running _), _ -> Outcome.Violated(sprintf "the host ended but the slot is %A" died.After)
          | _, (ticket, _) :: _ -> Outcome.Violated(sprintf "%A started after the host ended" ticket)
          | _ -> Outcome.Holds }

  /// answers-are-backed-by-what-happened: the editor is told a test ran under the debugger only if one did and finished,
  /// that no debugger came only if the hold expired, and that the host was lost only if it ended.
  let answersAreTruthful : Invariant =
    { Id = "answers-are-backed-by-what-happened"
      Description = "Attached needs a finished run, NoDebuggerWithin needs an expiry, ReleasedWithoutDebugger needs a release that saw no debugger, HostLost needs a host that ended."
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
            let released = happened (fun a -> match a.Event with HoldEvent.Release(t, DebuggerPresence.NoDebugger) -> t = ticket | _ -> false)
            match released with
            | true -> None
            | false -> Some(sprintf "%A was told ReleasedWithoutDebugger but no release saw an absent debugger" ticket)
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
      answersAreTruthful ]

  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun invariant ->
      match invariant.Check states with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some(invariant.Id, message))
