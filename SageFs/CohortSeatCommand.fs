namespace SageFs

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks

/// Where the conductor seat stands, as a closed set with one to-string function (the wire carries the token).
[<RequireQualifiedAccess>]
type SeatKind =
  | NeverBound
  | Vacant
  | Bound

module SeatKind =
  let token (kind: SeatKind) : string =
    match kind with
    | SeatKind.NeverBound -> "never_bound"
    | SeatKind.Vacant -> "vacant"
    | SeatKind.Bound -> "bound"

/// `GET` and `POST /api/cohort/seat`: a person looks at a cohort's conductor seat and fills it when it is empty.
/// Both need the operator credential (`operator.token`, the one `sagefs member mint` uses), so the route is the
/// one place a `Cohort.SeatConductor` is built and no MCP tool and no member token reaches it. This module holds
/// what is decided without a server: how the request is read, which member the name means, what a refusal says
/// and what the reply holds. McpServer.fs registers the route and runs it.
module CohortSeatRoute =

  [<Literal>]
  let Path = "/api/cohort/seat"

  /// What `POST` asks for: the member to seat, named as `get_cohort_status` prints it, in the cohort of a checkout.
  type SeatRequest = { WorkingDirectory: string; Member: string }

  let parseRequest (body: string) : Result<SeatRequest, SageFsError> =
    try
      use document = System.Text.Json.JsonDocument.Parse body
      let root = document.RootElement
      match root.ValueKind, McpMintRoute.textField root "member" with
      | System.Text.Json.JsonValueKind.Object, name when not (String.IsNullOrWhiteSpace name) ->
        Ok { WorkingDirectory = McpMintRoute.textField root "working_directory"; Member = name.Trim() }
      | System.Text.Json.JsonValueKind.Object, _ ->
        Error(SageFsError.JsonParseError("request body", "'member' is required: a member id as get_cohort_status prints it, such as mcp:m-0123456789abcdef."))
      | _ -> Error(SageFsError.JsonParseError("request body", "expected a JSON object."))
    with :? System.Text.Json.JsonException as ex -> Error(SageFsError.JsonParseError("request body", ex.Message))

  /// One present member of the cohort.
  type SeatMember = { Id: string; Role: string }

  /// What the route answers. `Seated` and `Ended` are empty for a look and filled by a seating.
  type SeatReply =
    { Scope: string
      Seat: string
      SeatText: string
      Holder: string
      Present: SeatMember list
      Seated: string
      Ended: string }

  let private roleToken (role: Cohort.JoinableRole) : string =
    match role with
    | Cohort.JoinableRole.Implementer -> "Implementer"
    | Cohort.JoinableRole.Verifier -> "Verifier"
    | Cohort.JoinableRole.Observer -> "Observer"

  /// A binding in words, for the person reading it.
  let bindingText (binding: Cohort.ConductorBinding<MemberTable.MemberId>) : string =
    match binding with
    | Cohort.ConductorBinding.Bound who -> sprintf "held by %s" (MemberTable.MemberId.display who)
    | Cohort.ConductorBinding.Vacant(former, since, why) ->
      sprintf "vacant since %s (%s held it: %s)" (since.ToString "u") (MemberTable.MemberId.display former) (McpCohortTools.vacancyWhy why)
    | Cohort.ConductorBinding.NeverBound -> "never bound: no member has ever joined this cohort"

  let kindOf (binding: Cohort.ConductorBinding<MemberTable.MemberId>) : SeatKind =
    match binding with
    | Cohort.ConductorBinding.Bound _ -> SeatKind.Bound
    | Cohort.ConductorBinding.Vacant _ -> SeatKind.Vacant
    | Cohort.ConductorBinding.NeverBound -> SeatKind.NeverBound

  /// The seat and the members who could take it, read off the cohort's frame. Departed members are left out: the
  /// seat is never theirs, and a roster that listed them would offer a name the cohort refuses.
  let viewOf (frame: Cohort.CohortFrame<MemberTable.MemberId>) : SeatReply =
    { Scope = Scope.label frame.Scope
      Seat = SeatKind.token (kindOf frame.Conductor)
      SeatText = bindingText frame.Conductor
      Holder =
        match frame.Conductor with
        | Cohort.ConductorBinding.Bound who -> MemberTable.MemberId.display who
        | Cohort.ConductorBinding.Vacant _
        | Cohort.ConductorBinding.NeverBound -> ""
      Present =
        [ for i in 0 .. frame.MemberIds.Length - 1 do
            match frame.MemberSeat.[i] with
            | Cohort.SeatState.Present -> { Id = MemberTable.MemberId.display frame.MemberIds.[i]; Role = roleToken frame.MemberRole.[i] }
            | Cohort.SeatState.Departed _ -> () ]
      Seated = ""
      Ended = "" }

  /// The member the person named, found among the ones the cohort knows by the id `get_cohort_status` prints. A name
  /// the cohort does not know stays a name (`Minted`), and `Cohort.decide` refuses it with the roster of those who
  /// are present, which is the answer that helps.
  let resolveTarget (frame: Cohort.CohortFrame<MemberTable.MemberId>) (name: string) : MemberTable.MemberId =
    frame.MemberIds
    |> Array.tryFind (fun m -> MemberTable.MemberId.display m = name)
    |> Option.defaultValue (MemberTable.MemberId.Minted name)

  /// The command to hand the cohort, or why it will not be built. A member acting under a minted token is refused:
  /// no token may call the conductor's tools, so the seat would sit with someone who cannot use it, and a bound
  /// seat can be moved only by its holder.
  let commandFor (frame: Cohort.CohortFrame<MemberTable.MemberId>) (scope: CohortScope) (name: string) : Result<Cohort.CohortCommand<MemberTable.MemberId>, SageFsError> =
    match resolveTarget frame name with
    | MemberTable.MemberId.Capability _ as target -> Error(CohortErrorMapping.seatTargetHoldsToken target)
    | MemberTable.MemberId.Mcp _
    | MemberTable.MemberId.Minted _
    | MemberTable.MemberId.Operator
    | MemberTable.MemberId.Browser _ as target -> Ok(Cohort.CohortCommand.SeatConductor(target, scope))

  /// The reply for a seating that committed: the frame after, plus who was seated and the vacancy that ended.
  let seatedReply (frame: Cohort.CohortFrame<MemberTable.MemberId>) (events: Cohort.CohortEvent<MemberTable.MemberId> list) : SeatReply =
    let seated, ended =
      events
      |> List.tryPick (function
        | Cohort.CohortEvent.ConductorSeated(who, ended) -> Some(MemberTable.MemberId.display who, bindingText ended)
        | _ -> None)
      |> Option.defaultValue ("", "")
    { viewOf frame with Seated = seated; Ended = ended }

  /// A refusal in the body the editors and `sagefs member mint` already parse.
  let edgeBodyOf (err: SageFsError) : McpMintRoute.EdgeBody =
    match err with
    | SageFsError.CohortActionFailed(reason, suggestion) ->
      { Error = "cohort_refused"; Message = reason; Case = "cohort_refused"; SuggestedAction = suggestion }
    | other -> { Error = "seat_failed"; Message = SageFsError.describe other; Case = "seat_failed"; SuggestedAction = "" }

/// `sagefs cohort seat [<member id>] [--dir <checkout>]`: look at a cohort's conductor seat, or fill an empty one.
/// With no member id it lists who is present and where the seat stands. With one it seats that member, which only
/// works while the seat is empty (the conductor left, or went silent). It reads `operator.token` from the data
/// directory, which only the person who runs the daemon can read, and calls `/api/cohort/seat` with it. The seat is
/// never filled by an MCP tool or by a token: that is why an agent cannot take it.
module CohortSeatCommand =

  let usage = "usage: sagefs cohort seat [<member id>] [--dir <checkout>] [--mcp-port <port>]"

  /// What was asked: look, or seat a member. `Directory` is the checkout whose cohort it is.
  [<RequireQualifiedAccess>]
  type Ask =
    | Look of directory: string
    | Seat of directory: string * memberId: string

  [<RequireQualifiedAccess>]
  type UsageRefusal =
    | NotCohortSeat
    | FlagNeedsValue of flag: string
    | UnknownFlag of flag: string
    | MoreThanOneMember of first: string * second: string

  module UsageRefusal =
    let describe (refusal: UsageRefusal) : string =
      match refusal with
      | UsageRefusal.NotCohortSeat -> "the only cohort command is 'seat'."
      | UsageRefusal.FlagNeedsValue flag -> sprintf "%s needs a value after it." flag
      | UsageRefusal.UnknownFlag flag -> sprintf "%s is not a flag of 'cohort seat'." flag
      | UsageRefusal.MoreThanOneMember(first, second) -> sprintf "name one member to seat; '%s' and '%s' are two." first second

  /// The arguments after `cohort`, with `defaultDirectory` standing in for a missing `--dir`. Total: a mistake is a
  /// named refusal.
  let parseArgs (defaultDirectory: string) (args: string list) : Result<Ask, UsageRefusal> =
    let rec walk (remaining: string list) (directory: string option) (members: string list) =
      match remaining with
      | [] -> Ok(directory, List.rev members)
      | "--dir" :: value :: more when not (value.StartsWith("--", StringComparison.Ordinal)) -> walk more (Some value) members
      | "--dir" :: _ -> Error(UsageRefusal.FlagNeedsValue "--dir")
      | flag :: _ when flag.StartsWith("--", StringComparison.Ordinal) -> Error(UsageRefusal.UnknownFlag flag)
      | name :: more -> walk more directory (name :: members)
    match args with
    | "seat" :: rest ->
      match walk rest None [] with
      | Error refusal -> Error refusal
      | Ok(directory, members) ->
        let dir = Option.defaultValue defaultDirectory directory
        match members with
        | [] -> Ok(Ask.Look dir)
        | [ one ] -> Ok(Ask.Seat(dir, one))
        | first :: second :: _ -> Error(UsageRefusal.MoreThanOneMember(first, second))
    | _ -> Error UsageRefusal.NotCohortSeat

  /// What the command prints for a reply.
  let render (reply: CohortSeatRoute.SeatReply) : string =
    let members =
      match reply.Present with
      | [] -> [ "  (nobody is present)" ]
      | present -> present |> List.map (fun m -> sprintf "  %s  %s" m.Id m.Role)
    let seatedLine =
      match reply.Seated with
      | "" -> []
      | seated -> [ sprintf "seated:  %s is the conductor now (ended: %s)" seated reply.Ended ]
    let hint =
      match reply.Seat with
      | "vacant"
      | "never_bound" -> [ sprintf "seat one of them with: %s <member id>" Features.CohortStatusText.seatCommandText ]
      | _ -> []
    String.concat
      "\n"
      (seatedLine
       @ [ sprintf "cohort:  %s" reply.Scope; sprintf "seat:    %s" reply.SeatText; "present members:" ]
       @ members
       @ hint)

  let parseReply (status: int) (text: string) : Result<CohortSeatRoute.SeatReply, MemberMintCommand.MintFailure> =
    match status with
    | 200 ->
      match Json.deserialize<CohortSeatRoute.SeatReply> Json.camelCase text with
      | Ok reply -> Ok reply
      | Error _ -> Error(MemberMintCommand.MintFailure.UnreadableReply status)
    | _ ->
      match Json.deserialize<McpMintRoute.EdgeBody> Json.camelCase text with
      | Ok body when not (String.IsNullOrWhiteSpace body.Message) -> Error(MemberMintCommand.MintFailure.Refused(status, body.Message, body.SuggestedAction))
      | Ok _
      | Error _ -> Error(MemberMintCommand.MintFailure.UnreadableReply status)

  /// Ask the daemon on `port` for `ask`, with the operator credential from `dataDir`.
  let call (port: int) (dataDir: string) (ask: Ask) : Task<Result<CohortSeatRoute.SeatReply, MemberMintCommand.MintFailure>> =
    task {
      match OperatorStore.readCredential dataDir with
      | Error refusal -> return Error(MemberMintCommand.MintFailure.NoCredential refusal)
      | Ok credential ->
        use client = new HttpClient(Timeout = Timeouts.memberMintRequest)
        let url = sprintf "http://localhost:%d%s" port CohortSeatRoute.Path
        use request =
          match ask with
          | Ask.Look directory -> new HttpRequestMessage(HttpMethod.Get, sprintf "%s?working_directory=%s" url (Uri.EscapeDataString directory))
          | Ask.Seat(directory, memberId) ->
            let message = new HttpRequestMessage(HttpMethod.Post, url)
            message.Content <-
              new StringContent(
                Json.serialize Json.snakeCase ({ WorkingDirectory = directory; Member = memberId }: CohortSeatRoute.SeatRequest),
                Encoding.UTF8,
                "application/json"
              )
            message
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
  let run (port: int) (dataDir: string) (ask: Ask) : Task<int> =
    task {
      let! result = call port dataDir ask
      match result with
      | Ok reply ->
        printfn "%s" (render reply)
        return 0
      | Error failure ->
        eprintfn "sagefs cohort seat: %s" (MemberMintCommand.MintFailure.describe failure)
        return 1
    }
