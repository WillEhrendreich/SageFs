/// `sagefs cohort seat`: how a person looks at a cohort's conductor seat and fills an empty one. The pure rules of the
/// seat are in `CohortSeatConductorTests`; this pins the edge: how the arguments and the request are read, which
/// member a name means, what the person is told, and that no tool path can build the command.
module SageFs.Tests.CohortSeatCommandTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine

let private ada = MemberId.Mcp "m-00000000000000aa"
let private grace = MemberId.Mcp "m-00000000000000bb"
let private token = MemberId.Capability "a49a4ba60edd0244"

let private join who role = CohortCommand.Join(who, role, None, machine)

let private step (state: CohortState<MemberId>) (command: CohortCommand<MemberId>) =
  match decide epoch [| 1uy |] state command with
  | Ok(next, _, _) -> next
  | Error refusal -> failwithf "expected %A to succeed, got %A" command refusal

/// The cohort the live defect left behind: a token member joined first and held the seat, ada and grace joined after,
/// and the token member left. The seat is vacant, ada and grace are here.
let private vacantFrame () =
  let state =
    [ join token JoinableRole.Implementer
      join ada JoinableRole.Implementer
      join grace JoinableRole.Verifier
      CohortCommand.Depart(token, machine) ]
    |> List.fold step (CohortState.empty ())
  let head: LedgerHead<MemberId> = { Seq = 4L<ledgerSeq>; State = state }
  project head [||]

[<Tests>]
let seatCommandTests =
  testList "sagefs cohort seat (the edge of SeatConductor)" [

    test "no member id is a look at the directory's cohort, and a member id is a seating" {
      CohortSeatCommand.parseArgs "/repo" [ "seat" ]
      |> Expect.equal "a look at the current directory" (Ok(CohortSeatCommand.Ask.Look "/repo"))
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "mcp:m-00000000000000aa" ]
      |> Expect.equal "a seating in the current directory" (Ok(CohortSeatCommand.Ask.Seat("/repo", "mcp:m-00000000000000aa")))
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "--dir"; "/other"; "mcp:m-00000000000000aa" ]
      |> Expect.equal "--dir wins over the current directory, in either order" (Ok(CohortSeatCommand.Ask.Seat("/other", "mcp:m-00000000000000aa")))
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "mcp:m-00000000000000aa"; "--dir"; "/other" ]
      |> Expect.equal "the member may come before the flag" (Ok(CohortSeatCommand.Ask.Seat("/other", "mcp:m-00000000000000aa")))
    }

    test "a mistake is a named refusal and not a guess" {
      CohortSeatCommand.parseArgs "/repo" [ "mint" ]
      |> Expect.equal "only 'seat' is a cohort command" (Error CohortSeatCommand.UsageRefusal.NotCohortSeat)
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "--dir" ]
      |> Expect.equal "--dir needs a value" (Error(CohortSeatCommand.UsageRefusal.FlagNeedsValue "--dir"))
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "--force" ]
      |> Expect.equal "an unknown flag is refused" (Error(CohortSeatCommand.UsageRefusal.UnknownFlag "--force"))
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "a"; "b" ]
      |> Expect.equal "two members are two answers" (Error(CohortSeatCommand.UsageRefusal.MoreThanOneMember("a", "b")))
    }

    test "the request body names the member, and the checkout is optional" {
      CohortSeatRoute.parseRequest """{"working_directory": "/repo", "member": " mcp:m-00000000000000aa "}"""
      |> Expect.equal "read" (Ok({ WorkingDirectory = "/repo"; Member = "mcp:m-00000000000000aa"; Replace = "" }: CohortSeatRoute.SeatRequest))
      CohortSeatRoute.parseRequest """{"member": "x"}"""
      |> Expect.equal "no directory is the daemon's own cohort" (Ok({ WorkingDirectory = ""; Member = "x"; Replace = "" }: CohortSeatRoute.SeatRequest))
      match CohortSeatRoute.parseRequest """{"working_directory": "/repo"}""" with
      | Error(SageFsError.JsonParseError _) -> ()
      | other -> failwithf "a body with no member is refused, got %A" other
      match CohortSeatRoute.parseRequest "[1]" with
      | Error(SageFsError.JsonParseError _) -> ()
      | other -> failwithf "a body that is not an object is refused, got %A" other
    }

    test "the view lists the present members and leaves the departed out, and says the seat is vacant" {
      let reply = CohortSeatRoute.viewOf (vacantFrame ())
      reply.Seat |> Expect.equal "the seat is vacant" "vacant"
      reply.Holder |> Expect.equal "nobody holds it" ""
      reply.Present
      |> List.map (fun m -> m.Id, m.Role)
      |> Expect.equal "ada and grace are present, with their roles, and the departed token member is not listed"
        [ MemberId.display ada, "Implementer"; MemberId.display grace, "Verifier" ]
      reply.SeatText |> Expect.stringContains "the text names who held it" (MemberId.display token)
      reply.SeatText |> Expect.stringContains "and why it emptied" "left"
    }

    test "a name the cohort knows is the member, a name it does not know is still a name the cohort refuses with the roster" {
      let frame = vacantFrame ()
      CohortSeatRoute.commandFor frame machine (MemberId.display ada)
      |> Expect.equal "a printed id finds the member" (Ok(CohortCommand.SeatConductor(ada, machine)))
      CohortSeatRoute.commandFor frame machine "mcp:m-ffffffffffffffff"
      |> Expect.equal "a stranger becomes a command decide refuses" (Ok(CohortCommand.SeatConductor(MemberId.Minted "mcp:m-ffffffffffffffff", machine)))
    }

    test "a member acting under a minted token is refused with the reason, and the command is never built" {
      let frame =
        { vacantFrame () with
            MemberIds = [| token |]
            MemberRole = [| JoinableRole.Implementer |]
            MemberSeat = [| SeatState.Present |] }
      match CohortSeatRoute.commandFor frame machine (MemberId.display token) with
      | Error(SageFsError.CohortActionFailed(reason, suggestion)) ->
        reason |> Expect.stringContains "the reason names the stranded seat" "stranded"
        suggestion |> Expect.stringContains "the way on names the listing command" "sagefs cohort seat"
      | other -> failwithf "expected a refusal, got %A" other
    }

    test "a seating's reply says who was seated and the vacancy that ended" {
      let frame = vacantFrame ()
      let ended = frame.Conductor
      let reply = CohortSeatRoute.seatedReply frame [ CohortEvent.ConductorSeated(ada, ended) ]
      reply.Seated |> Expect.equal "ada" (MemberId.display ada)
      reply.Ended |> Expect.equal "the vacancy in words" (CohortSeatRoute.bindingText ended)
    }

    test "a refusal from the cohort reaches the person with its reason and its way on" {
      let body = CohortSeatRoute.edgeBodyOf (SageFsError.CohortActionFailed("the seat is taken", "ask the conductor"))
      body.Message |> Expect.equal "the reason" "the seat is taken"
      body.SuggestedAction |> Expect.equal "the way on" "ask the conductor"
    }

    test "the listing prints the seat, the present members, and the command that fills it" {
      let text = CohortSeatCommand.render (CohortSeatRoute.viewOf (vacantFrame ()))
      text |> Expect.stringContains "the seat is named" "vacant since"
      text |> Expect.stringContains "a present member is listed" (MemberId.display ada)
      text |> Expect.stringContains "the command is named" "sagefs cohort seat <member id>"
    }

    test "a held seat prints no command to fill it" {
      let held =
        [ join ada JoinableRole.Implementer ] |> List.fold step (CohortState.empty ())
      let text = CohortSeatCommand.render (CohortSeatRoute.viewOf (project { Seq = 1L<ledgerSeq>; State = held } [||]))
      text |> Expect.stringContains "the holder is named" (sprintf "held by %s" (MemberId.display ada))
      text.Contains "seat one of them with" |> Expect.isFalse "a held seat is not offered for filling"
    }

    test "the status text for a vacant seat names the command, so an agent reading it knows who to ask" {
      let text = Features.CohortStatusText.render (vacantFrame ())
      text |> Expect.stringContains "the command is in the status" Features.CohortStatusText.seatCommandText
    }

    test "no tool path builds SeatConductor or ReplaceConductor: the MCP tool files never name them" {
      for file in [ "SageFs/McpCohortTools.fs"; "SageFs/McpTools.fs"; "SageFs/McpCohortIntegration.fs"; "SageFs/Mcp.fs" ] do
        let text = File.ReadAllText(RepoPaths.repoPathFull [| file |])
        text.Contains "SeatConductor"
        |> Expect.isFalse (sprintf "%s must not build SeatConductor: only the operator route does, so an agent cannot take the seat" file)
        text.Contains "ReplaceConductor"
        |> Expect.isFalse (sprintf "%s must not build ReplaceConductor: only the operator route does, so an agent cannot unseat a conductor" file)
    }

    // ── P15: a person replaces a conductor whose connection is gone ──────────────────────────────────────────────

    test "--replace names the holder: seat <id> --replace <holder> is a replacement, and the flag needs a member and a value" {
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "mcp:m-00000000000000bb"; "--replace"; "mcp:m-00000000000000aa" ]
      |> Expect.equal "a replacement in the current directory" (Ok(CohortSeatCommand.Ask.Replace("/repo", "mcp:m-00000000000000bb", "mcp:m-00000000000000aa")))
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "--replace"; "mcp:m-00000000000000aa"; "--dir"; "/other"; "mcp:m-00000000000000bb" ]
      |> Expect.equal "the flag may come first, and --dir still wins" (Ok(CohortSeatCommand.Ask.Replace("/other", "mcp:m-00000000000000bb", "mcp:m-00000000000000aa")))
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "mcp:m-00000000000000bb"; "--replace" ]
      |> Expect.equal "--replace needs the holder after it" (Error(CohortSeatCommand.UsageRefusal.FlagNeedsValue "--replace"))
      CohortSeatCommand.parseArgs "/repo" [ "seat"; "--replace"; "mcp:m-00000000000000aa" ]
      |> Expect.equal "naming the holder with nobody to put in the seat is a mistake" (Error CohortSeatCommand.UsageRefusal.ReplaceNeedsMember)
    }

    test "the request body carries the holder to replace, and no holder is a plain seating" {
      CohortSeatRoute.parseRequest """{"working_directory": "/repo", "member": "b", "replace": " a "}"""
      |> Expect.equal "read" (Ok({ WorkingDirectory = "/repo"; Member = "b"; Replace = "a" }: CohortSeatRoute.SeatRequest))
    }

    test "a replacement is built for a present member and the holder the person named, and never for a member under a token" {
      let frame = vacantFrame ()
      CohortSeatRoute.commandForReplace frame machine (MemberId.display grace) (MemberId.display ada)
      |> Expect.equal "both printed ids find their members" (Ok(CohortCommand.ReplaceConductor(ada, grace, machine)))
      CohortSeatRoute.commandForReplace frame machine (MemberId.display grace) "mcp:m-ffffffffffffffff"
      |> Expect.equal "a holder the cohort does not know stays a name, and decide refuses it as stale"
        (Ok(CohortCommand.ReplaceConductor(MemberId.Minted "mcp:m-ffffffffffffffff", grace, machine)))
      let withToken =
        { frame with
            MemberIds = [| token; ada |]
            MemberRole = [| JoinableRole.Implementer; JoinableRole.Implementer |]
            MemberSeat = [| SeatState.Present; SeatState.Present |] }
      match CohortSeatRoute.commandForReplace withToken machine (MemberId.display token) (MemberId.display ada) with
      | Error(SageFsError.CohortActionFailed(reason, _)) -> reason |> Expect.stringContains "a token member cannot take the seat" "stranded"
      | other -> failwithf "expected a refusal for a member under a token, got %A" other
      CohortSeatRoute.commandForReplace withToken machine (MemberId.display ada) (MemberId.display token)
      |> Expect.equal "a token member may be the one replaced: that is the point" (Ok(CohortCommand.ReplaceConductor(token, ada, machine)))
    }

    /// Ada holds the seat and grace is present: the state the person finds when ada's bridge is gone and her lease has not lapsed.
    let adaHolds () =
      [ join ada JoinableRole.Implementer; join grace JoinableRole.Implementer ] |> List.fold step (CohortState.empty ())

    let request: CohortSeatRoute.SeatRequest = { WorkingDirectory = "/repo"; Member = MemberId.display grace; Replace = "" }

    test "a seating over a held seat says who holds it, whether it is silent and since when, when its lease lapses, and the exact command" {
      let state = adaHolds ()
      let now = epoch.AddMinutes 12.0
      match CohortSeatRoute.refusalFor state now request (CohortError.ConductorSeatTaken ada) with
      | SageFsError.CohortActionFailed(reason, suggestion) ->
        reason |> Expect.stringContains "names the holder" (MemberId.display ada)
        reason |> Expect.stringContains "says it is silent, and for how long" "silent for 12 min"
        reason |> Expect.stringContains "says when the lease lapses" ((epoch.Add leaseWindow).ToString "u")
        suggestion
        |> Expect.stringContains
          "prints the command to run, with the member the person named, the holder and the directory"
          (sprintf "sagefs cohort seat %s --replace %s --dir /repo" (MemberId.display grace) (MemberId.display ada))
      | other -> failwithf "expected a cohort refusal, got %A" other
    }

    test "a held seat whose holder is not silent says so, so a person does not replace a live conductor by habit" {
      let state = adaHolds ()
      match CohortSeatRoute.refusalFor state (epoch.AddSeconds 30.0) request (CohortError.ConductorSeatTaken ada) with
      | SageFsError.CohortActionFailed(reason, _) ->
        reason |> Expect.stringContains "names the holder" (MemberId.display ada)
        reason.Contains "silent for" |> Expect.isFalse "a holder renewed half a minute ago is not silent"
      | other -> failwithf "expected a cohort refusal, got %A" other
    }

    test "every other refusal keeps its own mapping" {
      let state = adaHolds ()
      CohortSeatRoute.refusalFor state epoch request (CohortError.SeatTargetAbsent(grace, [ ada ]))
      |> Expect.equal "the roster refusal is the mapping's" (CohortErrorMapping.toSageFsError (CohortError.SeatTargetAbsent(grace, [ ada ])))
      match CohortSeatRoute.refusalFor state epoch request (CohortError.ConductorHolderChanged(grace, state.Conductor)) with
      | SageFsError.CohortActionFailed(reason, suggestion) ->
        reason |> Expect.stringContains "a stale holder says who holds the seat now" (MemberId.display ada)
        suggestion |> Expect.stringContains "and the way on is to look again" "sagefs cohort seat"
      | other -> failwithf "expected a cohort refusal, got %A" other
    }

    test "the view of a held seat carries the silence, and the listing prints the exact replace command for a silent holder" {
      let state = adaHolds ()
      let frame = project { Seq = 2L<ledgerSeq>; State = state } [||]
      let now = epoch.AddMinutes 12.0
      let silence = Features.CohortStatusText.HolderSilence.ofState state now
      let reply = CohortSeatRoute.viewWithSilence silence now frame
      reply.Silent |> Expect.stringContains "the reply says it" "silent for 12 min"
      let text = CohortSeatCommand.render reply
      text |> Expect.stringContains "the listing says the holder is silent" "silent for 12 min"
      text |> Expect.stringContains "and prints the command with the holder filled in" (sprintf "--replace %s" (MemberId.display ada))
      let live = CohortSeatCommand.render (CohortSeatRoute.viewWithSilence Features.CohortStatusText.HolderSilence.NotSilent now frame)
      live.Contains "--replace" |> Expect.isFalse "a holder that is not silent is not offered for replacing"
    }

    test "a replacement's reply says who was seated and who was replaced, and the listing prints both" {
      let state = adaHolds ()
      let frame = project { Seq = 2L<ledgerSeq>; State = state } [||]
      let ended = ConductorBinding.Vacant(ada, epoch, VacancyReason.Replaced)
      let reply = CohortSeatRoute.seatedReply frame [ CohortEvent.ConductorReplaced(grace, ended) ]
      reply.Seated |> Expect.equal "grace is seated" (MemberId.display grace)
      reply.Replaced |> Expect.equal "ada was replaced" (MemberId.display ada)
      let text = CohortSeatCommand.render reply
      text |> Expect.stringContains "the listing says who is the conductor now" (sprintf "%s is the conductor now" (MemberId.display grace))
      text |> Expect.stringContains "and who was replaced" (sprintf "%s was replaced" (MemberId.display ada))
    }

    test "the status text for a silent holder says how long, when the lease lapses, and the replace command" {
      let state = adaHolds ()
      let frame = project { Seq = 2L<ledgerSeq>; State = state } [||]
      let silent = Features.CohortStatusText.HolderSilence.SilentSince(epoch, epoch.Add leaseWindow)
      let text = Features.CohortStatusText.renderWithSilence silent frame
      text |> Expect.stringContains "the conductor line names the holder and the silence" (sprintf "Conductor: held by %s, silent for" (MemberId.display ada))
      text |> Expect.stringContains "and the command" (sprintf "a person can replace it: sagefs cohort seat <member id> --replace %s" (MemberId.display ada))
      (Features.CohortStatusText.render frame).Contains "can replace it" |> Expect.isFalse "a holder nobody calls silent is shown as it always was"
    }
  ]
