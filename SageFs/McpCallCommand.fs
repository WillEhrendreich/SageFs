module SageFs.Server.McpCallCommand

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open SageFs
open SageFs.McpBridge
open SageFs.McpCall
open SageFs.Server.McpStdioBridge

/// `sagefs call`: call one daemon tool from a shell, a script, a hook or CI, as the member whose token it carries.
///
/// It speaks to the daemon's streamable-HTTP MCP endpoint in this process, through the stdio bridge's own client
/// (`McpStdioBridge.forward`), so there is no child process and no pipe to drain, and a session the daemon no
/// longer knows is healed the way the bridge heals it. Every decision (how the words are read, how an argument is typed
/// from the tool's schema, which stream and which exit code) is `SageFs.McpCall`; this file is the IO those decisions
/// drive.

/// One conversation with the daemon: the session id it handed out, which later requests carry.
type Conversation(port: int) =
  let mutable sessionId: string option = None

  member _.SessionId = sessionId

  /// Send one JSON-RPC message and return every message the daemon wrote in answer.
  member _.Send(client: HttpClient, raw: string) : Task<Result<string list, ForwardError>> =
    task {
      let answered = ResizeArray<string>()
      let! sent =
        forward client port answered.Add (fun fresh -> sessionId <- Some fresh) sessionId (Some Rpc.initializeParams) (parseRpcMessage raw)
      return sent |> Result.map (fun () -> List.ofSeq answered)
    }

let outcomeOfForward (error: ForwardError) : Outcome =
  match error with
  | ForwardError.Rejected reason
  | ForwardError.SessionInvalid reason -> Outcome.DaemonRefused reason
  | ForwardError.Unreachable reason -> Outcome.DaemonGone reason

/// Send a request and read the answer to its id.
let ask (conversation: Conversation) (client: HttpClient) (id: int) (raw: string) : Task<Result<string, Outcome>> =
  task {
    let! sent = conversation.Send(client, raw)
    match sent with
    | Result.Error error -> return Result.Error(outcomeOfForward error)
    | Result.Ok lines ->
      match Rpc.answerTo id lines with
      | Answer.Succeeded result -> return Result.Ok result
      | Answer.Failed failure -> return Result.Error(Outcome.RpcFailed failure)
      | Answer.Unanswered -> return Result.Error(Outcome.BadAnswer(sprintf "no answer to request %d" id))
  }

/// A client that carries the credentials on every request it makes, so a token never appears in a message.
let clientFor (credentials: Credentials) (timeout: TimeSpan) : HttpClient =
  let client = new HttpClient(Timeout = timeout)
  for name, value in credentials.Headers do
    client.DefaultRequestHeaders.Add(name, value)
  client

/// The tool called, once its name and arguments have been checked against what the daemon says it takes.
let callTool (conversation: Conversation) (client: HttpClient) (tool: ToolSpec) (argumentsJson: string) : Task<Outcome> =
  task {
    let! called = ask conversation client Rpc.toolsCallId (Rpc.toolsCallRequest Rpc.toolsCallId tool argumentsJson)
    match called with
    | Result.Error outcome -> return outcome
    | Result.Ok result ->
      match ToolReply.parse result with
      | Some reply -> return Outcome.Replied reply
      | None -> return Outcome.BadAnswer "the tools/call result is not JSON"
  }

/// Shake hands, read the tool list, then list or call. The session is the caller's to end.
let converse (conversation: Conversation) (quick: HttpClient) (patient: HttpClient) (args: CallArgs) : Task<Outcome> =
  task {
    let! initialized = ask conversation quick Rpc.initializeId (Rpc.initializeRequest Rpc.initializeId)
    match initialized with
    | Result.Error outcome -> return outcome
    | Result.Ok _ ->
      let! acknowledged = conversation.Send(quick, Rpc.initializedNotification)
      match acknowledged with
      | Result.Error error -> return outcomeOfForward error
      | Result.Ok _ ->
        let! listed = ask conversation quick Rpc.toolsListId (Rpc.toolsListRequest Rpc.toolsListId)
        match listed |> Result.map parseTools with
        | Result.Error outcome -> return outcome
        | Result.Ok(Result.Error fault) -> return Outcome.BadAnswer(ToolListFault.describe fault)
        | Result.Ok(Result.Ok tools) ->
          match args.Target with
          | Target.ListTools -> return Outcome.ListedTools tools
          | Target.CallTool name ->
            let checkedCall =
              Arguments.findTool tools name
              |> Result.bind (fun tool -> Arguments.ofPayload tool args.Payload |> Result.map (fun json -> tool, json))
            match checkedCall with
            | Result.Error refusal -> return Outcome.ArgsRefused refusal
            | Result.Ok(tool, json) -> return! callTool conversation patient tool json
  }

/// The whole run against a daemon that answers on `port`.
let run (port: int) (credentials: Credentials) (args: CallArgs) : Task<Outcome> =
  task {
    use quick = clientFor credentials Timeouts.callHandshakeRequest
    use patient = clientFor credentials Timeouts.workerHttpRequest
    let conversation = Conversation(port)
    let! outcome = converse conversation quick patient args
    match conversation.SessionId with
    | Some sessionId -> do! terminateSession quick port sessionId
    | None -> ()
    return outcome
  }

/// A text to a stream, ending in one newline whatever the text ended in.
let write (writer: TextWriter) (text: string) : unit =
  match text with
  | "" -> ()
  | _ when text.EndsWith '\n' -> writer.Write text
  | _ -> writer.WriteLine text

let emit (out: TextWriter) (err: TextWriter) (emission: Emission) : int =
  write out emission.Stdout
  write err emission.Stderr
  emission.ExitCode

/// `sagefs call <words>`, returning the exit code. `presenceOf` and `homePort` are what the other commands read
/// (`sagefs status` and the port rule), so a daemon that is not there is answered in the words `sagefs status` uses.
let execute
  (presenceOf: int -> DaemonPresence)
  (homePort: unit -> int)
  (getEnv: string -> string)
  (words: string list)
  (out: TextWriter)
  (err: TextWriter)
  : Task<int> =
  task {
    match CallArgs.parse words with
    | Result.Error refusal -> return emit out err (Emission.ofOutcome Credentials.anonymous (Outcome.UsageRefused refusal))
    | Result.Ok Invocation.Help ->
      write out usage
      return 0
    | Result.Ok(Invocation.Run args) ->
      match Credentials.resolve args.Token getEnv with
      | Result.Error refusal -> return emit out err (Emission.ofOutcome Credentials.anonymous (Outcome.UsageRefused refusal))
      | Result.Ok credentials ->
        let port =
          match args.Port with
          | PortChoice.Configured -> homePort ()
          | PortChoice.Named named -> named
        match presenceOf port with
        | DaemonPresence.Running _ ->
          let! outcome = run port credentials args
          return emit out err (Emission.ofOutcome credentials outcome)
        | absent ->
          let outcome = Outcome.NotRunning(DaemonPresence.unreachableLines port absent)
          return emit out err (Emission.ofOutcome credentials outcome)
  }
