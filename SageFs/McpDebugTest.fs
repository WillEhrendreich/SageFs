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

  /// A daemon's table of the holds it has granted (`HeldTable`, pure), behind a lock because routes run on many threads. One per
  /// daemon, carried by its route context: a process that runs several daemons (a test run does, with the same session id and ticket
  /// in each) must not have one take another's hold.
  [<Sealed>]
  type HeldTargets() =
    let gate = obj ()
    let mutable table = HeldTable.empty
    member _.Add(session: string, target: SageFs.HostAgent.TestDebug.DebugTarget) : unit =
      lock gate (fun () -> table <- HeldTable.add session target table)
    /// The verdict the host's answer comes to, with the hold taken out of the table.
    member _.Verdict(session: string, ticket: string, ended: SageFs.HostAgent.TestDebug.DebugEnd) : SageFs.Features.LiveTesting.DebugRunRecord option =
      lock gate (fun () ->
        let rest, record = verdictFor table session ticket ended System.DateTimeOffset.UtcNow
        table <- rest
        record)

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
  let beginDebug (held: HeldTargets) (ctx: McpContext) (request: DebugRequest) : Task<int * DebugWire> =
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
              | DebugAnswer.Begun(SageFs.HostAgent.TestDebug.DebugBegin.Held target) -> held.Add(sid, target)
              | _ -> ()
              match hostVoiceOf answer with
              | HostVoice.ClearNote -> SilentHost.clear sid
              | HostVoice.NoteSilent _
              | HostVoice.KeepNote -> ()
              return toWire answer
    }

  /// Release the held test and wait (up to the park bound) for it to finish.
  let continueDebug (held: HeldTargets) (ctx: McpContext) (request: ContinueRequest) : Task<int * DebugWire> =
    task {
      match! route ctx request.SessionId request.WorkingDirectory with
      | Error answer -> return toWire answer
      | Ok sid ->
        let! answer = DebugTestRequest.continueDebug ctx.SessionOps (toSessionId sid) request.Ticket Timeouts.debugContinuePark
        // What the answer says about the host's voice goes where the session's health reads it: a silent host makes a Ready
        // session Degraded with the reason, and the host answering again takes that back.
        match hostVoiceOf answer with
        | HostVoice.NoteSilent silence ->
          let! info = ctx.SessionOps.GetSessionInfo(toSessionId sid)
          SilentHost.note sid (info |> Option.bind (fun i -> WorkerProtocol.SessionLifecycleStatus.workerPid i.Status)) silence
        | HostVoice.ClearNote -> SilentHost.clear sid
        | HostVoice.KeepNote -> ()
        // The hold ended with a verdict: it goes on the test's row, where a normal run's would, without a normal run.
        match answer, ctx.Dispatch with
        | DebugAnswer.Progress(SageFs.HostAgent.TestDebug.DebugProgress.Ended ended), Some dispatch ->
          match held.Verdict(sid, request.Ticket, ended) with
          | Some record -> dispatch (SageFsMsg.Event(TuiEvent.DebugRunFinished record))
          | None -> ()
        | _ -> ()
        return toWire answer
    }
