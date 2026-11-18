namespace SageFs

/// The editor eval channel's one decision: render a worker's reply as the value
/// the user evaluated, with no MCP framing (Result prefix, verdict, freshness
/// banner). Agents keep the framed text channel; editors get this. A user whose
/// value prints "Result: ..." keeps it verbatim because nothing is stripped later.
module EvalEditorOutput =
  open WorkerProtocol

  /// A failed eval still owes the editor the FULL error — the worker's diagnostic,
  /// the classified reason and the workflow's next step. Only the agent's "Error: "
  /// label is framing; the detail is the user's information.
  let private errorDetail (workflow: WorkflowTypes.SessionWorkflow) (err: SageFsError) (diags: WorkerDiagnostic list) =
    let reason = SageFsError.describeForAgent err
    let suggestion =
      match err with
      | SageFsError.FsiHostCrashed _ -> ""
      | _ -> reason |> ErrorMessages.categorize |> ErrorMessages.getSuggestion
    let enhanced = WorkflowErrorContext.enhance workflow reason suggestion
    [ reason; enhanced; diags |> List.map (fun d -> d.Message) |> String.concat "\n" ]
    |> List.filter (not << System.String.IsNullOrWhiteSpace)
    |> String.concat "\n"
    |> AppState.stripAnsi

  let render (workflow: WorkflowTypes.SessionWorkflow) (response: WorkerResponse) : string =
    match response with
    | WorkerResponse.EvalResult(_, Ok output, _, _) -> AppState.stripAnsi output
    | WorkerResponse.EvalResult(_, Error err, diags, _) -> errorDetail workflow err diags
    | WorkerResponse.WorkerError err -> errorDetail workflow err []
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
