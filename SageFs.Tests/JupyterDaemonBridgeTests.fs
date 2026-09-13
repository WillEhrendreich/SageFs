module SageFs.Tests.JupyterDaemonBridgeTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.JupyterDaemonBridge

// ── parseExecResponse: pure decoding of the daemon's /exec wire contract ──
// (McpServer.fs's mapExecutionRoutes — success body — and structuredErrorBody
// — error body). No I/O, no daemon, no socket: every branch is provable from
// a literal JSON string.

[<Tests>]
let parseExecResponseTests =
  testList "JupyterDaemonBridge.parseExecResponse" [

    testCase "WHY — a successful eval (200, success=true) decodes to Evaluated true with the real result, not an echo" <| fun () ->
      let outcome = parseExecResponse 200 """{"success":true,"result":"val it: int = 2"}"""
      outcome |> Expect.equal "decoded" (Evaluated (true, "val it: int = 2"))

    testCase "WHY — /exec's truthful-200 contract: a compile/runtime failure still returns HTTP 200 with success=false" <| fun () ->
      let outcome = parseExecResponse 200 """{"success":false,"result":"error FS0039: not defined","error":"error FS0039: not defined"}"""
      outcome |> Expect.equal "decoded" (Evaluated (false, "error FS0039: not defined"))

    testCase "WHY — a non-2xx status is an infra failure the eval never ran, decoded from the structured error body" <| fun () ->
      let outcome = parseExecResponse 404 """{"success":false,"error":"Session not reachable: No active session.","errorDetails":{"case":"SessionNotRoutable"}}"""
      outcome |> Expect.equal "decoded" (InfraError (SageFsError.SessionNotRoutable "Session not reachable: No active session."))

    testCase "WHY — a non-2xx status with no error field still surfaces the HTTP status, never silently succeeds" <| fun () ->
      let outcome = parseExecResponse 503 "{}"
      match outcome with
      | InfraError (SageFsError.SessionNotRoutable msg) -> msg |> Expect.stringContains "mentions the status" "503"
      | other -> failtestf "expected InfraError SessionNotRoutable, got %A" other

    testCase "WHY — malformed JSON from the daemon is a decode failure, never a silent success" <| fun () ->
      let outcome = parseExecResponse 200 "not json at all"
      match outcome with
      | InfraError (SageFsError.JsonParseError _) -> ()
      | other -> failtestf "expected InfraError JsonParseError, got %A" other
  ]

// ── makeSessionProxy: the real bridge, with the daemon's HTTP surface faked
// out (no network, no socket, no live process) so the RED→GREEN proof that
// EvalCode reaches "a session" and returns a REAL result — not the old
// `sprintf "val it: string = \"%s\"" code` echo — needs nothing but this
// process. ──

let private fakePost (response: PostResult) : PostJson =
  fun _body -> async { return response }

let private replyId = "abc123"

[<Tests>]
let makeSessionProxyTests =
  testList "JupyterDaemonBridge.makeSessionProxy" [

    testAsync "WHY — EvalCode routes through /exec and returns the daemon's real result, proving the echo stub is gone" {
      let execPost = fakePost (Ok (200, """{"success":true,"result":"val it: int = 2"}"""))
      let createPost = fakePost (Ok (200, """{"success":true,"message":"unused"}"""))
      let proxy = makeSessionProxy execPost createPost "/work/dir"
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      match response with
      | WorkerProtocol.WorkerResponse.EvalResult(rid, Ok result, _, _) ->
        rid |> Expect.equal "replyId" replyId
        result |> Expect.equal "real result, not an echo of the source code" "val it: int = 2"
      | other -> failtestf "expected EvalResult Ok, got %A" other
    }

    testAsync "WHY — a real compile failure from the daemon surfaces as a real eval error, not a fabricated echo" {
      let execPost = fakePost (Ok (200, """{"success":false,"result":"error FS0039: not defined","error":"error FS0039: not defined"}"""))
      let createPost = fakePost (Ok (200, "{}"))
      let proxy = makeSessionProxy execPost createPost "/work/dir"
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("bogus", replyId))
      match response with
      | WorkerProtocol.WorkerResponse.EvalResult(_, Error (SageFsError.EvalFailed reason), _, _) ->
        reason |> Expect.equal "reason" "error FS0039: not defined"
      | other -> failtestf "expected EvalResult Error EvalFailed, got %A" other
    }

    testAsync "WHY — when no session exists anywhere, the bridge creates one for the kernel's own working dir and retries the eval" {
      let mutable execCalls = 0
      let mutable createCalls = 0
      let mutable lastCreateBody = ""
      let execPost : PostJson =
        fun _body ->
          async {
            execCalls <- execCalls + 1
            match execCalls with
            | 1 -> return Ok (404, """{"success":false,"error":"Session not reachable: No active session. Use create_session to create one first.","errorDetails":{"case":"SessionNotRoutable"}}""")
            | _ -> return Ok (200, """{"success":true,"result":"val it: int = 2"}""")
          }
      let createPost : PostJson =
        fun body ->
          async {
            createCalls <- createCalls + 1
            lastCreateBody <- body
            return Ok (200, """{"success":true,"message":"a1b2c3d4"}""")
          }
      let proxy = makeSessionProxy execPost createPost "/kernel/cwd"
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      execCalls |> Expect.equal "exec called twice: once to discover no session, once to retry" 2
      createCalls |> Expect.equal "a session was created exactly once" 1
      lastCreateBody |> Expect.stringContains "created for the kernel's own working directory" "/kernel/cwd"
      match response with
      | WorkerProtocol.WorkerResponse.EvalResult(_, Ok result, _, _) ->
        result |> Expect.equal "the retried eval's real result" "val it: int = 2"
      | other -> failtestf "expected EvalResult Ok after auto-create, got %A" other
    }

    testAsync "WHY — a session that is merely warming up must NOT be treated as 'create one': the daemon's own guidance says not to" {
      let mutable createCalls = 0
      let execPost = fakePost (Ok (404, """{"success":false,"error":"Session 'abc123' is still warming up (Starting). Do NOT create a new session — it will compete for resources and make warmup slower.","errorDetails":{"case":"SessionNotRoutable"}}"""))
      let createPost : PostJson = fun _ -> async { createCalls <- createCalls + 1; return Ok (200, "{}") }
      let proxy = makeSessionProxy execPost createPost "/work/dir"
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      createCalls |> Expect.equal "no session was created while one is warming up" 0
      match response with
      | WorkerProtocol.WorkerResponse.WorkerError (SageFsError.SessionNotRoutable _) -> ()
      | other -> failtestf "expected WorkerError SessionNotRoutable, got %A" other
    }

    testAsync "WHY — an unreachable daemon (no /exec at all) is a clear, actionable failure, never a silent echo" {
      let execPost : PostJson = fun _ -> async { return Error "Connection refused" }
      let createPost = fakePost (Ok (200, "{}"))
      let proxy = makeSessionProxy execPost createPost "/work/dir"
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      match response with
      | WorkerProtocol.WorkerResponse.WorkerError SageFsError.DaemonNotRunning -> ()
      | other -> failtestf "expected WorkerError DaemonNotRunning, got %A" other
    }

    testAsync "WHY — GetCompletions has no daemon endpoint yet; empty matches, not a lie about being disconnected" {
      let execPost = fakePost (Ok (200, "{}"))
      let createPost = fakePost (Ok (200, "{}"))
      let proxy = makeSessionProxy execPost createPost "/work/dir"
      let! response = proxy (WorkerProtocol.WorkerMessage.GetCompletions("List.", 5, replyId))
      match response with
      | WorkerProtocol.WorkerResponse.CompletionResult(rid, matches) ->
        rid |> Expect.equal "replyId" replyId
        matches |> Expect.isEmpty "no completions wired yet"
      | other -> failtestf "expected CompletionResult, got %A" other
    }
  ]
