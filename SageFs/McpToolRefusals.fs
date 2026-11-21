namespace SageFs

/// The refusals the session tools give for input they cannot act on, each as a `SageFsError` that says what
/// failed, why, and the one thing to do next. They are values so the tool body returns them
/// (`McpEcho.EchoResult.Failed`) and the seam reports isError, instead of the body writing a sentence that a
/// reader of the text has to recognise as a failure. Pure: no context, no IO.
module McpToolRefusals =

  /// The create_*_session targets were not one project, one solution, or bare (`SessionProjectTarget.validate`).
  let targetsInvalid (reason: string) : SageFsError =
    SageFsError.ToolFailed(
      reason,
      "Pass one .fsproj to create_project_session, one .sln or .slnx to create_solution_session, or call create_bare_session for a session with no project")

  /// The workflow argument named no workflow. The three that exist are the next step.
  let unknownWorkflow (raw: string) : SageFsError =
    SageFsError.ToolFailed(
      sprintf "unknown workflow '%s'" raw,
      "Pass workflow as 'interactive' (REPL), 'livetesting' (Live Testing) or 'hotreload' (Hot Reload), or leave it out for interactive")

  /// A session id that is not an id at all (the wrong shape), as opposed to one no session has.
  let invalidSessionId (reason: string) : SageFsError =
    SageFsError.ToolFailed(
      sprintf "invalid session ID: %s" reason,
      "Run list_sessions and pass an id exactly as it prints there")

  /// decompose_pipeline got blank code, so there is nothing to split.
  let noPipelineCode : SageFsError =
    SageFsError.ToolFailed(
      "no F# code was given to decompose",
      "Pass the pipeline expression as code, for example 'xs |> List.map f |> List.filter g'")

  /// A tool that reads the caller's session feature state found no session for the caller.
  let noFeatureState : SageFsError = SageFsError.NoActiveSessions

  /// The name of a worker answer, one arm per `WorkerResponse` case and no wildcard: a case added to the protocol
  /// stops this file compiling until someone says what it is called, which is the prompt to decide whether an
  /// eval can ever receive it (`unexpectedEvalAnswer`).
  let workerAnswerName (response: WorkerProtocol.WorkerResponse) : string =
    match response with
    | WorkerProtocol.WorkerResponse.EvalResult _ -> "EvalResult"
    | WorkerProtocol.WorkerResponse.CheckResult _ -> "CheckResult"
    | WorkerProtocol.WorkerResponse.TypeCheckWithSymbolsResult _ -> "TypeCheckWithSymbolsResult"
    | WorkerProtocol.WorkerResponse.CompletionResult _ -> "CompletionResult"
    | WorkerProtocol.WorkerResponse.DescriptionResult _ -> "DescriptionResult"
    | WorkerProtocol.WorkerResponse.ReferencesResult _ -> "ReferencesResult"
    | WorkerProtocol.WorkerResponse.HostProcessResult _ -> "HostProcessResult"
    | WorkerProtocol.WorkerResponse.StatusResult _ -> "StatusResult"
    | WorkerProtocol.WorkerResponse.LiveValuesResult _ -> "LiveValuesResult"
    | WorkerProtocol.WorkerResponse.LiveMemberResult _ -> "LiveMemberResult"
    | WorkerProtocol.WorkerResponse.EvalCancelled _ -> "EvalCancelled"
    | WorkerProtocol.WorkerResponse.ResetResult _ -> "ResetResult"
    | WorkerProtocol.WorkerResponse.HardResetResult _ -> "HardResetResult"
    | WorkerProtocol.WorkerResponse.LevelResult _ -> "LevelResult"
    | WorkerProtocol.WorkerResponse.ScriptLoaded _ -> "ScriptLoaded"
    | WorkerProtocol.WorkerResponse.TestRunResults _ -> "TestRunResults"
    | WorkerProtocol.WorkerResponse.InitialTestDiscovery _ -> "InitialTestDiscovery"
    | WorkerProtocol.WorkerResponse.EvalLiveTestFileResult _ -> "EvalLiveTestFileResult"
    | WorkerProtocol.WorkerResponse.InstrumentationMapsResult _ -> "InstrumentationMapsResult"
    | WorkerProtocol.WorkerResponse.AppRunResult _ -> "AppRunResult"
    | WorkerProtocol.WorkerResponse.DebugTestAnswer _ -> "DebugTestAnswer"
    | WorkerProtocol.WorkerResponse.SaveSourceSet _ -> "SaveSourceSet"
    | WorkerProtocol.WorkerResponse.SavesApplied _ -> "SavesApplied"
    | WorkerProtocol.WorkerResponse.WorkerReady -> "WorkerReady"
    | WorkerProtocol.WorkerResponse.WorkerShuttingDown -> "WorkerShuttingDown"
    | WorkerProtocol.WorkerResponse.WorkerError _ -> "WorkerError"

  /// An eval got an answer that is not an eval's (a protocol mix-up between the daemon and its worker, usually
  /// a version skew). It never ran clean, so it is a failure that names what arrived. The next step is the one
  /// that settles skew: compare versions, then reset.
  let unexpectedEvalAnswer (sessionId: string) (response: WorkerProtocol.WorkerResponse) : SageFsError =
    SageFsError.WorkerCommunicationFailed(
      sessionId,
      sprintf "the worker answered with %s, which an eval does not expect" (workerAnswerName response))
