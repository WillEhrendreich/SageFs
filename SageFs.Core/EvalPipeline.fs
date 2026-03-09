namespace SageFs

open SageFs.Measures

/// Eval pipeline computation expression with per-stage tracing.
/// Each stage records its name, elapsed time, and outcome.
/// The trace enables railway visualization in the dashboard.
module EvalPipeline =

  /// Outcome of a single pipeline stage.
  [<RequireQualifiedAccess>]
  type StageOutcome =
    | Succeeded
    | Failed of SageFsError

  /// A completed pipeline stage with timing and outcome.
  [<Struct>]
  type CompletedStage = {
    Name: string
    ElapsedMs: float<ms>
    Outcome: StageOutcome
  }

  /// Result of a tracked stage computation (before binding).
  [<Struct>]
  type TrackedResult<'T> = {
    Value: Result<'T, SageFsError>
    StageName: string
    ElapsedMs: float<ms>
  }

  /// Full pipeline trace: final result plus all completed stages.
  type PipelineTrace<'T> = {
    Result: Result<'T, SageFsError>
    Stages: CompletedStage list
  }

  /// Execute a named stage with timing.
  let stage (name: string) (f: unit -> Result<'T, SageFsError>) : TrackedResult<'T> =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let result = f ()
    sw.Stop()
    { Value = result
      StageName = name
      ElapsedMs = Measures.toMs sw.Elapsed }

  /// Execute a named stage that returns a raw value (cannot fail).
  let stageOk (name: string) (f: unit -> 'T) : TrackedResult<'T> =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let value = f ()
    sw.Stop()
    { Value = Ok value
      StageName = name
      ElapsedMs = Measures.toMs sw.Elapsed }

  /// The computation expression builder for eval pipelines.
  type PipelineBuilder() =
    member _.Bind(tracked: TrackedResult<'a>, f: 'a -> PipelineTrace<'b>) : PipelineTrace<'b> =
      let completed = {
        Name = tracked.StageName
        ElapsedMs = tracked.ElapsedMs
        Outcome =
          match tracked.Value with
          | Ok _ -> StageOutcome.Succeeded
          | Error e -> StageOutcome.Failed e
      }
      match tracked.Value with
      | Ok v ->
        let rest = f v
        { rest with Stages = completed :: rest.Stages }
      | Error e ->
        { Result = Error e; Stages = [ completed ] }

    member _.Return(x: 'a) : PipelineTrace<'a> =
      { Result = Ok x; Stages = [] }

    member _.ReturnFrom(t: PipelineTrace<'a>) : PipelineTrace<'a> = t

    member _.Zero() : PipelineTrace<unit> =
      { Result = Ok (); Stages = [] }

  /// The pipeline CE instance.
  let pipeline = PipelineBuilder()

  /// Total elapsed time across all stages.
  let totalMs (trace: PipelineTrace<'T>) : float<ms> =
    trace.Stages |> List.sumBy (fun s -> s.ElapsedMs)

  /// Whether the pipeline completed successfully (all stages passed).
  let succeeded (trace: PipelineTrace<'T>) : bool =
    match trace.Result with
    | Ok _ -> true
    | Error _ -> false

  /// Format a trace as a railway string: "Parse ✓ [1.2ms] → TypeCheck ✓ [3.4ms] → Execute ✗ [0.0ms]"
  let formatRailway (trace: PipelineTrace<'T>) : string =
    match trace.Stages with
    | [] -> "(empty pipeline)"
    | stages ->
      stages
      |> List.map (fun s ->
        let icon =
          match s.Outcome with
          | StageOutcome.Succeeded -> "✓"
          | StageOutcome.Failed _ -> "✗"
        sprintf "%s %s [%.1fms]" s.Name icon (rawMsf s.ElapsedMs))
      |> String.concat " → "
