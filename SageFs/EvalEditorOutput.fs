namespace SageFs

/// The editor eval channel's one decision: render a worker's reply as the value
/// the user evaluated, with no MCP framing (Result prefix, verdict, freshness
/// banner). Agents keep the framed text channel; editors get this. A user whose
/// value prints "Result: ..." keeps it verbatim because nothing is stripped later.
module EvalEditorOutput =
  open WorkerProtocol

  let render (response: WorkerResponse) : string =
    match response with
    | WorkerResponse.EvalResult(_, Ok output, _, _) -> AppState.stripAnsi output
    | WorkerResponse.EvalResult(_, Error err, _, _) -> AppState.stripAnsi (SageFsError.describeForAgent err)
    | WorkerResponse.WorkerError err -> AppState.stripAnsi (SageFsError.describeForAgent err)
    | WorkerResponse.CheckResult _
    | WorkerResponse.TypeCheckWithSymbolsResult _
    | WorkerResponse.CompletionResult _
    | WorkerResponse.ReferencesResult _
    | WorkerResponse.HostProcessResult _
    | WorkerResponse.StatusResult _
    | WorkerResponse.LiveValuesResult _
    | WorkerResponse.LiveMemberResult _
    | WorkerResponse.EvalCancelled _
    | WorkerResponse.ResetResult _
    | WorkerResponse.HardResetResult _
    | WorkerResponse.LevelResult _
    | WorkerResponse.ScriptLoaded _
    | WorkerResponse.TestRunResults _
    | WorkerResponse.InitialTestDiscovery _
    | WorkerResponse.EvalLiveTestFileResult _
    | WorkerResponse.InstrumentationMapsResult _
    | WorkerResponse.AppRunResult _
    | WorkerResponse.DebugTestAnswer _
    | WorkerResponse.SaveSourceSet _
    | WorkerResponse.SavesApplied _
    | WorkerResponse.WorkerReady
    | WorkerResponse.WorkerShuttingDown -> ""
