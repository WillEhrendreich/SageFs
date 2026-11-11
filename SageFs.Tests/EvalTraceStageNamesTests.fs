/// The eval trace names every production stage with its real name, from the same list the pipeline is built from,
/// and a failing stage records the failure's own text.
module SageFs.Tests.EvalTraceStageNamesTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState
open SageFs.EvalPipeline
open SageFs.Middleware.Tracing

let private errorWrapper : Middleware = fun next (request, st) -> next (request, st)

[<Tests>]
let traceNameTests =
  testList "eval trace stage names" [
    testCase "WHY — commonMiddleware — the pipeline's stages ARE the named stages, same functions in the same order" <| fun _ ->
      ActorCreation.commonMiddleware
      |> Expect.hasLength "one function per named stage" namedCommonMiddleware.Length
      List.zip ActorCreation.commonMiddleware namedCommonMiddleware
      |> List.iter (fun (stage, named) ->
        obj.ReferenceEquals(stage, named.Middleware)
        |> Expect.isTrue (sprintf "the pipeline runs the function that %s names" named.Name))

    testCase "WHY — nameStages — every production stage appears in the trace under its real name, and none is Unknown" <| fun _ ->
      let named = nameStages namedCommonMiddleware (errorWrapper :: ActorCreation.commonMiddleware)
      let names = named |> List.map (fun stage -> stage.Name)
      names
      |> Expect.equal "the error wrapper, then every named stage in pipeline order"
           (ErrorWrapperStageName :: (namedCommonMiddleware |> List.map (fun stage -> stage.Name)))
      names |> List.contains UnknownStageName |> Expect.isFalse "no stage is traced as Unknown"
      names |> List.contains "NothingToEvaluate" |> Expect.isTrue "the first stage of the session's middleware is in the trace"

    testCase "WHY — nameStages — a stage that is in no list is traced as Unknown, so a stage added without a name shows" <| fun _ ->
      let stray : Middleware = fun next (request, st) -> next (request, st)
      nameStages namedCommonMiddleware [ errorWrapper; stray ]
      |> List.map (fun stage -> stage.Name)
      |> Expect.equal "head by position, the stray by lookup" [ ErrorWrapperStageName; UnknownStageName ]

    testCase "WHY — traced pipeline — a failing eval puts the failure's text in every stage, not a stock phrase" <| fun _ ->
      let failingEval : MiddlewareNext =
        fun (request, st) ->
          ({ EvaluationResult = Error (Exception "the real failure"); Diagnostics = [||]; EvaluatedCode = request.Code; Metadata = Map.empty }, st)
      let pipeline =
        buildTracedPipeline (nameStages namedCommonMiddleware [ errorWrapper ]) "CoreEval" failingEval
      let response, _ = pipeline ({ Code = "x"; Args = Map.empty }, Unchecked.defaultof<_>)
      let trace = tryGetTrace response |> Option.get
      trace.Stages |> Expect.isNonEmpty "the trace has stages"
      for stage in trace.Stages do
        match stage.Outcome with
        | StageOutcome.Failed (SageFsError.EvalFailed message) ->
          message |> Expect.equal (sprintf "%s records the failure itself" stage.Name) "the real failure"
        | other -> failtestf "%s should be Failed with the failure's text, got %A" stage.Name other
  ]
