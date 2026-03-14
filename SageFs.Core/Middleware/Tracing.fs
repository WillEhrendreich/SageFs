module SageFs.Middleware.Tracing

open SageFs
open SageFs.AppState
open SageFs.EvalPipeline
open SageFs.Measures

/// Metadata key for the pipeline trace in EvalResponse.
[<Literal>]
let TraceMetadataKey = "pipelineTrace"

/// A named middleware for tracing.
type NamedMiddleware = {
  Name: string
  Middleware: Middleware
}

/// Wrap a middleware with timing, producing a CompletedStage.
let private traceMiddleware
  (named: NamedMiddleware)
  (stages: CompletedStage list ref)
  (next: MiddlewareNext)
  : MiddlewareNext =
  fun (request, st) ->
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let wrappedNext = named.Middleware next
    let (response, newSt) = wrappedNext (request, st)
    sw.Stop()
    let outcome =
      match response.EvaluationResult with
      | Ok _ -> StageOutcome.Succeeded
      | Error _ -> StageOutcome.Failed (SageFsError.EvalFailed "middleware error")
    stages.Value <-
      { Name = named.Name
        ElapsedMs = Measures.toMs sw.Elapsed
        Outcome = outcome }
      :: stages.Value
    (response, newSt)

/// Build a traced pipeline from named middlewares and a core eval function.
/// Returns a MiddlewareNext that adds a PipelineTrace to EvalResponse.Metadata.
let buildTracedPipeline
  (namedMiddleware: NamedMiddleware list)
  (coreEvalName: string)
  (evalFn: MiddlewareNext)
  : MiddlewareNext =
  let stages = ref []
  // Wrap the core eval function with timing
  let tracedEval: MiddlewareNext =
    fun (request, st) ->
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let (response, newSt) = evalFn (request, st)
      sw.Stop()
      let outcome =
        match response.EvaluationResult with
        | Ok _ -> StageOutcome.Succeeded
        | Error _ -> StageOutcome.Failed (SageFsError.EvalFailed "eval error")
      stages.Value <-
        { Name = coreEvalName
          ElapsedMs = Measures.toMs sw.Elapsed
          Outcome = outcome }
        :: stages.Value
      (response, newSt)
  // Build the pipeline: each named middleware wraps the next
  let pipeline =
    namedMiddleware
    |> List.foldBack (fun nm next -> traceMiddleware nm stages next) 
    <| tracedEval
  // Return a MiddlewareNext that runs the pipeline and attaches the trace
  fun (request, st) ->
    stages.Value <- []
    let (response, newSt) = pipeline (request, st)
    let trace: PipelineTrace<string> = {
      Result =
        match response.EvaluationResult with
        | Ok s -> Ok s
        | Error ex -> Error (SageFsError.EvalFailed ex.Message)
      Stages = stages.Value
    }
    let enrichedResponse =
      { response with
          Metadata = response.Metadata |> Map.add TraceMetadataKey (box trace) }
    (enrichedResponse, newSt)

/// Extract a PipelineTrace from EvalResponse.Metadata.
let tryGetTrace (response: EvalResponse) : PipelineTrace<string> option =
  response.Metadata
  |> Map.tryFind TraceMetadataKey
  |> Option.bind (fun v ->
    match v with
    | :? PipelineTrace<string> as t -> Some t
    | _ -> None)

/// Format middleware names for the standard middleware list.
let namedCommonMiddleware: NamedMiddleware list = [
  { Name = "TypeRedefWarn"; Middleware = TypeRedefinitionWarning.typeRedefinitionWarningMiddleware }
  { Name = "FsiCompat"; Middleware = FsiCompatibility.fsiCompatibilityMiddleware }
  { Name = "ViBind"; Middleware = Directives.viBindMiddleware }
  { Name = "OpenDirective"; Middleware = Directives.OpenDirective.openDirectiveMiddleware }
  { Name = "CompExpr"; Middleware = ComputationExpression.compExprMiddleware }
  { Name = "NonBlockingRun"; Middleware = NonBlockingRun.nonBlockingRunMiddleware }
  { Name = "HotReload"; Middleware = HotReloading.hotReloadingMiddleware }
]

/// Build the full traced pipeline including the error wrapper middleware.
/// This is the production-ready replacement for `buildPipeline (wrapErrorMiddleware :: middleware) evalFn`.
let buildProductionTracedPipeline
  (wrapError: Middleware)
  (middleware: NamedMiddleware list)
  (evalFn: MiddlewareNext)
  : MiddlewareNext =
  let allNamed =
    { Name = "ErrorWrapper"; Middleware = wrapError } :: middleware
  buildTracedPipeline allNamed "CoreEval" evalFn
