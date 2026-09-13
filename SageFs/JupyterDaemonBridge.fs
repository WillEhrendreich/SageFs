namespace SageFs

open System
open System.Text.Json

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

  /// The daemon suggests `create_session` in exactly two `SessionNotRoutable`
  /// shapes (Mcp.fs's `resolveSessionId`/`formatSessionResolution`): "No
  /// active session. Use create_session to create one first." (zero
  /// sessions anywhere, no working directory given) and "No sessions match
  /// workingDirectory '...'. ... Use create_session with that directory,
  /// or switch_session ..." (zero sessions match the directory this bridge
  /// always sends) — the second is what a bare daemon actually returns,
  /// since this bridge always sends `working_directory`. Every OTHER
  /// `SessionNotRoutable` reason — warming up, unroutable, faulted,
  /// ambiguous — carries its own explicit "Do NOT create a new/duplicate
  /// session" guidance baked into the same string, so excluding those
  /// takes priority: a warming-up or faulted session must never be mistaken
  /// for "create a new one". There is no structured signal for this
  /// distinction over the wire today (`SageFsError.toJson`'s `case` is
  /// `SessionNotRoutable` for all of them alike) — matching the literal
  /// daemon message is the only signal this bridge has.
  let private isNoSessionAtAll (err: SageFsError) =
    match err with
    | SageFsError.SessionNotRoutable reason ->
      reason.Contains("Use create_session") && not (reason.Contains("Do NOT create"))
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

  /// Build a real `WorkerProtocol.SessionProxy` that routes `EvalCode` to
  /// the daemon's `/exec` endpoint. Session selection mirrors `/exec`'s own
  /// `resolveSessionId` (Mcp.fs): the caller's already-active session, or —
  /// via the `working_directory` field this bridge always sends — the
  /// session rooted at the kernel's own working directory, or the single
  /// running session if there is exactly one. When none of that resolves —
  /// no session exists anywhere yet — one is created rooted at the kernel's
  /// working directory and the eval is retried once, so a fresh `SageFs
  /// --jupyter` run against a bare daemon works without a manual
  /// `create_session` step first. `execPost` and `createSessionPost` are
  /// injected so the whole decision tree is testable without a socket.
  let makeSessionProxy
    (execPost: PostJson)
    (createSessionPost: PostJson)
    (workingDirectory: string)
    : WorkerProtocol.SessionProxy =

    let execOnce (code: string) : Async<ExecOutcome> =
      async {
        let body = JsonSerializer.Serialize {| code = code; working_directory = workingDirectory |}
        let! outcome = execPost body
        match outcome with
        | Ok (status, respBody) -> return parseExecResponse status respBody
        | Error transportMessage ->
          Utils.Log.warn "[JupyterDaemonBridge] /exec unreachable: %s" transportMessage
          return InfraError SageFsError.DaemonNotRunning
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
              JsonSerializer.Serialize {| workingDirectory = workingDirectory; projects = Array.empty<string> |}
            let! created = createSessionPost createBody
            match created with
            | Ok (status, _) when status >= 200 && status < 300 ->
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
