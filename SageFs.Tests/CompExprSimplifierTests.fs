module SageFs.Tests.CompExprSimplifierTests

open System
open Expecto
open Expecto.Flip

open SageFs
open SageFs.AppState
open SageFs.Middleware.ComputationExpression
open SageFs.Utils

/// The parse is an Async over the compiler service; the case awaits it (`let!`) instead of blocking a thread.
let isCompExpr (code: string) : System.Threading.Tasks.Task<bool> =
  async {
    let! parsed = parse code
    return
      parsed
      |> Option.map (fun res -> res.tree |> isCompExpr)
      |> Option.defaultValue false
  }
  |> Async.StartAsTask

type MockLogger() =
  interface ILogger with
    member this.LogDebug _ = ()
    member this.LogInfo _ = ()
    member this.LogError _ = ()
    member this.LogWarning _ = ()

let mockLogger = MockLogger()

let rewriteExpr (code: string) : System.Threading.Tasks.Task<string> =
  rewriteCompExpr mockLogger code |> Async.StartAsTask

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
    testCaseTask "test let no bang"
    <| fun () -> task {
      let! result = isCompExpr "let a = 10"
      result |> Expect.isFalse "let a = 10 - no comp expr"
    }
    testCaseTask "test let bang"
    <| fun () -> task {
      let! result = isCompExpr "let! a = 10"
      result |> Expect.isTrue "let! a = 10 - comp expr"
    }
    testCaseTask "test let bang tab"
    <| fun () -> task {
      let! result = isCompExpr "   let! a = 10"
      result |> Expect.isTrue "let! a = 10 - comp expr"
    }
    testCaseTask "test let bang multiline"
    <| fun () -> task {
      let expr = ofLines [ "let a = 10"; "let! b = 20" ]
      let! result = isCompExpr expr
      result |> Expect.isTrue $"{expr} - comp expr"
    }
    testCaseTask "test if bang"
    <| fun () -> task {
      let code =
        """
    if true then 
      let! a = 10
      return a
    else
      return 0
    """

      let! result = isCompExpr code
      result |> Expect.isTrue "if else with comp expr"
    }
    testCaseTask "test if bang reverse"
    <| fun () -> task {
      let code =
        """
      if true then 
        return a
      else
        let! a = 10
        return 0
      """

      let! result = isCompExpr code
      result |> Expect.isTrue "if else with comp expr"
    }

    testCaseTask "test bang rewrite"
    <| fun () -> task {
      let code = "let! a = 10"
      let expected = ofLines [ "let a = (10).Run()"; "" ]
      let! rewritten = rewriteExpr code
      rewritten |> Expect.equal "let bang rewrite" expected
    }
    testCaseTask "test bang rewrite tab"
    <| fun () -> task {
      let code = "    let! a = 10"
      let expected = ofLines [ "let a = (10).Run()"; "" ]
      let! rewritten = rewriteExpr code
      rewritten |> Expect.equal "let bang rewrite" expected
    }
    testCaseTask "test bang rewrite multiline"
    <| fun () -> task {
      let code = ofLines [ "let a = 10"; ""; ""; "let! b = 20" ]
      let expected = ofLines [ "let a = 10"; ""; "let b = (20).Run()"; "" ]
      let! rewritten = rewriteExpr code
      rewritten |> Expect.equal "let bang rewrite" expected
    }

    testCaseTask "test bang rewrite multiline expr"
    <| fun () -> task {
      let code =
        """
        let! a =
          someComplex
          |>> someMap
          |> multiline
              
        """

      let exp =
        ofLines [ "let a = (someComplex |>> someMap |> multiline).Run()"; ""; ""; "" ]

      let! rewritten = rewriteExpr code
      rewritten |> Expect.equal "let bang rewrite" exp
    }
    testCaseTask "test non let rewrite "
    <| fun () -> task {
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

      let! rewritten = rewriteExpr code
      rewritten |> Expect.equal "let bang rewrite" exp
    }
    testCaseTask "test if else"
    <| fun () -> task {
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

      let! rewritten = rewriteExpr code
      rewritten |> Expect.equal "let bang rewrite" exp
    }
  ]

[<Tests>]
let middlewareGuardTests =
  testList "comp expr middleware guards" [
    testCase "null session skips FSI flag lookup" <| fun _ ->
      let request = { Code = "let x = 1"; Args = Map.empty }
      let response, _ = compExprMiddleware passThroughNext (request, makeState ())
      response.EvaluatedCode |> Expect.equal "middleware should pass through unchanged when there is no live session" request.Code

    testCaseTask "explicit simplify flag still works with null session" <| fun () -> task {
      let request =
        { Code = "let! a = 10"
          Args = Map.ofList [ "simplifyCompExpression", box true ] }
      let response, _ = compExprMiddleware passThroughNext (request, makeState ())
      let! rewritten = rewriteExpr request.Code
      response.EvaluatedCode |> Expect.equal "explicit simplify flag should still rewrite the code" rewritten
    }
  ]
