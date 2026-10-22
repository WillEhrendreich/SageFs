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

/// A readiness probe that says the session for the working directory is
/// already up. The bridge only asks it after it has created a session, and
/// the answer it needs is "yes, it is Ready".
let private readyPost (workingDirectory: string) : PostJson =
  fun _body ->
    async {
      return
        Ok (
          200,
          sprintf """{"sessions":[{"workingDirectory":"%s","status":"Ready"}]}""" workingDirectory
        )
    }

/// A short warmup budget for the tests: the bridge only has to prove it
/// WAITS, not how long it would really wait for a cold build.
let private testWarmupBudget = TimeSpan.FromSeconds 5.0

let private replyId = "abc123"

[<Tests>]
let makeSessionProxyTests =
  testList "JupyterDaemonBridge.makeSessionProxy" [

    testAsync "WHY — EvalCode routes through /exec and returns the daemon's real result, proving the echo stub is gone" {
      let execPost = fakePost (Ok (200, """{"success":true,"result":"val it: int = 2"}"""))
      let createPost = fakePost (Ok (200, """{"success":true,"message":"unused"}"""))
      let proxy = makeSessionProxy execPost createPost (readyPost "/work/dir") "/work/dir" testWarmupBudget
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
      let proxy = makeSessionProxy execPost createPost (readyPost "/work/dir") "/work/dir" testWarmupBudget
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
            | 1 -> return Ok (404, """{"success":false,"error":"Session not reachable: No active session. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session to create one first.","errorDetails":{"case":"SessionNotRoutable"}}""")
            | _ -> return Ok (200, """{"success":true,"result":"val it: int = 2"}""")
          }
      let createPost : PostJson =
        fun body ->
          async {
            createCalls <- createCalls + 1
            lastCreateBody <- body
            return Ok (200, """{"success":true,"message":"a1b2c3d4"}""")
          }
      let proxy = makeSessionProxy execPost createPost (readyPost "/kernel/cwd") "/kernel/cwd" testWarmupBudget
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      execCalls |> Expect.equal "exec called twice: once to discover no session, once to retry" 2
      createCalls |> Expect.equal "a session was created exactly once" 1
      lastCreateBody |> Expect.stringContains "created for the kernel's own working directory" "/kernel/cwd"
      match response with
      | WorkerProtocol.WorkerResponse.EvalResult(_, Ok result, _, _) ->
        result |> Expect.equal "the retried eval's real result" "val it: int = 2"
      | other -> failtestf "expected EvalResult Ok after auto-create, got %A" other
    }

    testAsync "WHY — the bridge WAITS for the session it just created to be ready before it retries, because an eval sent into a warming-up session is refused" {
      let mutable execCalls = 0
      let mutable readyCalls = 0
      let execPost : PostJson =
        fun _body ->
          async {
            execCalls <- execCalls + 1
            match execCalls with
            | 1 -> return Ok (404, """{"success":false,"error":"Session not reachable: No active session. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session to create one first.","errorDetails":{"case":"SessionNotRoutable"}}""")
            | _ -> return Ok (200, """{"success":true,"result":"val it: int = 2"}""")
          }
      let createPost : PostJson =
        fun _body -> async { return Ok (200, """{"success":true,"message":"a1b2c3d4"}""") }
      // The session is NOT ready at first: the daemon reports it as still
      // warming up. The bridge must keep asking until it says Ready.
      let waitPost : PostJson =
        fun _body ->
          async {
            readyCalls <- readyCalls + 1
            if readyCalls < 3 then
              return Ok (200, """{"sessions":[{"workingDirectory":"/kernel/cwd","status":"Starting"}]}""")
            else
              return Ok (200, """{"sessions":[{"workingDirectory":"/kernel/cwd","status":"Ready"}]}""")
          }
      let proxy = makeSessionProxy execPost createPost waitPost "/kernel/cwd" testWarmupBudget
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      (readyCalls >= 3) |> Expect.isTrue "the bridge asked again while the session was still warming up"
      match response with
      | WorkerProtocol.WorkerResponse.EvalResult(_, Ok result, _, _) ->
        result |> Expect.equal "the eval ran once the session was ready" "val it: int = 2"
      | other -> failtestf "expected EvalResult Ok after waiting for the session, got %A" other
    }

    testAsync "WHY — a session that never becomes ready is reported as such, instead of the eval being retried into a warming-up session forever" {
      let mutable execCalls = 0
      let execPost : PostJson =
        fun _body ->
          async {
            execCalls <- execCalls + 1
            return Ok (404, """{"success":false,"error":"Session not reachable: No active session. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session to create one first.","errorDetails":{"case":"SessionNotRoutable"}}""")
          }
      let createPost : PostJson =
        fun _body -> async { return Ok (200, """{"success":true,"message":"a1b2c3d4"}""") }
      // The session never leaves "Starting".
      let waitPost : PostJson =
        fun _body -> async { return Ok (200, """{"sessions":[{"workingDirectory":"/kernel/cwd","status":"Starting"}]}""") }
      let proxy = makeSessionProxy execPost createPost waitPost "/kernel/cwd" (TimeSpan.FromSeconds 1.0)
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      execCalls |> Expect.equal "the eval was not retried into a session that never became ready" 1
      match response with
      | WorkerProtocol.WorkerResponse.WorkerError (SageFsError.SessionCreationFailed _) -> ()
      | other -> failtestf "expected WorkerError SessionCreationFailed, got %A" other
    }

    testAsync "WHY — the real daemon's shape when working_directory matches nothing (zero sessions at all) is 'No sessions match workingDirectory ... Use create_session', not 'No active session.' — both must trigger auto-create" {
      let mutable execCalls = 0
      let mutable createCalls = 0
      let execPost : PostJson =
        fun _body ->
          async {
            execCalls <- execCalls + 1
            match execCalls with
            | 1 ->
              // The daemon's real structuredErrorBody shape: `error` is the
              // already-described display string (SageFsError.describe),
              // `errorDetails.fields.reason` is the RAW undecorated reason —
              // this bridge must read the raw one so it doesn't end up
              // double-describing ("Session not reachable: Session not
              // reachable: ...") when it re-wraps the error for Jupyter.
              return Ok (404, """{"success":false,"error":"Session not reachable: No sessions match workingDirectory '/kernel/cwd'. Running sessions: (none running). Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session for that directory, or switch_session to an existing matching session.","errorDetails":{"case":"SessionNotRoutable","fields":{"reason":"No sessions match workingDirectory '/kernel/cwd'. Running sessions: (none running). Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session for that directory, or switch_session to an existing matching session."}}}""")
            | _ -> return Ok (200, """{"success":true,"result":"val it: int = 2"}""")
          }
      let createPost : PostJson = fun _ -> async { createCalls <- createCalls + 1; return Ok (200, """{"success":true,"message":"a1b2c3d4"}""") }
      let proxy = makeSessionProxy execPost createPost (readyPost "/kernel/cwd") "/kernel/cwd" testWarmupBudget
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      createCalls |> Expect.equal "a session was created for the unmatched working directory" 1
      match response with
      | WorkerProtocol.WorkerResponse.EvalResult(_, Ok result, _, _) ->
        result |> Expect.equal "the retried eval's real result" "val it: int = 2"
      | other -> failtestf "expected EvalResult Ok after auto-create, got %A" other
    }

    testAsync "WHY — a session that is merely warming up must NOT be treated as 'create one': the daemon's own guidance says not to" {
      let mutable createCalls = 0
      let execPost = fakePost (Ok (404, """{"success":false,"error":"Session 'abc123' is still warming up (Starting). Do NOT create a new session — it will compete for resources and make warmup slower.","errorDetails":{"case":"SessionNotRoutable"}}"""))
      let createPost : PostJson = fun _ -> async { createCalls <- createCalls + 1; return Ok (200, "{}") }
      let proxy = makeSessionProxy execPost createPost (readyPost "/work/dir") "/work/dir" testWarmupBudget
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      createCalls |> Expect.equal "no session was created while one is warming up" 0
      match response with
      | WorkerProtocol.WorkerResponse.WorkerError (SageFsError.SessionNotRoutable _) -> ()
      | other -> failtestf "expected WorkerError SessionNotRoutable, got %A" other
    }

    // ── The wording guard ──
    //
    // The bridge decides "may I create a session?" by reading the daemon's
    // reason, and it once read a COPY of that sentence. The daemon's tool
    // list then changed (2026-10-06) and the copy went stale: the kernel
    // could no longer create a session, while every test here still passed
    // because each one fed its own (old) literal. This test is the guard
    // against that shape of drift: it builds the reason from the SAME
    // constant the daemon builds its message from, so a change to the
    // wording can no longer leave the bridge behind.
    testCase "WHY — the bridge's 'no session exists' reading is driven by the daemon's own wording, so the two cannot drift apart" <| fun () ->
      let anywhere = sprintf "%s Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session to create one first." McpSessionRouting.noSessionAnywhereOpener
      let forDirectory =
        sprintf "%s '%s'. Running sessions: %s. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session for that directory, or switch_session to an existing matching session."
          McpSessionRouting.noSessionMatchesWorkingDirectoryOpener
          "/work/dir"
          "(none running)"

      McpSessionRouting.reasonMeansNoSessionExists anywhere
      |> Expect.isTrue "the daemon's 'no active session' reason means no session exists"

      McpSessionRouting.reasonMeansNoSessionExists forDirectory
      |> Expect.isTrue "the daemon's 'no sessions match workingDirectory' reason means no session exists"

      // The same reason reaches a client DESCRIBED too — the flat `error`
      // field carries `SageFsError.describe`'s "Session not reachable: "
      // prefix — so the opener is in the middle of that string, not at the
      // start of it. A client that only understood the raw shape would
      // fail to auto-create on a daemon that sends the described one.
      let described = sprintf "Session not reachable: %s" forDirectory
      McpSessionRouting.reasonMeansNoSessionExists described
      |> Expect.isTrue "the DESCRIBED shape of the same reason means no session exists too"

    testCase "WHY — a reason about a session that EXISTS but cannot take work is not 'no session exists', whatever its tail says" <| fun () ->
      // Same opener, but the daemon's own 'Do NOT create' guidance: the
      // exclusion must win, or a warming-up session gets a duplicate.
      let warmingUp =
        sprintf "%s '/work/dir'. Do NOT create a new session — it will compete for resources." McpSessionRouting.noSessionMatchesWorkingDirectoryOpener
      McpSessionRouting.reasonMeansNoSessionExists warmingUp
      |> Expect.isFalse "a session that exists but is warming up is not 'no session exists'"

      let ambiguous = "Multiple sessions match workingDirectory '/work/dir'."
      McpSessionRouting.reasonMeansNoSessionExists ambiguous
      |> Expect.isFalse "an ambiguous match is not 'no session exists'"

    testAsync "WHY — an unreachable daemon (no /exec at all) is a clear, actionable failure, never a silent echo" {
      let execPost : PostJson = fun _ -> async { return Error "Connection refused" }
      let createPost = fakePost (Ok (200, "{}"))
      let proxy = makeSessionProxy execPost createPost (readyPost "/work/dir") "/work/dir" testWarmupBudget
      let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode("1 + 1", replyId))
      match response with
      | WorkerProtocol.WorkerResponse.WorkerError SageFsError.DaemonNotRunning -> ()
      | other -> failtestf "expected WorkerError DaemonNotRunning, got %A" other
    }

    testAsync "WHY — GetCompletions has no daemon endpoint yet; empty matches, not a lie about being disconnected" {
      let execPost = fakePost (Ok (200, "{}"))
      let createPost = fakePost (Ok (200, "{}"))
      let proxy = makeSessionProxy execPost createPost (readyPost "/work/dir") "/work/dir" testWarmupBudget
      let! response = proxy (WorkerProtocol.WorkerMessage.GetCompletions("List.", 5, replyId))
      match response with
      | WorkerProtocol.WorkerResponse.CompletionResult(rid, matches) ->
        rid |> Expect.equal "replyId" replyId
        matches |> Expect.isEmpty "no completions wired yet"
      | other -> failtestf "expected CompletionResult, got %A" other
    }
  ]
