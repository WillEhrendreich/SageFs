module SageFs.Tests.TracingMiddlewareTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState
open SageFs.EvalPipeline
open SageFs.Middleware.Tracing
open SageFs.Measures

// ── Test helpers ──

let dummyRequest code = { Code = code; Args = Map.empty }

let successResponse code =
  { EvaluationResult = Ok "result"
    Diagnostics = [||]
    EvaluatedCode = code
    Metadata = Map.empty }

let failResponse code ex =
  { EvaluationResult = Error (ex: System.Exception)
    Diagnostics = [||]
    EvaluatedCode = code
    Metadata = Map.empty }

// A simple pass-through eval function
let passThroughEval: MiddlewareNext =
  fun (req, st) ->
    (successResponse req.Code, st)

// A failing eval function
let failingEval: MiddlewareNext =
  fun (req, st) ->
    (failResponse req.Code (System.Exception "boom"), st)

// A middleware that uppercases the code
let uppercaseMiddleware: Middleware =
  fun next (req, st) ->
    let newReq = { req with Code = req.Code.ToUpperInvariant() }
    next (newReq, st)

// A middleware that adds a prefix
let prefixMiddleware: Middleware =
  fun next (req, st) ->
    let newReq = { req with Code = "PREFIX:" + req.Code }
    next (newReq, st)

// A delay middleware for timing tests
let delayMiddleware (delayMs: int) : Middleware =
  fun next (req, st) ->
    System.Threading.Thread.Sleep(delayMs)
    next (req, st)

let namedUpper = { Name = "Uppercase"; Middleware = uppercaseMiddleware }
let namedPrefix = { Name = "Prefix"; Middleware = prefixMiddleware }

// ── Tests ──

[<Tests>]
let tracingMiddlewareTests =
  testList "Tracing Middleware" [

    testList "buildTracedPipeline" [
      test "empty middleware list traces only the eval stage" {
        let pipeline = buildTracedPipeline [] "Eval" passThroughEval
        let (response, _) = pipeline (dummyRequest "hello", Unchecked.defaultof<_>)
        let trace = tryGetTrace response |> Option.get
        trace.Stages |> Expect.hasLength "one stage" 1
        trace.Stages.[0].Name |> Expect.equal "eval stage" "Eval"
        trace.Stages.[0].Outcome
        |> Expect.equal "succeeded" StageOutcome.Succeeded
      }

      test "middleware stages appear in order" {
        let pipeline =
          buildTracedPipeline
            [ namedUpper; namedPrefix ]
            "Eval"
            passThroughEval
        let (response, _) = pipeline (dummyRequest "test", Unchecked.defaultof<_>)
        let trace = tryGetTrace response |> Option.get
        let names = trace.Stages |> List.map (fun s -> s.Name)
        names |> Expect.equal "ordered" [ "Uppercase"; "Prefix"; "Eval" ]
      }

      test "middleware transformations are applied" {
        let pipeline =
          buildTracedPipeline
            [ namedUpper; namedPrefix ]
            "Eval"
            passThroughEval
        let (response, _) = pipeline (dummyRequest "hello", Unchecked.defaultof<_>)
        response.EvaluatedCode
        |> Expect.equal "uppercased then prefixed" "PREFIX:HELLO"
      }

      test "failed eval produces Failed outcome on last stage" {
        let pipeline = buildTracedPipeline [] "Eval" failingEval
        let (response, _) = pipeline (dummyRequest "fail", Unchecked.defaultof<_>)
        let trace = tryGetTrace response |> Option.get
        match trace.Stages.[0].Outcome with
        | StageOutcome.Failed _ -> ()
        | StageOutcome.Succeeded ->
          failtest "expected Failed outcome for eval stage"
      }

      test "trace result matches response" {
        let pipeline = buildTracedPipeline [] "Eval" passThroughEval
        let (response, _) = pipeline (dummyRequest "code", Unchecked.defaultof<_>)
        let trace = tryGetTrace response |> Option.get
        match trace.Result with
        | Ok s -> s |> Expect.equal "result" "result"
        | Error _ -> failtest "expected Ok result"
      }

      test "trace result carries error on failure" {
        let pipeline = buildTracedPipeline [] "Eval" failingEval
        let (response, _) = pipeline (dummyRequest "code", Unchecked.defaultof<_>)
        let trace = tryGetTrace response |> Option.get
        match trace.Result with
        | Error (SageFsError.EvalFailed msg) ->
          msg |> Expect.stringContains "has message" "boom"
        | _ -> failtest "expected Error result"
      }
    ]

    testList "timing" [
      test "stages have positive elapsed times" {
        let delayNamed = { Name = "Delay"; Middleware = delayMiddleware 10 }
        let pipeline =
          buildTracedPipeline [ delayNamed ] "Eval" passThroughEval
        let (response, _) = pipeline (dummyRequest "x", Unchecked.defaultof<_>)
        let trace = tryGetTrace response |> Option.get
        let delayStage = trace.Stages |> List.find (fun s -> s.Name = "Delay")
        (rawMsf delayStage.ElapsedMs, 1.0)
        |> Expect.isGreaterThanOrEqual "delay stage should take >1ms"
      }

      test "total pipeline time is sum of stages" {
        let pipeline =
          buildTracedPipeline
            [ namedUpper; namedPrefix ]
            "Eval"
            passThroughEval
        let (response, _) = pipeline (dummyRequest "x", Unchecked.defaultof<_>)
        let trace = tryGetTrace response |> Option.get
        let total = totalMs trace
        let stageSum =
          trace.Stages |> List.sumBy (fun s -> s.ElapsedMs)
        total |> Expect.equal "total = sum" stageSum
      }
    ]

    testList "tryGetTrace" [
      test "returns None for response without trace" {
        let response = successResponse "code"
        tryGetTrace response |> Expect.equal "no trace" None
      }

      test "returns Some for traced response" {
        let pipeline = buildTracedPipeline [] "Eval" passThroughEval
        let (response, _) = pipeline (dummyRequest "x", Unchecked.defaultof<_>)
        tryGetTrace response |> Expect.isSome "has trace"
      }

      test "returns None for wrong metadata type" {
        let response =
          { successResponse "code" with
              Metadata = Map.ofList [ TraceMetadataKey, box "wrong" ] }
        tryGetTrace response |> Expect.equal "wrong type" None
      }
    ]

    testList "namedCommonMiddleware" [
      test "has correct count" {
        namedCommonMiddleware
        |> Expect.hasLength "6 middlewares" 6
      }

      test "has unique names" {
        let names = namedCommonMiddleware |> List.map (fun nm -> nm.Name)
        names |> List.distinct |> Expect.hasLength "all unique" 6
      }

      test "names are non-empty" {
        for nm in namedCommonMiddleware do
          System.String.IsNullOrWhiteSpace nm.Name
          |> Expect.isFalse
            (sprintf "middleware '%s' should have non-empty name" nm.Name)
      }
    ]

    testList "formatRailway integration" [
      test "traced pipeline produces formatted railway" {
        let pipeline =
          buildTracedPipeline
            [ namedUpper; namedPrefix ]
            "Eval"
            passThroughEval
        let (response, _) = pipeline (dummyRequest "x", Unchecked.defaultof<_>)
        let trace = tryGetTrace response |> Option.get
        let railway = formatRailway trace
        railway |> Expect.stringContains "has Uppercase" "Uppercase"
        railway |> Expect.stringContains "has Prefix" "Prefix"
        railway |> Expect.stringContains "has Eval" "Eval"
        railway |> Expect.stringContains "has check" "✓"
      }
    ]

    testList "pipeline reuse" [
      test "pipeline can be called multiple times" {
        let pipeline = buildTracedPipeline [ namedUpper ] "Eval" passThroughEval
        let (r1, _) = pipeline (dummyRequest "a", Unchecked.defaultof<_>)
        let (r2, _) = pipeline (dummyRequest "b", Unchecked.defaultof<_>)
        let t1 = tryGetTrace r1 |> Option.get
        let t2 = tryGetTrace r2 |> Option.get
        t1.Stages |> Expect.hasLength "first has 2 stages" 2
        t2.Stages |> Expect.hasLength "second has 2 stages" 2
      }
    ]
  ]
