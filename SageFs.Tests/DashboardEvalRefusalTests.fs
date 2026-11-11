/// An Evaluate click that cannot run says why and keeps the code.
///
/// The handler used to answer a blank box, a missing session and a malformed session id the same way: it
/// patched the `code` signal to "" and said nothing. The person's text vanished from the box and no
/// message appeared. `EvalRequest.decide` now names each refusal, and the handler shows the sentence where
/// every other dashboard action says how it went, leaving the box alone.
module SageFs.Tests.DashboardEvalRefusalTests

open System.IO
open System.Text
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Server.Dashboard
open SageFs.Server.DashboardRequests

let private goodId = "0a0b0c0d"

let private contextWithBody (bodyJson: string) =
  let ctx = DefaultHttpContext()
  ctx.Request.ContentType <- "application/json"
  let bytes = Encoding.UTF8.GetBytes(bodyJson)
  ctx.Request.ContentLength <- System.Nullable(int64 bytes.Length)
  ctx.Request.Body <- new MemoryStream(bytes)
  ctx.Response.Body <- new MemoryStream()
  ctx

let private responseText (ctx: HttpContext) =
  let ms = ctx.Response.Body :?> MemoryStream
  ms.Position <- 0L
  use reader = new StreamReader(ms)
  reader.ReadToEnd()

/// The refusal branches never read the queries or the infrastructure, so a handler built over nothing proves
/// they do not reach for either. A fake evaluator records whether anything was asked to run.
let private runHandler (body: string) : Task<string * bool> = task {
  let evaluated = ref false
  let evalCode (_: WorkerProtocol.SessionId) (_: string) : Task<Result<string, string>> =
    evaluated.Value <- true
    Task.FromResult(Ok "ran")
  let handler = createEvalHandler Unchecked.defaultof<_> Unchecked.defaultof<_> evalCode
  let ctx = contextWithBody body
  do! handler ctx
  return responseText ctx, evaluated.Value
}

[<Tests>]
let decisionTests =
  testList "EvalRequest.decide" [
    testCase "WHY — blank or whitespace code is refused as blank, whatever the session says" <| fun _ ->
      for code in [ ""; "   "; "\n\t " ] do
        EvalRequest.decide { Code = code; ViewingSessionId = goodId }
        |> Expect.equal (sprintf "code %A" code) (Result.Error EvalRefusal.BlankCode)

    testCase "WHY — code with no session in view is refused as NoSessionInView, not as a bad id" <| fun _ ->
      EvalRequest.decide { Code = "1 + 1"; ViewingSessionId = "" }
      |> Expect.equal "no session" (Result.Error EvalRefusal.NoSessionInView)
      EvalRequest.decide { Code = "1 + 1"; ViewingSessionId = null }
      |> Expect.equal "null session" (Result.Error EvalRefusal.NoSessionInView)

    testCase "WHY — a malformed session id keeps the validator's reason" <| fun _ ->
      match EvalRequest.decide { Code = "1 + 1"; ViewingSessionId = "not-an-id" } with
      | Result.Error(EvalRefusal.SessionIdInvalid reason) ->
        reason |> Expect.stringContains "the reason names the id" "not-an-id"
      | other -> failtestf "expected SessionIdInvalid, got %A" other

    testCase "WHY — code and a valid id decide to the typed id and the code with its terminator" <| fun _ ->
      match EvalRequest.decide { Code = "1 + 1"; ViewingSessionId = goodId } with
      | Result.Ok(sid, code) ->
        WorkerProtocol.SessionId.value sid |> Expect.equal "the id" goodId
        code |> Expect.equal "terminated" "1 + 1;;"
      | other -> failtestf "expected Ok, got %A" other

    testCase "WHY — code that already ends in ;; is not terminated twice" <| fun _ ->
      match EvalRequest.decide { Code = "1 + 1;;"; ViewingSessionId = goodId } with
      | Result.Ok(_, code) -> code |> Expect.equal "left alone" "1 + 1;;"
      | other -> failtestf "expected Ok, got %A" other

    testCase "WHY — every refusal says what happened and the next action, in a sentence of its own" <| fun _ ->
      for refusal in
        [ EvalRefusal.BlankCode
          EvalRefusal.NoSessionInView
          EvalRefusal.SessionIdInvalid "invalid session ID format: 'x'" ] do
        let text = EvalRefusal.describe refusal
        (text.Length, 30) |> Expect.isGreaterThan (sprintf "%A has words" refusal)
        text |> Expect.stringContains (sprintf "%A says nothing was run" refusal) "Nothing was run"
  ]

[<Tests>]
let handlerTests =
  testList "createEvalHandler refusals keep the code and say why" [
    testTask "WHY — blank code shows the refusal and does not touch the code signal" {
      let! out, evaluated = runHandler (sprintf """{"code":"   ","viewingSessionId":"%s"}""" goodId)
      evaluated |> Expect.isFalse "nothing was evaluated"
      out |> Expect.stringContains "the notice is on the page" "action-result"
      out |> Expect.stringContains "the sentence is there" (EvalRefusal.describe EvalRefusal.BlankCode |> fun s -> s.Substring(0, 20))
      out.Contains "datastar-patch-signals" |> Expect.isFalse "no signal patch, so the box keeps what is in it"
    }

    testTask "WHY — code with no session in view keeps the code and tells the person to open a session" {
      let! out, evaluated = runHandler """{"code":"let x = 1","viewingSessionId":""}"""
      evaluated |> Expect.isFalse "nothing was evaluated"
      out |> Expect.stringContains "the notice is on the page" "action-result"
      out |> Expect.stringContains "it names the next action" "Quick Start"
      out.Contains "datastar-patch-signals" |> Expect.isFalse "the code signal is left alone"
    }

    testTask "WHY — an id the validator refuses keeps the code and carries the validator's reason" {
      let! out, evaluated = runHandler """{"code":"let x = 1","viewingSessionId":"zzz"}"""
      evaluated |> Expect.isFalse "nothing was evaluated"
      out |> Expect.stringContains "the reason is shown" "invalid session ID format"
      out.Contains "datastar-patch-signals" |> Expect.isFalse "the code signal is left alone"
    }
  ]
