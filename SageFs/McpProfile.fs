namespace SageFs

open System.IO
open System.Threading.Tasks
open SageFs.Features
open SageFs.McpRouteError
open SageFs.McpTools

/// The `profile` tool: run F# like `send_fsharp_code` while the daemon records the session's FSI host from outside the
/// process (the host loads nothing for it), then answer with the eval's own reply and a ranked, bounded table of where the
/// managed CPU time went, or the sampled allocations by type. The decisions here are pure and tested (`hostPidOf`, `projectModulesOf`, `answerOf`); the recording
/// is `DotnetTraceCollector`, and the eval is the daemon's ordinary one.
module McpProfile =

  /// What one call asks for, parsed at the edge. A session id or working directory the caller left out is empty.
  type ProfileCall =
    { Agent: string
      Code: string
      Mode: ProfileMode
      SessionId: string
      WorkingDirectory: string }

  /// A tool argument the caller may leave out, as the routing functions take it.
  let argumentOf (text: string) : string option =
    match System.String.IsNullOrWhiteSpace text with
    | true -> None
    | false -> Some text

  /// The process to record, from what the worker answered to `GetHostProcess`. A session with no process of its own, or
  /// none running, is refused with the reason and the next step.
  let hostPidOf (routed: Result<WorkerProtocol.WorkerResponse, RouteError>) : Result<int, ProfileFailure> =
    match routed with
    | Result.Ok(WorkerProtocol.WorkerResponse.HostProcessResult(_, WorkerProtocol.HostProcess.Isolated pid)) -> Result.Ok pid
    | Result.Ok(WorkerProtocol.WorkerResponse.HostProcessResult(_, WorkerProtocol.HostProcess.InWorker)) ->
      Result.Error(ProfileFailure.NotRecordable "its code runs inside the worker process, which also runs the daemon's own work")
    | Result.Ok(WorkerProtocol.WorkerResponse.HostProcessResult(_, WorkerProtocol.HostProcess.NotRunning)) ->
      Result.Error(ProfileFailure.NotRecordable "the session has no running FSI yet or any more, so there is nothing to record")
    | Result.Ok _ -> Result.Error(ProfileFailure.CouldNotAttach "the worker answered the host process question with another reply")
    | Result.Error routeError -> Result.Error(ProfileFailure.CouldNotAttach(routeErrorMessage routeError))

  /// The assembly names of the projects the session loaded, which is how a frame is known to be the user's.
  let projectModulesOf (projectPaths: string list) : Set<string> =
    projectPaths |> List.map Path.GetFileNameWithoutExtension |> List.filter (fun name -> not (System.String.IsNullOrEmpty name)) |> Set.ofList

  /// The whole reply: the eval's own text, then the table or the named reason there is none.
  let answerOf (projectModules: Set<string>) (mode: ProfileMode) (evalText: string) (trace: Result<RecordedTrace, ProfileFailure>) : string =
    let bounds = ProfileReport.defaultBounds
    let table =
      match mode, trace with
      | _, Result.Error failure -> ProfileFailure.describe failure
      | ProfileMode.Cpu, Result.Ok(RecordedTrace.Speedscope json) ->
        match SpeedscopeTrace.cpuReport projectModules bounds.Top json with
        | Result.Ok report -> ProfileReport.renderCpu bounds report
        | Result.Error failure -> ProfileFailure.describe failure
      | ProfileMode.Alloc, Result.Ok(RecordedTrace.NetTrace bytes) ->
        match NetTrace.allocReport projectModules bounds.Top bytes with
        | Result.Ok report -> ProfileReport.renderAlloc bounds report
        | Result.Error failure -> ProfileFailure.describe failure
      | ProfileMode.Cpu, Result.Ok(RecordedTrace.NetTrace _)
      | ProfileMode.Alloc, Result.Ok(RecordedTrace.Speedscope _) ->
        ProfileFailure.describe (ProfileFailure.TraceUnreadable "the recorder gave the other kind of trace than this mode reads")
    sprintf "%s\n\n--- profile (%s) ---\n%s" evalText (ProfileMode.toWire mode) table

  /// The trace the reply is built from. A host that crashed while the code ran is the one thing the eval itself knows and the
  /// recorder may not yet have noticed (it ends when the host does, and says so a moment later), so the eval's own word wins
  /// and the crash text is the reply, never a table read from a trace of a process that died.
  let traceAfterEval
    (evalError: SageFsError option)
    (trace: Result<RecordedTrace, ProfileFailure>)
    : Result<RecordedTrace, ProfileFailure> =
    match evalError with
    | Some(SageFsError.FsiHostCrashed crash) -> Result.Error(ProfileFailure.HostGone(HostCrash.describe crash))
    | Some _
    | None -> trace

  let traceBaseFor () : string =
    Path.Combine(Path.GetTempPath(), sprintf "sagefs-profile-%s" (System.Guid.NewGuid().ToString "N"))

  let hostPidAsync (ctx: McpContext) (sid: string) : Task<Result<int, ProfileFailure>> =
    task {
      let! routed = routeToSession ctx sid (fun replyId -> WorkerProtocol.WorkerMessage.GetHostProcess(WorkerProtocol.SessionId.value replyId))
      return hostPidOf routed
    }

  let projectModulesAsync (ctx: McpContext) (sid: string) : Task<Set<string>> =
    task {
      let! info = ctx.SessionOps.GetSessionInfo(McpSessionRouting.toSessionId sid)
      return
        match info with
        | Some value -> value.ProjectRoles |> List.map _.Path |> projectModulesOf
        | None -> Set.empty
    }

  /// Run the call's code in its session under a recording. The recording must start before the code runs, and a recording that
  /// cannot start is answered with the reason and the code does not run.
  let profileIn (ctx: McpContext) (collector: TraceCollector) (call: ProfileCall) (sid: string) : Task<string> =
    task {
      match! hostPidAsync ctx sid with
      | Result.Error failure -> return ProfileFailure.describe failure
      | Result.Ok pid ->
        let! modules = projectModulesAsync ctx sid
        let limit = EvalLimit.ofRequest Timeouts.evalCall Timeouts.evalCallCap 0
        let eval () =
          evalFSharpCodeWithOutcome ctx call.Agent call.Code OutputFormat.Text (Some sid) None None None None None limit
          |> Async.AwaitTask
        match! ProfileRecorder.around collector { HostPid = pid; Mode = call.Mode; TraceBase = traceBaseFor () } eval |> Async.StartAsTask with
        | Result.Error failure -> return ProfileFailure.describe failure
        | Result.Ok profiled ->
          let evalText, evalError =
            match profiled.Eval with
            | Guarded.Attempt.Completed(text, _, _, error, _, _) -> text, error
            | Guarded.Attempt.Threw ex -> sprintf "Error: the eval threw before it answered: %s" ex.Message, None
          return answerOf modules call.Mode evalText (traceAfterEval evalError profiled.Trace)
    }

  /// The call, answered, with the daemon's installed recorder.
  let profile (ctx: McpContext) (call: ProfileCall) : Task<string> =
    withSession ctx call.Agent (argumentOf call.SessionId) (argumentOf call.WorkingDirectory) (profileIn ctx (DotnetTraceCollector.create ()) call)
