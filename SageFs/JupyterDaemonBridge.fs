namespace SageFs

open System
open System.IO
open System.Text.Json
open SageFs.McpSessionRouting

/// Bridges the `--jupyter` kernel process to a running SageFs daemon over
/// HTTP — the exact `/exec` contract every other client (dashboard, VS Code,
/// Neovim) already speaks (McpServer.fs's `mapExecutionRoutes`). The Jupyter
/// kernel is its own OS process, started separately from the daemon by
/// `SageFs --jupyter <conn.json>`; this module is what makes it evaluate
/// real F# instead of echoing the source text back
/// (`sprintf "val it: string = \"%s\"" code`, the bug this replaces).
module JupyterDaemonBridge =

  /// One HTTP round-trip: `Ok (statusCode, body)` means the daemon answered
  /// at all (2xx or not); `Error reason` means the request never reached it
  /// — connection refused, DNS failure, timeout, anything below the HTTP
  /// layer.
  type PostResult = Result<int * string, string>

  /// POST a JSON body, get back a `PostResult`. Abstracted so tests can
  /// substitute a fake daemon — no socket, no Kestrel server, no live
  /// process — while the real implementation (wired in Program.fs) is a
  /// thin `HttpClient.PostAsync`.
  type PostJson = string -> Async<PostResult>

  /// What one `/exec` round-trip told us, decoded from the daemon's actual
  /// JSON contract. `Evaluated` covers BOTH a real FSI success and a real
  /// FSI failure: `/exec` keeps eval failures at HTTP 200 with
  /// `success=false` (McpServer.fs:1379-1385, the "truthful 200" contract —
  /// the code ran, it just didn't compile/succeed). `InfraError` covers a
  /// request that never ran at all: the session couldn't be routed to, the
  /// daemon isn't reachable, or its response can't be parsed.
  type ExecOutcome =
    | Evaluated of success: bool * result: string
    | InfraError of SageFsError

  let private tryGetString (name: string) (root: JsonElement) =
    match root.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
    | _ -> None

  /// Pull the RAW (undescribed) `reason` out of a `SessionNotRoutable`
  /// error's `errorDetails.fields` when present. `structuredErrorBody`
  /// (McpServer.fs) puts `SageFsError.describe err` in the flat `error`
  /// field — already prefixed with "Session not reachable: " — while
  /// `errorDetails.fields` carries the DU's own raw field values
  /// (`SageFsError.toJson`). Preferring the raw reason means wrapping it
  /// back into `SessionNotRoutable` here doesn't double the prefix when
  /// `SageFsError.describe` runs on it again for the Jupyter error output.
  let private tryGetSessionNotRoutableReason (root: JsonElement) =
    match root.TryGetProperty("errorDetails") with
    | true, details ->
      match details.TryGetProperty("case") with
      | true, caseEl when caseEl.GetString() = "SessionNotRoutable" ->
        match details.TryGetProperty("fields") with
        | true, fields -> tryGetString "reason" fields
        | false, _ -> None
      | _ -> None
    | false, _ -> None

  /// Decode one `/exec` response. Pure — no I/O — so every branch of the
  /// daemon's real wire contract is provable from a literal JSON string:
  /// see McpServer.fs's `mapExecutionRoutes` (success body, `{success,
  /// result}`) and `structuredErrorBody` (error body, `{success=false,
  /// error, errorDetails}`).
  let parseExecResponse (statusCode: int) (body: string) : ExecOutcome =
    try
      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement
      match statusCode >= 200 && statusCode < 300 with
      | true ->
        let success =
          match root.TryGetProperty("success") with
          | true, v -> v.GetBoolean()
          | false, _ -> true
        let result = tryGetString "result" root |> Option.defaultValue ""
        Evaluated (success, result)
      | false ->
        let message =
          tryGetSessionNotRoutableReason root
          |> Option.orElseWith (fun () -> tryGetString "error" root)
          |> Option.defaultValue (sprintf "daemon returned HTTP %d" statusCode)
        InfraError (SageFsError.SessionNotRoutable message)
    with ex ->
      InfraError (SageFsError.JsonParseError ("/exec response", ex.Message))

  /// The daemon reports "there is no session to route this to" in two shapes
  /// (Mcp.fs's `resolveSessionId`/`formatSessionResolution`): "No active
  /// session. ..." (zero sessions anywhere) and "No sessions match
  /// workingDirectory '...'. ..." (zero sessions match the directory this
  /// bridge always sends) — the second is what a bare daemon actually
  /// returns. Every OTHER `SessionNotRoutable` reason — warming up,
  /// unroutable, faulted, ambiguous — describes a session that EXISTS but
  /// cannot take work, and says so with its own explicit "Do NOT create a
  /// new/duplicate session" guidance, so excluding those takes priority: a
  /// warming-up or faulted session must never be mistaken for "create a new
  /// one".
  ///
  /// The wording itself lives in `SageFs.McpSessionRouting`, next to the
  /// messages the daemon builds, so the two cannot drift apart — they DID
  /// drift once (the daemon's tool list changed on 2026-10-06 and this
  /// bridge's own copy of the sentence went stale), and the symptom was a
  /// kernel that could not evaluate anything on a daemon with no session,
  /// while every test that fed the OLD wording still passed.
  let private isNoSessionAtAll (err: SageFsError) =
    match err with
    | SageFsError.SessionNotRoutable reason -> McpSessionRouting.reasonMeansNoSessionExists reason
    | _ -> false

  /// The real `PostJson` — a thin HTTP POST against one route on the
  /// daemon's MCP port. Kept separate from `makeSessionProxy` so the
  /// decision logic above is fully testable without a socket; this is the
  /// only part of the bridge that actually touches the network.
  let httpPostJson (client: System.Net.Http.HttpClient) (path: string) : PostJson =
    fun body ->
      async {
        try
          use content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
          let! resp = client.PostAsync(path, content) |> Async.AwaitTask
          let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
          return Ok (int resp.StatusCode, respBody)
        with ex ->
          return Error ex.Message
      }

  /// The real readiness probe — a GET of the daemon's session list, which
  /// reports every session with its directory and its status. Same shape as
  /// `PostJson` (so the bridge's decision tree stays testable without a
  /// socket), but it reads rather than writes, and it sends no body.
  let httpGetJson (client: System.Net.Http.HttpClient) (path: string) : PostJson =
    fun _body ->
      async {
        try
          let! resp = client.GetAsync(path) |> Async.AwaitTask
          let! respBody = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
          return Ok (int resp.StatusCode, respBody)
        with ex ->
          return Error ex.Message
      }

  /// Is the session rooted at `workingDirectory` ready to take work?
  ///
  /// The daemon's `GET /api/sessions` lists every session with its
  /// `workingDirectory` and its `status`; a session that is still warming up
  /// says so, and only a `Ready` one can be evaluated. The comparison is on
  /// the normalized path, because the daemon reports an absolute path and
  /// the kernel's own working directory is whatever it was started in.
  let sessionIsReady (body: string) (workingDirectory: string) : bool =
    try
      use doc = JsonDocument.Parse(body)
      let expected =
        if String.IsNullOrWhiteSpace workingDirectory then "" else Path.GetFullPath workingDirectory
      doc.RootElement.GetProperty("sessions").EnumerateArray()
      |> Seq.exists (fun session ->
        let dir = match session.GetProperty("workingDirectory").GetString() with
                  | null -> ""
                  | d -> d
        let sameDir =
          if expected = "" then true
          else String.Equals(Path.GetFullPath dir, expected, StringComparison.OrdinalIgnoreCase)
        sameDir && session.GetProperty("status").GetString() = "Ready")
    // A body the daemon did not send in the shape this reads is not "not
    // ready" and not "ready" either — it is an answer this code cannot
    // read, and the honest answer to "is it ready" is "no, and here is
    // why it could not tell". The named exceptions are the ones the read
    // can actually raise; a wildcard would swallow a bug in the read
    // itself and report it as a session that is merely not ready yet.
    with
    | :? JsonException
    | :? System.Collections.Generic.KeyNotFoundException
    | :? InvalidOperationException ->
      false

  /// Build a real `WorkerProtocol.SessionProxy` that routes `EvalCode` to
  /// the daemon's `/exec` endpoint. Session selection mirrors `/exec`'s own
  /// `resolveSessionId` (Mcp.fs): the caller's already-active session, or —
  /// via the `working_directory` field this bridge always sends — the
  /// session rooted at the kernel's own working directory, or the single
  /// running session if there is exactly one. When none of that resolves —
  /// no session exists anywhere yet — one is created rooted at the kernel's
  /// working directory, WAITED FOR, and the eval is retried once, so a fresh
  /// `SageFs --jupyter` run against a bare daemon works without a manual
  /// `create_session` step first. `execPost` and `createSessionPost` are
  /// injected so the whole decision tree is testable without a socket.
  ///
  /// `waitForReadyPost` answers "is the session for this working directory
  /// ready yet" — the bridge MUST ask before it retries, because a session
  /// that was just created is still warming up (15-30s for a test project)
  /// and an eval sent into a warming-up session is refused. Without the
  /// wait, the retry is refused too, and a fresh kernel can never evaluate
  /// anything.
  let makeSessionProxy
    (execPost: PostJson)
    (createSessionPost: PostJson)
    (waitForReadyPost: PostJson)
    (workingDirectory: string)
    (warmupBudget: TimeSpan)
    : WorkerProtocol.SessionProxy =

    let execOnce (code: string) : Async<ExecOutcome> =
      async {
        let body = Json.serialize Json.standard {| code = code; working_directory = workingDirectory |}
        let! outcome = execPost body
        match outcome with
        | Ok (status, respBody) -> return parseExecResponse status respBody
        | Error transportMessage ->
          Utils.Log.warn "[JupyterDaemonBridge] /exec unreachable: %s" transportMessage
          return InfraError SageFsError.DaemonNotRunning
      }

    /// Wait until the session for this working directory is Ready.
    ///
    /// The daemon answers "still warming up" until the session is up; the
    /// bridge polls until it is not, and gives up on a budget rather than
    /// waiting forever, so a session that never comes up fails the eval
    /// instead of hanging the kernel.
    let waitForReady (budget: TimeSpan) : Async<bool> =
      async {
        let deadline = DateTime.UtcNow.Add(budget)
        let mutable ready = false
        while not ready && DateTime.UtcNow < deadline do
          let! outcome = waitForReadyPost ""
          match outcome with
          | Ok (status, respBody) when status = 200 -> ready <- sessionIsReady respBody workingDirectory
          | Ok (_, _) -> ()
          | Error transportMessage ->
            Utils.Log.warn "[JupyterDaemonBridge] session wait unreachable: %s" transportMessage
            ready <- false
          if not ready then
            do! Async.Sleep Timeouts.jupyterSessionWaitPoll
        return ready
      }

    let toEvalResult (replyId: string) (outcome: ExecOutcome) =
      match outcome with
      | Evaluated (true, result) -> WorkerProtocol.WorkerResponse.EvalResult(replyId, Ok result, [], Map.empty)
      | Evaluated (false, result) -> WorkerProtocol.WorkerResponse.EvalResult(replyId, Error (SageFsError.EvalFailed result), [], Map.empty)
      | InfraError err -> WorkerProtocol.WorkerResponse.WorkerError err

    fun msg ->
      async {
        match msg with
        | WorkerProtocol.WorkerMessage.EvalCode (code, replyId) ->
          let! first = execOnce code
          match first with
          | InfraError err when isNoSessionAtAll err ->
            let createBody =
              Json.serialize Json.standard {| workingDirectory = workingDirectory; projects = Array.empty<string> |}
            let! created = createSessionPost createBody
            match created with
            | Ok (status, _) when status >= 200 && status < 300 ->
              // A session that was just created is still warming up, and an
              // eval sent into a warming-up session is refused. So the
              // bridge waits for it before it retries — otherwise the retry
              // fails the same way and a fresh kernel can never evaluate
              // anything.
              let! ready = waitForReady warmupBudget
              if not ready then
                return WorkerProtocol.WorkerResponse.WorkerError (
                  SageFsError.SessionCreationFailed (
                    sprintf
                      "the session for %s did not become ready within %O — the daemon created it, but it never finished warming up"
                      workingDirectory
                      warmupBudget))
              else
                let! retried = execOnce code
                return toEvalResult replyId retried
            | Ok (status, respBody) ->
              return WorkerProtocol.WorkerResponse.WorkerError (
                SageFsError.SessionCreationFailed (sprintf "HTTP %d: %s" status respBody))
            | Error transportMessage ->
              Utils.Log.warn "[JupyterDaemonBridge] session create unreachable: %s" transportMessage
              return WorkerProtocol.WorkerResponse.WorkerError SageFsError.DaemonNotRunning
          | other -> return toEvalResult replyId other
        | WorkerProtocol.WorkerMessage.GetCompletions (_, _, replyId) ->
          // /exec has no completions endpoint — report "no matches" rather
          // than pretending to be disconnected from a daemon we ARE
          // connected to.
          return WorkerProtocol.WorkerResponse.CompletionResult(replyId, [])
        | other ->
          return WorkerProtocol.WorkerResponse.WorkerError (
            SageFsError.Unexpected (NotSupportedException(sprintf "%A is not supported by the Jupyter daemon bridge" other)))
      }
