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
      | Error ex -> StageOutcome.Failed (SageFsError.EvalFailed ex.Message)
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
        | Error ex -> StageOutcome.Failed (SageFsError.EvalFailed ex.Message)
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

/// The standard middleware stages, each with its name: the ONE list. The pipeline a session runs
/// (`ActorCreation.commonMiddleware`) is these functions in this order, and the trace names its stages
/// from the same entries, so a stage cannot run without a name or carry a name it does not have.
let namedCommonMiddleware: NamedMiddleware list = [
  // Must run FIRST: it never rewrites code, only ever short-circuits a submission that has nothing for
  // FSI to run (a bare module/namespace header, or blank/comments only), see EvaluableSubmission.fs.
  // Every Executable submission passes through it unmodified, so ordering it ahead of the rewriting
  // middleware below costs nothing on the normal path and saves wasted rewrite work (plus a guaranteed
  // FSI compile error) on the "nothing to evaluate" path.
  { Name = "NothingToEvaluate"; Middleware = EvaluableSubmission.nothingToEvaluateMiddleware }
  { Name = "ViBind"; Middleware = Directives.viBindMiddleware }
  { Name = "OpenDirective"; Middleware = Directives.OpenDirective.openDirectiveMiddleware }
  { Name = "CompExpr"; Middleware = ComputationExpression.compExprMiddleware }
  { Name = "NonBlockingRun"; Middleware = NonBlockingRun.nonBlockingRunMiddleware }
  { Name = "HotReload"; Middleware = HotReloading.hotReloadingMiddleware }
]

/// The name the trace gives the error wrapper the eval actor puts at the head of every pipeline.
[<Literal>]
let ErrorWrapperStageName = "ErrorWrapper"

/// The name a trace gives a stage that is in no known list. A production pipeline has none: a stage
/// carrying this name is a stage someone added to the pipeline without naming it.
[<Literal>]
let UnknownStageName = "Unknown"

/// Name the stages of the pipeline the eval actor hands to the trace builder. That pipeline is the
/// error wrapper followed by the session's middleware (`AppState`'s eval command: `wrapErrorMiddleware
/// :: middleware`), so the head is the error wrapper by position, and every other stage is named by
/// the entry of `known` that holds the very same function.
let nameStages (known: NamedMiddleware list) (pipeline: Middleware list) : NamedMiddleware list =
  let nameOf (stage: Middleware) =
    known
    |> List.tryFind (fun candidate -> obj.ReferenceEquals(candidate.Middleware, stage))
    |> Option.map (fun candidate -> candidate.Name)
    |> Option.defaultValue UnknownStageName
  match pipeline with
  | [] -> []
  | wrapper :: rest ->
    { Name = ErrorWrapperStageName; Middleware = wrapper }
    :: (rest |> List.map (fun stage -> { Name = nameOf stage; Middleware = stage }))

/// Build the full traced pipeline including the error wrapper middleware.
/// This is the production-ready replacement for `buildPipeline (wrapErrorMiddleware :: middleware) evalFn`.
let buildProductionTracedPipeline
  (wrapError: Middleware)
  (middleware: NamedMiddleware list)
  (evalFn: MiddlewareNext)
  : MiddlewareNext =
  let allNamed =
    { Name = ErrorWrapperStageName; Middleware = wrapError } :: middleware
  buildTracedPipeline allNamed "CoreEval" evalFn
