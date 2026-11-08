namespace SageFs

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks

/// `sagefs data clear [--store friction|cohort|all]`: a person clears the local stores SageFs keeps under its data
/// directory, from a shell. `POST /api/local-data/clear` takes the operator credential (`operator.token`, the one
/// `sagefs member mint` uses) and nothing else, so no tool and no member token reaches it. This command is the way a
/// person reaches it: it reads the credential from the data directory (`SAGEFS_DATA_DIR` respected) and sends it as
/// the Bearer header, so it never appears in a command line.
module LocalDataCommand =

  [<Literal>]
  let Path = "/api/local-data/clear"

  let usage = "usage: sagefs data clear [--store <friction|cohort|all>] [--mcp-port <port>]"

  [<RequireQualifiedAccess>]
  type UsageRefusal =
    | NoVerb
    | NotClear of word: string
    | FlagNeedsValue of flag: string
    | UnknownFlag of flag: string
    | NotAStore of reason: string

  module UsageRefusal =
    let describe (refusal: UsageRefusal) : string =
      match refusal with
      | UsageRefusal.NoVerb -> "name what to do. The only data command is 'clear'."
      | UsageRefusal.NotClear word -> sprintf "'%s' is not a data command. The only one is 'clear'." word
      | UsageRefusal.FlagNeedsValue flag -> sprintf "%s needs a value after it." flag
      | UsageRefusal.UnknownFlag flag -> sprintf "%s is not a flag of 'data clear'." flag
      | UsageRefusal.NotAStore reason -> reason

  /// What was asked: which store, read by the same parser the route and `manage_local_data` use.
  type ClearAsk = { Store: LocalData.ClearTarget; StoreText: string }

  /// The words after `sagefs data`, with `--mcp-port` already taken out (the program reads it).
  let parseArgs (words: string list) : Result<ClearAsk, UsageRefusal> =
    let rec flags (remaining: string list) (store: string) : Result<string, UsageRefusal> =
      match remaining with
      | [] -> Ok store
      | "--store" :: value :: rest when not (value.StartsWith("--", StringComparison.Ordinal)) -> flags rest value
      | "--store" :: _ -> Error(UsageRefusal.FlagNeedsValue "--store")
      | flag :: _ -> Error(UsageRefusal.UnknownFlag flag)
    match words with
    | [] -> Error UsageRefusal.NoVerb
    | "clear" :: rest ->
      flags rest "all"
      |> Result.bind (fun text ->
        match LocalData.ClearTarget.parse text with
        | Ok target -> Ok { Store = target; StoreText = text }
        | Error reason -> Error(UsageRefusal.NotAStore reason))
    | word :: _ -> Error(UsageRefusal.NotClear word)

  /// One store the clear touched, as the route reports it.
  type ClearedStore = { Store: string; Outcome: string }

  type ClearReply = { Success: bool; Results: ClearedStore list }

  /// The reply as text a person reads: one line per store.
  let render (reply: ClearReply) : string =
    reply.Results |> List.map (fun cleared -> cleared.Outcome) |> String.concat "\n"

  /// The body a 400 carries (`{ success: false, error }`).
  type RouteError = { Success: bool; Error: string }

  let parseReply (status: int) (text: string) : Result<ClearReply, MemberMintCommand.MintFailure> =
    match status with
    | 200 ->
      match Json.deserialize<ClearReply> Json.camelCase text with
      | Ok reply -> Ok reply
      | Error _ -> Error(MemberMintCommand.MintFailure.UnreadableReply status)
    | _ ->
      match Json.deserialize<McpMintRoute.EdgeBody> Json.camelCase text with
      | Ok body when not (String.IsNullOrWhiteSpace body.Message) ->
        Error(MemberMintCommand.MintFailure.Refused(status, body.Message, body.SuggestedAction))
      | Ok _
      | Error _ ->
        match Json.deserialize<RouteError> Json.camelCase text with
        | Ok body when not (String.IsNullOrWhiteSpace body.Error) -> Error(MemberMintCommand.MintFailure.Refused(status, body.Error, ""))
        | Ok _
        | Error _ -> Error(MemberMintCommand.MintFailure.UnreadableReply status)

  /// Ask the daemon on `port` to clear, with the operator credential from `dataDir`.
  let clear (port: int) (dataDir: string) (ask: ClearAsk) : Task<Result<ClearReply, MemberMintCommand.MintFailure>> =
    task {
      match OperatorStore.readCredential dataDir with
      | Error refusal -> return Error(MemberMintCommand.MintFailure.NoCredential refusal)
      | Ok credential ->
        use client = new HttpClient(Timeout = Timeouts.memberMintRequest)
        let url = sprintf "http://localhost:%d%s?store=%s" port Path (Uri.EscapeDataString ask.StoreText)
        use request = new HttpRequestMessage(HttpMethod.Post, url)
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", credential)
        try
          use! response = client.SendAsync request
          let! text = response.Content.ReadAsStringAsync()
          return parseReply (int response.StatusCode) text
        with
        | :? HttpRequestException as ex -> return Error(MemberMintCommand.MintFailure.DaemonUnreachable(port, ex.Message))
        | :? TaskCanceledException -> return Error(MemberMintCommand.MintFailure.DaemonUnreachable(port, "no answer in time"))
    }

  /// Run the command and return its exit code.
  let run (port: int) (dataDir: string) (ask: ClearAsk) : Task<int> =
    task {
      let! result = clear port dataDir ask
      match result with
      | Ok reply ->
        printfn "%s" (render reply)
        return 0
      | Error failure ->
        eprintfn "sagefs data clear: %s" (MemberMintCommand.MintFailure.describe failure)
        return 1
    }
