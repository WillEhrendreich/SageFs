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
/// Both need the operator credential (`operator.token`, the one `sagefs member mint` uses), which no member token
/// holds. The dashboard's Seat button (`seatFromDashboard`) builds the command through the same `commandFor`. This module holds
/// what is decided without a server: how the request is read, which member the name means, what a refusal says
/// and what the reply holds. McpServer.fs registers the route and runs it.
module CohortSeatRoute =

  [<Literal>]
  let Path = "/api/cohort/seat"

  /// What `POST` asks for: the member to seat, named as `get_cohort_status` prints it, in the cohort of a checkout.
  /// `Replace` is the holder the person replaces, named the same way: empty is a plain seating of an empty seat,
  /// a name is a replacement of that holder (refused when it is no longer the holder).
  type SeatRequest = { WorkingDirectory: string; Member: string; Replace: string }

  let parseRequest (body: string) : Result<SeatRequest, SageFsError> =
    try
      use document = System.Text.Json.JsonDocument.Parse body
      let root = document.RootElement
      match root.ValueKind, McpMintRoute.textField root "member" with
      | System.Text.Json.JsonValueKind.Object, name when not (String.IsNullOrWhiteSpace name) ->
        Ok
          { WorkingDirectory = McpMintRoute.textField root "working_directory"
            Member = name.Trim()
            Replace = (McpMintRoute.textField root "replace").Trim() }
      | System.Text.Json.JsonValueKind.Object, _ ->
        Error(SageFsError.JsonParseError("request body", "'member' is required: a member id as get_cohort_status prints it, such as mcp:m-0123456789abcdef."))
      | _ -> Error(SageFsError.JsonParseError("request body", "expected a JSON object."))
    with :? System.Text.Json.JsonException as ex -> Error(SageFsError.JsonParseError("request body", ex.Message))

  /// One present member of the cohort.
  type SeatMember = { Id: string; Role: string }

  /// What the route answers. `Seated`, `Ended` and `Replaced` are empty for a look and filled by a seating or a
  /// replacement. `Silent` is empty unless the seat is bound to a holder that has gone silent, and then says for how
  /// long and when its lease lapses.
  type SeatReply =
    { Scope: string
      Seat: string
      SeatText: string
      Holder: string
      Present: SeatMember list
      Silent: string
      Seated: string
      Ended: string
      Replaced: string }

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
  let viewWithSilence (silence: Features.CohortStatusText.HolderSilence) (now: DateTime) (frame: Cohort.CohortFrame<MemberTable.MemberId>) : SeatReply =
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
      Silent = Features.CohortStatusText.HolderSilence.text now silence
      Seated = ""
      Ended = ""
      Replaced = "" }

  /// The seat as it stands, with no holder called silent (a frame cannot say: it carries no lease).
  let viewOf (frame: Cohort.CohortFrame<MemberTable.MemberId>) : SeatReply =
    viewWithSilence Features.CohortStatusText.HolderSilence.NotSilent DateTime.UtcNow frame

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

  /// The command for a REPLACEMENT: `holderName` loses the seat to `targetName`. The target is refused when it acts
  /// under a minted token, for the reason a seating is. A holder under a token is fine: replacing one whose connection
  /// is gone is the point, and a holder the cohort does not know stays a name that `Cohort.decide` refuses as stale,
  /// with the seat as it stands. Only the operator route builds this command; no tool and no token reaches it.
  let commandForReplace
    (frame: Cohort.CohortFrame<MemberTable.MemberId>)
    (scope: CohortScope)
    (targetName: string)
    (holderName: string)
    : Result<Cohort.CohortCommand<MemberTable.MemberId>, SageFsError> =
    match resolveTarget frame targetName with
    | MemberTable.MemberId.Capability _ as target -> Error(CohortErrorMapping.seatTargetHoldsToken target)
    | MemberTable.MemberId.Mcp _
    | MemberTable.MemberId.Minted _
    | MemberTable.MemberId.Operator
    | MemberTable.MemberId.Browser _ as target -> Ok(Cohort.CohortCommand.ReplaceConductor(resolveTarget frame holderName, target, scope))

  /// A former holder's lost seat in words: who, and when.
  let private replacedText (ended: Cohort.ConductorBinding<MemberTable.MemberId>) : string * string =
    match ended with
    | Cohort.ConductorBinding.Vacant(former, since, _) ->
      MemberTable.MemberId.display former, sprintf "%s was replaced at %s" (MemberTable.MemberId.display former) (since.ToString "u")
    | Cohort.ConductorBinding.Bound former -> MemberTable.MemberId.display former, sprintf "%s was replaced" (MemberTable.MemberId.display former)
    | Cohort.ConductorBinding.NeverBound -> "", ""

  /// The reply for a seating or a replacement that committed: the frame after, plus who was seated and what ended (a
  /// vacancy, or the former holder's seat).
  let seatedReply (frame: Cohort.CohortFrame<MemberTable.MemberId>) (events: Cohort.CohortEvent<MemberTable.MemberId> list) : SeatReply =
    let seated, ended, replaced =
      events
      |> List.tryPick (function
        | Cohort.CohortEvent.ConductorSeated(who, ended) -> Some(MemberTable.MemberId.display who, bindingText ended, "")
        | Cohort.CohortEvent.ConductorReplaced(who, ended) ->
          let former, text = replacedText ended
          Some(MemberTable.MemberId.display who, text, former)
        | _ -> None)
      |> Option.defaultValue ("", "", "")
    { viewOf frame with Seated = seated; Ended = ended; Replaced = replaced }

  /// What a refusal says. `ConductorSeatTaken` is the one a person meets over a seat whose connection is gone, so it
  /// says who holds the seat, whether that holder is silent and for how long, when its lease lapses, and prints the
  /// exact command to run for the member the person named. `state` and `now` are read at the refusal: the same cohort
  /// the command was refused by. Every other refusal keeps the mapping tools and the CLI share.
  let refusalFor
    (state: Cohort.CohortState<MemberTable.MemberId>)
    (now: DateTime)
    (request: SeatRequest)
    (refusal: Cohort.CohortError<MemberTable.MemberId>)
    : SageFsError =
    match refusal with
    | Cohort.CohortError.ConductorSeatTaken sitting ->
      let holder = MemberTable.MemberId.display sitting
      let silence = Features.CohortStatusText.HolderSilence.ofState state now
      let standing =
        match silence with
        | Features.CohortStatusText.HolderSilence.NotSilent -> "is not silent (it renewed its lease lately)"
        | Features.CohortStatusText.HolderSilence.SilentSince _ -> sprintf "has been %s" (Features.CohortStatusText.HolderSilence.text now silence)
      let directory = if String.IsNullOrWhiteSpace request.WorkingDirectory then "" else sprintf " --dir %s" request.WorkingDirectory
      let command = Features.CohortStatusText.replaceCommandText request.Member holder + directory
      SageFsError.CohortActionFailed(
        sprintf "%s holds the conductor seat and %s. A person fills an EMPTY seat, and a holder whose connection is gone is replaced by name." holder standing,
        sprintf "If %s's connection is gone, run: %s. Otherwise ask the conductor to hand the seat on with delegate_conductor." holder command
      )
    | other -> CohortErrorMapping.toSageFsError other

  /// A refusal in the body the editors and `sagefs member mint` already parse.
  let edgeBodyOf (err: SageFsError) : McpMintRoute.EdgeBody =
    match err with
    | SageFsError.CohortActionFailed(reason, suggestion) ->
      { Error = "cohort_refused"; Message = reason; Case = "cohort_refused"; SuggestedAction = suggestion }
    | other -> { Error = "seat_failed"; Message = SageFsError.describe other; Case = "seat_failed"; SuggestedAction = "" }

  /// The dashboard's Seat button: the cohort the panel shows, one member named as `get_cohort_status` prints it. The same
  /// rules as the command line (`commandFor`, then `Cohort.decide`), answered in a sentence for the person on the page.
  /// The caller has already decided the request came from a person at the page.
  let seatFromDashboard (owner: Features.CohortOwner.Handle) (name: string) : System.Threading.Tasks.Task<Result<string, string>> =
    task {
      let frame = owner.ReadFrame()
      match commandFor frame frame.Scope name with
      | Error err -> return Error(SageFsError.describeForAgent err)
      | Ok command ->
        let! committed = owner.Commit command
        match committed with
        | Ok(events, _) ->
          let reply = seatedReply (owner.ReadFrame()) events
          return Ok(sprintf "%s is the conductor now (ended: %s)." reply.Seated reply.Ended)
        | Error err -> return Error(SageFsError.describeForAgent (CohortErrorMapping.toSageFsError err))
    }

  /// The dashboard's Replace button: the pair's holder loses the seat to its target, as the command line's `--replace` does
  /// (`commandForReplace`, then `Cohort.decide`, which refuses a holder that is no longer the holder with the seat as it
  /// stands). The caller has already decided the request came from a person at the page, with a ticket for this pair.
  let replaceFromDashboard
    (owner: Features.CohortOwner.Handle)
    (pair: SageFs.Server.SeatNonces.ReplacePair)
    : System.Threading.Tasks.Task<Result<string, string>> =
    task {
      let frame = owner.ReadFrame()
      match commandForReplace frame frame.Scope pair.TargetName pair.HolderName with
      | Error err -> return Error(SageFsError.describeForAgent err)
      | Ok command ->
        let! committed = owner.Commit command
        match committed with
        | Ok(events, _) ->
          let reply = seatedReply (owner.ReadFrame()) events
          return Ok(sprintf "%s is the conductor now (%s). %s stays a member, with its claims untouched." reply.Seated reply.Ended reply.Replaced)
        | Error err -> return Error(SageFsError.describeForAgent (CohortErrorMapping.toSageFsError err))
    }

/// `sagefs cohort seat [<member id>] [--dir <checkout>]`: look at a cohort's conductor seat, or fill an empty one.
/// With no member id it lists who is present and where the seat stands. With one it seats that member, which only
/// works while the seat is empty (the conductor left, or went silent). It reads `operator.token` from the data
/// directory, a file the operating-system user can read, and calls `/api/cohort/seat` with it. No MCP tool builds the
/// command without a client's accepted question and no member token can take the seat. A process running as that user,
/// an agent with a shell included, can read the credential and use this command, so it is not a guard against one.
module CohortSeatCommand =

  let usage = "usage: sagefs cohort seat [<member id> [--replace <holder id>]] [--dir <checkout>] [--mcp-port <port>]"

  /// What was asked: look, seat a member in an empty seat, or replace the holder of a bound seat with a member.
  /// `Directory` is the checkout whose cohort it is.
  [<RequireQualifiedAccess>]
  type Ask =
    | Look of directory: string
    | Seat of directory: string * memberId: string
    | Replace of directory: string * memberId: string * holder: string

  [<RequireQualifiedAccess>]
  type UsageRefusal =
    | NotCohortSeat
    | FlagNeedsValue of flag: string
    | UnknownFlag of flag: string
    | MoreThanOneMember of first: string * second: string
    | ReplaceNeedsMember

  module UsageRefusal =
    let describe (refusal: UsageRefusal) : string =
      match refusal with
      | UsageRefusal.NotCohortSeat -> "the only cohort command is 'seat'."
      | UsageRefusal.FlagNeedsValue flag -> sprintf "%s needs a value after it." flag
      | UsageRefusal.UnknownFlag flag -> sprintf "%s is not a flag of 'cohort seat'." flag
      | UsageRefusal.MoreThanOneMember(first, second) -> sprintf "name one member to seat; '%s' and '%s' are two." first second
      | UsageRefusal.ReplaceNeedsMember -> sprintf "%s names the holder to replace, and the member who takes the seat goes before it: %s <member id> %s <holder id>." Features.CohortStatusText.replaceFlagText Features.CohortStatusText.seatCommandText Features.CohortStatusText.replaceFlagText

  /// The arguments after `cohort`, with `defaultDirectory` standing in for a missing `--dir`. Total: a mistake is a
  /// named refusal.
  let parseArgs (defaultDirectory: string) (args: string list) : Result<Ask, UsageRefusal> =
    let rec walk (remaining: string list) (directory: string option) (holder: string option) (members: string list) =
      match remaining with
      | [] -> Ok(directory, holder, List.rev members)
      | "--dir" :: value :: more when not (value.StartsWith("--", StringComparison.Ordinal)) -> walk more (Some value) holder members
      | "--dir" :: _ -> Error(UsageRefusal.FlagNeedsValue "--dir")
      | "--replace" :: value :: more when not (value.StartsWith("--", StringComparison.Ordinal)) -> walk more directory (Some value) members
      | "--replace" :: _ -> Error(UsageRefusal.FlagNeedsValue "--replace")
      | flag :: _ when flag.StartsWith("--", StringComparison.Ordinal) -> Error(UsageRefusal.UnknownFlag flag)
      | name :: more -> walk more directory holder (name :: members)
    match args with
    | "seat" :: rest ->
      match walk rest None None [] with
      | Error refusal -> Error refusal
      | Ok(directory, holder, members) ->
        let dir = Option.defaultValue defaultDirectory directory
        match holder, members with
        | None, [] -> Ok(Ask.Look dir)
        | None, [ one ] -> Ok(Ask.Seat(dir, one))
        | Some named, [ one ] -> Ok(Ask.Replace(dir, one, named))
        | Some _, [] -> Error UsageRefusal.ReplaceNeedsMember
        | _, first :: second :: _ -> Error(UsageRefusal.MoreThanOneMember(first, second))
    | _ -> Error UsageRefusal.NotCohortSeat

  /// What the command prints for a reply.
  let render (reply: CohortSeatRoute.SeatReply) : string =
    let members =
      match reply.Present with
      | [] -> [ "  (nobody is present)" ]
      | present -> present |> List.map (fun m -> sprintf "  %s  %s" m.Id m.Role)
    let seatedLine =
      match reply.Seated, reply.Replaced with
      | "", _ -> []
      | seated, "" -> [ sprintf "seated:  %s is the conductor now (ended: %s)" seated reply.Ended ]
      | seated, replaced ->
        [ sprintf "seated:  %s is the conductor now (%s)" seated reply.Ended
          sprintf "replaced: %s is an ordinary member now: its presence and its claims are untouched" replaced ]
    let seatLine =
      match reply.Silent with
      | "" -> reply.SeatText
      | silent -> sprintf "%s, %s" reply.SeatText silent
    let hint =
      match reply.Seat, reply.Silent with
      | "vacant", _
      | "never_bound", _ -> [ sprintf "seat one of them with: %s <member id>" Features.CohortStatusText.seatCommandText ]
      | "bound", silent when silent <> "" ->
        [ sprintf "its connection is gone; replace it with: %s" (Features.CohortStatusText.replaceCommandText "<member id>" reply.Holder) ]
      | _ -> []
    String.concat
      "\n"
      (seatedLine
       @ [ sprintf "cohort:  %s" reply.Scope; sprintf "seat:    %s" seatLine; "present members:" ]
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

  let private post (url: string) (body: CohortSeatRoute.SeatRequest) : HttpRequestMessage =
    let message = new HttpRequestMessage(HttpMethod.Post, url)
    message.Content <- new StringContent(Json.serialize Json.snakeCase body, Encoding.UTF8, "application/json")
    message

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
          | Ask.Seat(directory, memberId) -> post url { WorkingDirectory = directory; Member = memberId; Replace = "" }
          | Ask.Replace(directory, memberId, holder) -> post url { WorkingDirectory = directory; Member = memberId; Replace = holder }
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
