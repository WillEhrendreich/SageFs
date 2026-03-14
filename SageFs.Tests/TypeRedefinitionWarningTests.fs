module SageFs.Tests.TypeRedefinitionWarningTests

open Expecto
open Expecto.Flip
open SageFs.AppState
open SageFs.Middleware.TypeRedefinitionWarning
open SageFs.Utils

let private mockLogger =
  { new ILogger with
      member _.LogDebug _ = ()
      member _.LogInfo _ = ()
      member _.LogError _ = ()
      member _.LogWarning _ = () }

let private makeState () : AppState =
  { Solution = Unchecked.defaultof<_>
    OriginalSolution = Unchecked.defaultof<_>
    ShadowDir = None
    Logger = mockLogger
    Session = Unchecked.defaultof<_>
    OutStream = Unchecked.defaultof<_>
    StartupConfig = None
    Custom = Map.empty
    Diagnostics = Unchecked.defaultof<_>
    WarmupFailures = []
    WarmupContext = Unchecked.defaultof<_>
    HotReloadState = Unchecked.defaultof<_> }

let private passThroughNext : MiddlewareNext =
  fun (request, st) ->
    { EvaluationResult = Ok request.Code
      Diagnostics = [||]
      EvaluatedCode = request.Code
      Metadata = Map.empty }, st

/// Run the middleware twice: first with code1, then with code2,
/// threading the state through so tracked types persist.
let private evalTwo code1 code2 =
  let st = makeState ()
  let req1 = { Code = code1; Args = Map.empty }
  let middleware = typeRedefinitionWarningMiddleware passThroughNext
  let resp1, st1 = middleware (req1, st)
  let req2 = { Code = code2; Args = Map.empty }
  let resp2, _ = middleware (req2, st1)
  resp1, resp2

/// Run the middleware once.
let private evalOnce code =
  let st = makeState ()
  let req = { Code = code; Args = Map.empty }
  let middleware = typeRedefinitionWarningMiddleware passThroughNext
  middleware (req, st)

[<Tests>]
let tests =
  testList "TypeRedefinitionWarning" [

    testCase "no warning for first definition of a type" <| fun _ ->
      let resp, _ = evalOnce "type Foo = { Name: string };;"
      match resp.EvaluationResult with
      | Ok text ->
        text.Contains "⚠"
        |> Expect.isFalse "first definition should not warn"
      | Error _ ->
        failtest "should not error"

    testCase "detects record type redefinition" <| fun _ ->
      let _, resp2 = evalTwo
                       "type Foo = { Name: string };;"
                       "type Foo = { Name: string; Age: int };;"
      match resp2.EvaluationResult with
      | Ok text ->
        text.Contains "⚠"
        |> Expect.isTrue "redefinition should warn"
        text.Contains "Foo"
        |> Expect.isTrue "warning should mention the type name"
      | Error _ ->
        failtest "should not error"

    testCase "detects DU type redefinition" <| fun _ ->
      let _, resp2 = evalTwo
                       "type Shape = | Circle of float | Square of float;;"
                       "type Shape = | Circle of float | Triangle of float;;"
      match resp2.EvaluationResult with
      | Ok text ->
        text.Contains "⚠"
        |> Expect.isTrue "DU redefinition should warn"
        text.Contains "Shape"
        |> Expect.isTrue "warning should mention Shape"
      | Error _ ->
        failtest "should not error"

    testCase "no warning for let bindings" <| fun _ ->
      let _, resp2 = evalTwo
                       "let x = 42;;"
                       "let y = x + 1;;"
      match resp2.EvaluationResult with
      | Ok text ->
        text.Contains "⚠"
        |> Expect.isFalse "let bindings should not warn"
      | Error _ ->
        failtest "should not error"

    testCase "no warning for different type names" <| fun _ ->
      let _, resp2 = evalTwo
                       "type Foo = { X: int };;"
                       "type Bar = { Y: int };;"
      match resp2.EvaluationResult with
      | Ok text ->
        text.Contains "⚠"
        |> Expect.isFalse "different type names should not warn"
      | Error _ ->
        failtest "should not error"

    testCase "warning does not block the eval response" <| fun _ ->
      let _, resp2 = evalTwo
                       "type Foo = { Name: string };;"
                       "type Foo = { Name: string; Age: int };;"
      match resp2.EvaluationResult with
      | Ok _ -> ()
      | Error _ ->
        failtest "middleware should not block — result must be Ok"

    testCase "detects multiple redefinitions in one submission" <| fun _ ->
      let _, resp2 = evalTwo
                       "type A = { X: int }\ntype B = | Y;;"
                       "type A = { Z: int }\ntype B = | W;;"
      match resp2.EvaluationResult with
      | Ok text ->
        text.Contains "A"
        |> Expect.isTrue "should warn about A"
        text.Contains "B"
        |> Expect.isTrue "should warn about B"
      | Error _ ->
        failtest "should not error"

    testCase "warning suggests combining into one ;; block" <| fun _ ->
      let _, resp2 = evalTwo
                       "type Foo = { X: int };;"
                       "type Foo = { X: int; Y: int };;"
      match resp2.EvaluationResult with
      | Ok text ->
        text.Contains ";;"
        |> Expect.isTrue "warning should mention combining into one ;; block"
      | Error _ ->
        failtest "should not error"

    testCase "type alias redefinition detected" <| fun _ ->
      let _, resp2 = evalTwo
                       "type Id = int;;"
                       "type Id = string;;"
      match resp2.EvaluationResult with
      | Ok text ->
        text.Contains "⚠"
        |> Expect.isTrue "type alias redefinition should warn"
      | Error _ ->
        failtest "should not error"

    testCase "handles type keyword in strings without false positive" <| fun _ ->
      let resp, _ = evalOnce """let s = "type Foo = bar";;"""
      match resp.EvaluationResult with
      | Ok text ->
        text.Contains "⚠"
        |> Expect.isFalse "type inside string literal should not trigger warning"
      | Error _ ->
        failtest "should not error"
  ]
