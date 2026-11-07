namespace SageFs

open System.Threading.Tasks
open SageFs.McpTools
open SageFs.McpSessionRouting
open SageFs.DebugTestRequest

/// `POST /api/live-testing/debug` and `/debug/continue`: the daemon's door for debugging one test from an editor.
/// It finds the session and the test, and hands the rest to `DebugTestRequest`, which asks the session's worker. It
/// runs no test and keeps no state: the host that runs the test holds the hold.
module McpDebugTest =

  /// What the editor asked to debug.
  type DebugRequest =
    { SessionId: string option
      WorkingDirectory: string option
      Selector: TestSelector }

  /// Where the editor's debugger has got to for a held test.
  type ContinueRequest =
    { SessionId: string option
      WorkingDirectory: string option
      Ticket: string }

  /// What the daemon keeps between "hold this test" and the end of the hold: which test a ticket holds, so the verdict the
  /// host reports (it names no test) can be put on that test's row. Bounded like the ticket log that records them, and a
  /// ticket leaves it the first time its verdict is dispatched, so a repeated continue records nothing.
  [<Sealed>]
  type HeldTargets() =
    let gate = obj ()
    let held = System.Collections.Generic.Dictionary<string * string, SageFs.HostAgent.TestDebug.DebugTarget>()
    let order = System.Collections.Generic.Queue<string * string>()
    member _.Add(session: string, target: SageFs.HostAgent.TestDebug.DebugTarget) : unit =
      let (SageFs.HostAgent.TestDebug.DebugTicket ticket) = target.Ticket
      lock gate (fun () ->
        held.[(session, ticket)] <- target
        order.Enqueue((session, ticket))
        while order.Count > SageFs.Features.LiveTesting.DebugTicketLog.capacity do
          held.Remove(order.Dequeue()) |> ignore)
    member _.TryTake(session: string, ticket: string) : SageFs.HostAgent.TestDebug.DebugTarget option =
      lock gate (fun () ->
        match held.TryGetValue((session, ticket)) with
        | true, target ->
          held.Remove((session, ticket)) |> ignore
          Some target
        | false, _ -> None)

  /// The daemon's one table of holds it has granted.
  let heldTargets = HeldTargets()

  let private selectorText (selector: TestSelector) : string =
    match selector with
    | TestSelector.ById id -> id
    | TestSelector.ByName name -> name

  /// Pick the session the request means, or the answer that says why none could be picked.
  let private route (ctx: McpContext) (sessionId: string option) (workingDirectory: string option) : Task<Result<string, DebugAnswer>> =
    task {
      let! resolution = resolveSessionId ctx "http" sessionId workingDirectory
      match resolution with
      | Routable sid -> return Ok sid
      | other -> return Error(DebugAnswer.NoSession(formatSessionResolution other))
    }

  /// Hold the chosen test in the session's host. The test must have been discovered into THIS session's own cycle.
  let beginDebug (ctx: McpContext) (request: DebugRequest) : Task<int * DebugWire> =
    task {
      match ctx.GetElmModel with
      | None -> return toWire (DebugAnswer.NoSession "This process has no live-testing engine to ask. Debugging a test needs the SageFs daemon.")
      | Some getModel ->
        match! route ctx request.SessionId request.WorkingDirectory with
        | Error answer -> return toWire answer
        | Ok sid ->
          let discovered = (SageFsModel.cycleOwnedBySession sid (getModel ())).TestState.DiscoveredTests
          match Array.isEmpty discovered with
          | true -> return toWire DebugAnswer.NotDiscovered
          | false ->
            match resolve discovered request.Selector with
            | Resolution.NoMatch -> return toWire (DebugAnswer.NoTestMatched(selectorText request.Selector))
            | Resolution.Ambiguous names -> return toWire (DebugAnswer.AmbiguousTest names)
            | Resolution.Resolved test ->
              let! answer = DebugTestRequest.beginDebug ctx.SessionOps (toSessionId sid) test
              match answer with
              | DebugAnswer.Begun(SageFs.HostAgent.TestDebug.DebugBegin.Held target) -> heldTargets.Add(sid, target)
              | _ -> ()
              return toWire answer
    }

  /// Release the held test and wait (up to the park bound) for it to finish.
  let continueDebug (ctx: McpContext) (request: ContinueRequest) : Task<int * DebugWire> =
    task {
      match! route ctx request.SessionId request.WorkingDirectory with
      | Error answer -> return toWire answer
      | Ok sid ->
        let! answer = DebugTestRequest.continueDebug ctx.SessionOps (toSessionId sid) request.Ticket Timeouts.debugContinuePark
        // The hold ended with a verdict: it goes on the test's row, where a normal run's would, without a normal run.
        match answer, ctx.Dispatch with
        | DebugAnswer.Progress(SageFs.HostAgent.TestDebug.DebugProgress.Ended ended), Some dispatch ->
          match heldTargets.TryTake(sid, request.Ticket) with
          | Some target ->
            match recordOf sid request.Ticket target ended System.DateTimeOffset.UtcNow with
            | Some record -> dispatch (SageFsMsg.Event(TuiEvent.DebugRunFinished record))
            | None -> ()
          | None -> ()
        | _ -> ()
        return toWire answer
    }
