namespace SageFs

open System.Threading.Tasks
open SageFs.Features.AutoCompletion
open SageFs.McpRouteError
open SageFs.McpSessionRouting
open SageFs.McpTools

/// The completions routes, out of `Mcp.fs` (the accretion hub whose line budget only goes down): what a
/// worker says about the members at a cursor, asked for on behalf of the editors' HTTP routes and of the
/// symbol ladder. Every reply is a `CompletionAnswer`: candidates with the kind FCS gave each and the size
/// of the set, or the named reason nobody could look.
module McpCompletions =

  /// What a worker's reply to `GetCompletions` says. A reply that is not a completion list, or no reply
  /// at all, is a named reason and never an empty list.
  let completionAnswerOf
    (routeResult: Result<WorkerProtocol.WorkerResponse, RouteError>)
    : CompletionAnswer<CompletionEntry> =
    match routeResult with
    | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, answer)) -> answer
    | Ok other ->
      CompletionAnswer.Unavailable(
        CompletionUnavailable.WorkerUnreachable(
          sprintf "the worker answered a completion request with %s" (other.GetType().Name)))
    | Error msg ->
      CompletionAnswer.Unavailable(CompletionUnavailable.WorkerUnreachable(routeErrorMessage msg))

  /// Get structured completion items for HTTP/editor clients. Unlike the MCP
  /// tool's human-readable response, this path must always serialize as JSON.
  let getCompletionsLimited
    (ctx: McpContext)
    (agent: string)
    (code: string)
    (cursorPosition: int)
    (limit: CompletionLimit)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<CompletionAnswer<CompletionItem>> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid ->
        let! routeResult =
          routeToSession ctx sid
            (fun replyId ->
              WorkerProtocol.WorkerMessage.GetCompletions(
                code,
                cursorPosition,
                limit,
                WorkerProtocol.SessionId.value replyId))
        return completionAnswerOf routeResult |> CompletionAnswer.map CompletionEntry.toItem
      | other ->
        return CompletionAnswer.Unavailable(CompletionUnavailable.NoSession(formatSessionResolution other))
    }

  /// The editor-sized answer: the few best candidates, with the size of the set they came from.
  let getCompletionsItems
    (ctx: McpContext)
    (agent: string)
    (code: string)
    (cursorPosition: int)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<CompletionAnswer<CompletionItem>> =
    getCompletionsLimited
      ctx agent code cursorPosition (CompletionLimit.UpTo EditorCandidates) sessionId workingDirectory

  /// The JSON of `POST /api/completions`. An answer carries `completions` and `count` (the items in this reply)
  /// plus `total` and `totalKind` ("exact" or "atLeast"), so a client can say "50 of 212". A reply that could not
  /// look carries no items and `unavailable` (a stable word) with `reason` (what is true and what to do).
  let completionItemJson (item: CompletionItem) : string =
    let escape = McpAdapter.escapeJson
    let detail =
      match item.GetDescription with
      | Some getDesc ->
        try
          let tags = getDesc ()
          let text = tags |> Array.map (fun t -> t.Text) |> String.concat ""
          match text.Length > 0 with
          | true -> sprintf ""","detail":"%s" """ (escape text)
          | false -> ""
        with
        | :? System.OperationCanceledException -> reraise()
        | ex ->
          // A description that cannot be made costs the row its tooltip, not the list its items.
          Utils.Log.warn "[completions] no description for %s: %s" item.DisplayText ex.Message
          ""
      | None -> ""
    sprintf """{"label":"%s","kind":"%s","insertText":"%s"%s}"""
      (escape item.DisplayText) (CompletionKind.label item.Kind) (escape item.ReplacementText) detail

  let formatCompletionsJson (answer: CompletionAnswer<CompletionItem>) : string =
    match answer with
    | CompletionAnswer.Unavailable reason ->
      sprintf """{"completions":[],"count":0,"unavailable":"%s","reason":"%s"}"""
        (McpAdapter.escapeJson (CompletionUnavailable.label reason))
        (McpAdapter.escapeJson (CompletionUnavailable.describe reason))
    | CompletionAnswer.Answered(items, total) ->
      sprintf """{"completions":[%s],"count":%d,"total":%d,"totalKind":"%s"}"""
        (items |> List.map completionItemJson |> String.concat ",")
        (List.length items)
        (CompletionTotal.count total)
        (CompletionTotal.label total)

  /// Ask the session's worker for every member of a name (up to the wire ceiling).
  let membersOf (ctx: McpContext) (sid: string) (name: string) : Task<CompletionAnswer<CompletionEntry>> =
    task {
      let code = sprintf "%s." name
      let! routeResult =
        routeToSession ctx sid
          (fun replyId ->
            WorkerProtocol.WorkerMessage.GetCompletions(code, code.Length, CompletionLimit.EveryMember, WorkerProtocol.SessionId.value replyId))
      return completionAnswerOf routeResult
    }

  let exploreQualifiedName (ctx: McpContext) (agent: string) (qualifiedName: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent (RouteTarget.ofOption workingDirectory) (fun sid -> task {
      let code = sprintf "%s." qualifiedName
      let cursor = code.Length
      let! routeResult =
        routeToSession ctx sid
          (fun replyId ->
            WorkerProtocol.WorkerMessage.GetCompletions(
              code, cursor, CompletionLimit.UpTo EditorCandidates, WorkerProtocol.SessionId.value replyId))
      return
        match completionAnswerOf routeResult with
        | CompletionAnswer.Unavailable reason ->
          sprintf "Could not list members of '%s': %s" qualifiedName (CompletionUnavailable.describe reason)
        | CompletionAnswer.Answered([], _) ->
          sprintf "No members found for '%s'" qualifiedName
        | CompletionAnswer.Answered(completions, total) ->
          let header = sprintf "Members of %s:" qualifiedName
          let items = completions |> List.map (fun c -> sprintf "  %s" c.DisplayText) |> String.concat "\n"
          let more =
            match CompletionTotal.count total > completions.Length with
            | true -> sprintf "\n(showing %d of %s)" completions.Length (CompletionTotal.describe total)
            | false -> ""
          sprintf "%s\n%s%s" header items more
    })

  let exploreNamespace (ctx: McpContext) (agent: string) (namespaceName: string) (workingDirectory: string option) : Task<string> =
    exploreQualifiedName ctx agent namespaceName workingDirectory
