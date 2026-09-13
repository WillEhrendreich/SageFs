module SageFs.Tests.CompExprSimplifierTests

open System
open Expecto
open Expecto.Flip

open SageFs
open SageFs.AppState
open SageFs.Middleware.ComputationExpression
open SageFs.Utils

let isCompExpr =
  parse
  >> Async.RunSynchronously
  >> Option.map (fun res -> res.tree |> isCompExpr)
  >> Option.defaultValue false

type MockLogger() =
  interface ILogger with
    member this.LogDebug _ = ()
    member this.LogInfo _ = ()
    member this.LogError _ = ()
    member this.LogWarning _ = ()

let mockLogger = MockLogger()

let rewriteExpr = rewriteCompExpr mockLogger >> Async.RunSynchronously

let ofLines (lines: string seq) =
  String.Join(Environment.NewLine, Seq.toArray lines)

let private makeState () : AppState =
  {
    Solution = Unchecked.defaultof<_>
    OriginalSolution = Unchecked.defaultof<_>
    ShadowDir = None
    Logger = mockLogger :> ILogger
    Session = Unchecked.defaultof<_>
    OutStream = Unchecked.defaultof<_>
    StartupConfig = None
    Custom = Map.empty
    Diagnostics = Unchecked.defaultof<_>
    WarmupFailures = []
    WarmupContext = Unchecked.defaultof<_>
    HotReloadState = Unchecked.defaultof<_>
  }

let private passThroughNext : MiddlewareNext =
  fun (request, st) ->
    ({ EvaluationResult = Ok request.Code
       Diagnostics = [||]
       EvaluatedCode = request.Code
       Metadata = Map.empty }, st)

[<Tests>]
let tests =
  testList "comp expr tests" [
    testCase "test let no bang"
    <| fun _ -> (isCompExpr "let a = 10") |> Expect.isFalse "let a = 10 - no comp expr"
    testCase "test let bang"
    <| fun _ -> (isCompExpr "let! a = 10") |> Expect.isTrue "let! a = 10 - comp expr"
    testCase "test let bang tab"
    <| fun _ -> (isCompExpr "   let! a = 10") |> Expect.isTrue "let! a = 10 - comp expr"
    testCase "test let bang multiline"
    <| fun _ ->
      let expr = ofLines [ "let a = 10"; "let! b = 20" ]
      (isCompExpr expr) |> Expect.isTrue $"{expr} - comp expr"
    testCase "test if bang"
    <| fun _ ->
      let code =
        """
    if true then 
      let! a = 10
      return a
    else
      return 0
    """

      (isCompExpr code) |> Expect.isTrue "if else with comp expr"
    testCase "test if bang reverse"
    <| fun _ ->
      let code =
        """
      if true then 
        return a
      else
        let! a = 10
        return 0
      """

      (isCompExpr code) |> Expect.isTrue "if else with comp expr"

    testCase "test bang rewrite"
    <| fun _ ->
      let code = "let! a = 10"
      let expected = ofLines [ "let a = (10).Run()"; "" ]
      (rewriteExpr code) |> Expect.equal "let bang rewrite" expected
    testCase "test bang rewrite tab"
    <| fun _ ->
      let code = "    let! a = 10"
      let expected = ofLines [ "let a = (10).Run()"; "" ]
      (rewriteExpr code) |> Expect.equal "let bang rewrite" expected
    testCase "test bang rewrite multiline"
    <| fun _ ->
      let code = ofLines [ "let a = 10"; ""; ""; "let! b = 20" ]
      let expected = ofLines [ "let a = 10"; ""; "let b = (20).Run()"; "" ]
      (rewriteExpr code) |> Expect.equal "let bang rewrite" expected

    testCase "test bang rewrite multiline expr"
    <| fun _ ->
      let code =
        """
        let! a =
          someComplex
          |>> someMap
          |> multiline
              
        """

      let exp =
        ofLines [ "let a = (someComplex |>> someMap |> multiline).Run()"; ""; ""; "" ]

      (rewriteExpr code) |> Expect.equal "let bang rewrite" exp
    testCase "test non let rewrite "
    <| fun _ ->
      let code =
        """
      let! a =
        someComplex
        |>> someMap
        |> multiline
      return! a
            
      """

      let exp =
        """let a = (someComplex |>> someMap |> multiline).Run()
(a).Run()"""

      (rewriteExpr code) |> Expect.equal "let bang rewrite" exp
    testCase "test if else"
    <| fun _ ->
      let code =
        """
      let! a =
        someComplex
        |>> someMap
        |> multiline
      if a then 
        let! c = 200
        do! sasa
      else
        let! f = 200
        do! baba
            
      """

      let exp =
        """let a = (someComplex |>> someMap |> multiline).Run()

if a then
    let c = (200).Run()
    do (sasa).Run()
else
    let f = (200).Run()
    do (baba).Run()"""

      (rewriteExpr code) |> Expect.equal "let bang rewrite" exp
  ]

[<Tests>]
let middlewareGuardTests =
  testList "comp expr middleware guards" [
    testCase "null session skips FSI flag lookup" <| fun _ ->
      let request = { Code = "let x = 1"; Args = Map.empty }
      let response, _ = compExprMiddleware passThroughNext (request, makeState ())
      response.EvaluatedCode |> Expect.equal "middleware should pass through unchanged when there is no live session" request.Code

    testCase "explicit simplify flag still works with null session" <| fun _ ->
      let request =
        { Code = "let! a = 10"
          Args = Map.ofList [ "simplifyCompExpression", box true ] }
      let response, _ = compExprMiddleware passThroughNext (request, makeState ())
      response.EvaluatedCode |> Expect.equal "explicit simplify flag should still rewrite the code" (rewriteExpr request.Code)
  ]
