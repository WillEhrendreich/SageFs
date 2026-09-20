/// Control-flow tests for `createWorkflowSwitchHandler` (`Dashboard.fs` —
/// sagefs-ux-roast.md §4.1/§4.2/§11 Island B item 4). Exercises the handler
/// directly against a `DefaultHttpContext` with FAKE `getCurrentLabel` /
/// `switchWorkflow` functions — no network, no daemon, no Kestrel host —
/// mirroring `McpServerOversizedRequestTests.fs`'s established pattern for
/// testing a Falco `HttpHandler` (`HttpContext -> Task`) as a plain function.
/// The real network call (`switchWorkflowViaApi`) is exercised separately in
/// `DashboardWorkflowSwitchApiWiringTests.fs`.
module SageFs.Tests.DashboardWorkflowSwitchHandlerTests

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Server.Dashboard
open SageFs.Server.DashboardTypes

let private contextWithBody (bodyJson: string) =
  let ctx = DefaultHttpContext()
  ctx.Request.ContentType <- "application/json"
  let bytes = Encoding.UTF8.GetBytes(bodyJson)
  ctx.Request.ContentLength <- Nullable(int64 bytes.Length)
  ctx.Request.Body <- new MemoryStream(bytes)
  ctx.Response.Body <- new MemoryStream()
  ctx

let private responseText (ctx: HttpContext) =
  let ms = ctx.Response.Body :?> MemoryStream
  ms.Position <- 0L
  use reader = new StreamReader(ms)
  reader.ReadToEnd()

let private sid = WorkerProtocol.SessionId.validate "0a0b0c0d" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())

/// A switchWorkflow fake that records whether it was called at all — used to
/// prove the invalid-input branches (missing session, bad target) never
/// reach the network-calling function.
let private neverCalled () : (WorkerProtocol.SessionId -> WorkflowTypes.SessionWorkflow -> Task<Result<string, string>>) * (bool ref) =
  let called = ref false
  let fn = fun (_: WorkerProtocol.SessionId) (_: WorkflowTypes.SessionWorkflow) ->
    called.Value <- true
    Task.FromResult(Ok "should not have been called")
  fn, called

[<Tests>]
let invalidInputTests =
  testList "createWorkflowSwitchHandler — invalid input never reaches the switch function" [
    testTask "WHY — a missing viewingSessionId signal is a clean error, and switchWorkflow is never invoked" {
      let switchFn, called = neverCalled ()
      let ctx = contextWithBody """{"workflowTarget":"hotreload"}"""
      do! createWorkflowSwitchHandler (fun _ -> "REPL") switchFn ctx
      called.Value |> Expect.isFalse "switchWorkflow must not be called without a session id"
      responseText ctx |> Expect.stringContains "the error names the missing signal" "viewingSessionId"
    }

    testTask "WHY — an unrecognized workflow target is a clean error naming the bad value, and switchWorkflow is never invoked" {
      let switchFn, called = neverCalled ()
      let ctx = contextWithBody (sprintf """{"viewingSessionId":"%s","workflowTarget":"quantum"}""" (WorkerProtocol.SessionId.value sid))
      do! createWorkflowSwitchHandler (fun _ -> "REPL") switchFn ctx
      called.Value |> Expect.isFalse "switchWorkflow must not be called for an unrecognized target"
      responseText ctx |> Expect.stringContains "the error names the bad value" "quantum"
    }
  ]

[<Tests>]
let successPathTests =
  testList "createWorkflowSwitchHandler — success path" [
    testTask "WHY — a successful switch shows the optimistic pending control BEFORE the final result, so the user sees feedback immediately, not just at the end" {
      let switchFn = fun (_: WorkerProtocol.SessionId) (_: WorkflowTypes.SessionWorkflow) -> Task.FromResult(Ok "Switching to Hot Reload")
      let ctx = contextWithBody (sprintf """{"viewingSessionId":"%s","workflowTarget":"hotreload"}""" (WorkerProtocol.SessionId.value sid))
      do! createWorkflowSwitchHandler (fun _ -> "REPL") switchFn ctx
      let out = responseText ctx
      let pendingIdx = out.IndexOf("Hot Reload…", StringComparison.Ordinal)
      let finalIdx = out.LastIndexOf("Switching to Hot Reload", StringComparison.Ordinal)
      (pendingIdx, 0) |> Expect.isGreaterThanOrEqual "the optimistic '…' control was pushed"
      (pendingIdx, finalIdx) |> Expect.isLessThan "the pending control precedes the final success message"
    }

    testTask "WHY — a successful switch's final control selects the NEW workflow, not the old one" {
      let switchFn = fun (_: WorkerProtocol.SessionId) (_: WorkflowTypes.SessionWorkflow) -> Task.FromResult(Ok "ok")
      let ctx = contextWithBody (sprintf """{"viewingSessionId":"%s","workflowTarget":"livetesting"}""" (WorkerProtocol.SessionId.value sid))
      do! createWorkflowSwitchHandler (fun _ -> "REPL") switchFn ctx
      let out = responseText ctx
      let selectedBlock =
        System.Text.RegularExpressions.Regex.Match(out, "<option[^>]*selected[^>]*>([^<]*)</option>")
      selectedBlock.Success |> Expect.isTrue "a final switcher with a selected option should be present"
      selectedBlock.Groups.[1].Value |> Expect.equal "the NEW workflow is selected after success" "Live Testing"
    }
  ]

[<Tests>]
let failurePathTests =
  testList "createWorkflowSwitchHandler — failure path" [
    testTask "WHY — a failed switch reverts the control to the ORIGINAL workflow, never leaving it stuck on the failed target" {
      let switchFn = fun (_: WorkerProtocol.SessionId) (_: WorkflowTypes.SessionWorkflow) -> Task.FromResult(Error "worker failed to spawn")
      let ctx = contextWithBody (sprintf """{"viewingSessionId":"%s","workflowTarget":"hotreload"}""" (WorkerProtocol.SessionId.value sid))
      do! createWorkflowSwitchHandler (fun _ -> "REPL") switchFn ctx
      let out = responseText ctx
      let selectedBlock =
        System.Text.RegularExpressions.Regex.Match(out, "<option[^>]*selected[^>]*>([^<]*)</option>")
      selectedBlock.Success |> Expect.isTrue "the reverted switcher should be present"
      selectedBlock.Groups.[1].Value |> Expect.equal "the ORIGINAL workflow (REPL) is selected again, not Hot Reload" "REPL"
      out |> Expect.stringContains "the failure reason is shown" "worker failed to spawn"
    }
  ]
