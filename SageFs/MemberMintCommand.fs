namespace SageFs

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks

/// `sagefs member mint --role R [--scope S] [--ttl MINUTES] (--dir D | --session ID) [--print-env VAR]`: mint a member
/// token for an agent run without an MCP connection, so the token goes from the daemon into an environment variable
/// and never through a model. It reads `operator.token` from the data directory (`SAGEFS_DATA_DIR` respected), which
/// only the person who runs the daemon can read, and calls `POST /api/members/mint` with it.
module MemberMintCommand =

  let usage =
    "usage: sagefs member mint --role <Observer|Analysis|Verifier|Implementer> (--dir <checkout> | --session <id>) [--scope <prefix>] [--ttl <minutes>] [--print-env <VAR>]"

  /// What the command prints for a token that was minted.
  [<RequireQualifiedAccess>]
  type MintOutput =
    /// One `export VAR=token` line a launcher can `eval`, and a summary without the token on stderr.
    | Export of variable: string
    /// The member, its grant and the token, for a person reading it.
    | Details

  /// What was asked, with the request's own defaults: a blank string and a zero are "not given".
  type MintArgs =
    { Request: McpMintRoute.MintRequest
      Output: MintOutput }

  [<RequireQualifiedAccess>]
  type UsageRefusal =
    | NotMemberMint
    | MissingRole
    | NamesNeitherOrBoth
    | FlagNeedsValue of flag: string
    | NotANumber of flag: string * given: string
    | NotAVariableName of given: string
    | UnknownFlag of flag: string

  module UsageRefusal =
    let describe (refusal: UsageRefusal) : string =
      match refusal with
      | UsageRefusal.NotMemberMint -> "the only member command is 'mint'."
      | UsageRefusal.MissingRole -> "--role is required."
      | UsageRefusal.NamesNeitherOrBoth -> "name the checkout the run works in with --dir, or one session with --session, and not both."
      | UsageRefusal.FlagNeedsValue flag -> sprintf "%s needs a value after it." flag
      | UsageRefusal.NotANumber(flag, given) -> sprintf "%s wants a whole number of minutes, and '%s' is not one." flag given
      | UsageRefusal.NotAVariableName given -> sprintf "--print-env wants an environment variable name such as SAGEFS_MEMBER_TOKEN, and '%s' is not one." given
      | UsageRefusal.UnknownFlag flag -> sprintf "%s is not a flag of 'member mint'." flag

  let isVariableName (name: string) : bool =
    not (String.IsNullOrEmpty name)
    && (Char.IsAsciiLetter name.[0] || name.[0] = '_')
    && name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_')

  let valueFlags = [ "--role"; "--scope"; "--ttl"; "--dir"; "--session"; "--print-env" ]

  let rec flagValues (remaining: string list) (found: Map<string, string>) : Result<Map<string, string>, UsageRefusal> =
    match remaining with
    | [] -> Ok found
    | flag :: value :: more when List.contains flag valueFlags && not (value.StartsWith("--", StringComparison.Ordinal)) ->
      flagValues more (Map.add flag value found)
    | flag :: _ when List.contains flag valueFlags -> Error(UsageRefusal.FlagNeedsValue flag)
    | other :: _ -> Error(UsageRefusal.UnknownFlag other)

  let ttlOf (found: Map<string, string>) : Result<int, UsageRefusal> =
    match Map.tryFind "--ttl" found with
    | None -> Ok 0
    | Some text ->
      match Int32.TryParse text with
      | true, minutes -> Ok minutes
      | false, _ -> Error(UsageRefusal.NotANumber("--ttl", text))

  let outputOf (found: Map<string, string>) : Result<MintOutput, UsageRefusal> =
    match Map.tryFind "--print-env" found with
    | None -> Ok MintOutput.Details
    | Some name when isVariableName name -> Ok(MintOutput.Export name)
    | Some name -> Error(UsageRefusal.NotAVariableName name)

  /// The arguments after `member`. Total: a mistake is a named refusal.
  let parseArgs (args: string list) : Result<MintArgs, UsageRefusal> =
    match args with
    | "mint" :: rest ->
      match flagValues rest Map.empty with
      | Error refusal -> Error refusal
      | Ok found ->
        let text flag = Map.tryFind flag found |> Option.defaultValue ""
        match text "--role", text "--dir", text "--session", ttlOf found, outputOf found with
        | "", _, _, _, _ -> Error UsageRefusal.MissingRole
        | _, _, _, Error refusal, _ -> Error refusal
        | _, _, _, _, Error refusal -> Error refusal
        | _, "", "", _, _ -> Error UsageRefusal.NamesNeitherOrBoth
        | _, directory, session, _, _ when directory <> "" && session <> "" -> Error UsageRefusal.NamesNeitherOrBoth
        | role, directory, session, Ok ttl, Ok output ->
          Ok
            { Request = { Role = role; Scope = text "--scope"; TtlMinutes = ttl; WorkingDirectory = directory; SessionId = session }
              Output = output }
    | _ -> Error UsageRefusal.NotMemberMint

  /// Why no token was printed. Each one carries what the person should do next.
  [<RequireQualifiedAccess>]
  type MintFailure =
    | NoCredential of OperatorStore.ReadRefusal
    | DaemonUnreachable of port: int * reason: string
    | Refused of status: int * message: string * suggestedAction: string
    | UnreadableReply of status: int

  module MintFailure =
    let describe (failure: MintFailure) : string =
      match failure with
      | MintFailure.NoCredential refusal -> OperatorStore.ReadRefusal.describe refusal
      | MintFailure.DaemonUnreachable(port, reason) ->
        sprintf "No daemon answered on port %d (%s). Start it with 'sagefs', or pass --mcp-port for the port it uses." port reason
      | MintFailure.Refused(status, message, suggestedAction) -> sprintf "The daemon refused (HTTP %d): %s %s" status message suggestedAction
      | MintFailure.UnreadableReply status -> sprintf "The daemon answered HTTP %d with a body this command cannot read." status

  /// What the command prints on stdout when the mint worked.
  let render (output: MintOutput) (reply: McpMintRoute.MintReply) : string =
    match output with
    | MintOutput.Export name -> sprintf "export %s=%s" name reply.Token
    | MintOutput.Details ->
      String.concat
        "\n"
        [ sprintf "member:  %s" reply.MemberId
          sprintf "role:    %s" reply.Role
          sprintf "route:   %s" reply.Route
          sprintf "scope:   %s" (match reply.Scope with "" -> "the whole repo" | scope -> scope)
          sprintf "expires: %s" reply.ExpiresAt
          sprintf "token:   %s" reply.Token ]

  /// What goes to stderr beside an `export` line, so a launcher's log says who was minted and the token is not in it.
  let summaryLine (reply: McpMintRoute.MintReply) : string =
    sprintf "minted %s as %s on %s, expires %s" reply.MemberId reply.Role reply.Route reply.ExpiresAt

  let parseReply (status: int) (text: string) : Result<McpMintRoute.MintReply, MintFailure> =
    match status with
    | 200 ->
      match Json.deserialize<McpMintRoute.MintReply> Json.camelCase text with
      | Ok reply -> Ok reply
      | Error _ -> Error(MintFailure.UnreadableReply status)
    | _ ->
      match Json.deserialize<McpMintRoute.EdgeBody> Json.camelCase text with
      | Ok body when not (String.IsNullOrWhiteSpace body.Message) -> Error(MintFailure.Refused(status, body.Message, body.SuggestedAction))
      | Ok _
      | Error _ -> Error(MintFailure.UnreadableReply status)

  /// Ask the daemon on `port` to mint, with the operator credential from `dataDir`.
  let mint (port: int) (dataDir: string) (args: MintArgs) : Task<Result<McpMintRoute.MintReply, MintFailure>> =
    task {
      match OperatorStore.readCredential dataDir with
      | Error refusal -> return Error(MintFailure.NoCredential refusal)
      | Ok credential ->
        use client = new HttpClient(Timeout = Timeouts.memberMintRequest)
        use request = new HttpRequestMessage(HttpMethod.Post, sprintf "http://localhost:%d%s" port McpMintRoute.Path)
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", credential)
        request.Content <- new StringContent(Json.serialize Json.snakeCase args.Request, Encoding.UTF8, "application/json")
        try
          use! response = client.SendAsync request
          let! text = response.Content.ReadAsStringAsync()
          return parseReply (int response.StatusCode) text
        with
        | :? HttpRequestException as ex -> return Error(MintFailure.DaemonUnreachable(port, ex.Message))
        | :? TaskCanceledException -> return Error(MintFailure.DaemonUnreachable(port, "no answer in time"))
    }

  /// Run the command and return its exit code. The token is the only secret, and it goes to stdout alone.
  let run (port: int) (dataDir: string) (args: MintArgs) : Task<int> =
    task {
      let! result = mint port dataDir args
      match result with
      | Ok reply ->
        printfn "%s" (render args.Output reply)
        match args.Output with
        | MintOutput.Export _ -> eprintfn "%s" (summaryLine reply)
        | MintOutput.Details -> ()
        return 0
      | Error failure ->
        eprintfn "sagefs member mint: %s" (MintFailure.describe failure)
        return 1
    }
