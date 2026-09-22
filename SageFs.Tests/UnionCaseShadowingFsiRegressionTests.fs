module SageFs.Tests.UnionCaseShadowingFsiRegressionTests

open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open FSharp.Compiler.Interactive.Shell
open SageFs.FsiSession
open SageFs.HostAgent

// ---------------------------------------------------------------------------
// The bug at the level it was felt: a real FSI session, self-hosting
// SageFs.Core the way a SageFs developer's own session does, with the
// namespaces its warmup auto-open would bring into scope opened by hand.
// Before the rename, `open SageFs.Features.EvalTimeline` put a bare `Error`
// case in scope and `| Error e -> ...` stopped meaning `Result.Error` —
// "This expression was expected to have type 'Result<int,string>' ... This
// union case does not take arguments", pointing at the wrong problem.
// ---------------------------------------------------------------------------

// --multiemit- matches what a hot-reload session runs with (SageFs.Tests/FsiSessionContractTests.fs).
let private fsiArgs = [ "fsi"; "--noninteractive"; "--nologo"; "--readline-"; "--multiemit-" ]

let private newInProcess () : IFsiSession =
  let config = FsiEvaluationSession.GetDefaultConfiguration()
  let session =
    FsiEvaluationSession.Create(
      config,
      List.toArray fsiArgs,
      new StreamReader(Stream.Null),
      TextWriter.Null,
      TextWriter.Null,
      collectible = true
    )
  new InProcessFsiSession(session, { Projects = []; ResolveFrom = []; ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }) :> IFsiSession

let private coreDllPath = typeof<SageFs.WorkerProtocol.SessionId>.Assembly.Location

let private mustSucceed (session: IFsiSession) (code: string) =
  match session.Eval(code, CancellationToken.None) with
  | { Outcome = FsiSucceeded } -> ()
  | result ->
    failtestf
      "expected %s to succeed, got %A (diagnostics: %A)"
      code
      result.Outcome
      result.Diagnostics

[<Tests>]
let unionCaseShadowingFsiRegressionTests =
  testList "Union case rename — FSI regression (roast: RQA fix)" [

    testCase
      "WHY — self-hosting SageFs.Core and opening EvalTimeline/Diagnostics/MessageJournal must not shadow a bare Result.Error match arm"
    <| fun _ ->
      let session = newInProcess ()
      try
        // `#r` what warmup's auto-open-namespaces would already have loaded
        // for a session self-hosting SageFs.Core, then open every module
        // this bug report named.
        mustSucceed session (sprintf "#r \"%s\";;" coreDllPath)
        mustSucceed session "open SageFs.Features.EvalTimeline;;"
        mustSucceed session "open SageFs.Features.Diagnostics;;"
        mustSucceed session "open SageFs.Features.MessageJournal;;"

        mustSucceed
          session
          "let regressionResult = match (Result.Error \"x\" : Result<int,string>) with Result.Ok _ -> 0 | Error e -> e.Length;;"

        session.BoundValue "regressionResult"
        |> string
        |> Expect.equal "the bare Error arm binds Result.Error, so e.Length on \"x\" is 1" "1"
      finally
        session.Dispose()
  ]
