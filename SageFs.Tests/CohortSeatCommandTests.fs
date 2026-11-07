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
      |> Expect.equal "read" (Ok({ WorkingDirectory = "/repo"; Member = "mcp:m-00000000000000aa" }: CohortSeatRoute.SeatRequest))
      CohortSeatRoute.parseRequest """{"member": "x"}"""
      |> Expect.equal "no directory is the daemon's own cohort" (Ok({ WorkingDirectory = ""; Member = "x" }: CohortSeatRoute.SeatRequest))
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

    test "no tool path builds SeatConductor: the MCP tool files never name it" {
      for file in [ "SageFs/McpCohortTools.fs"; "SageFs/McpTools.fs"; "SageFs/McpCohortIntegration.fs"; "SageFs/Mcp.fs" ] do
        let text = File.ReadAllText(RepoPaths.repoPathFull [| file |])
        text.Contains "SeatConductor"
        |> Expect.isFalse (sprintf "%s must not build SeatConductor: only the operator route does, so an agent cannot take the seat" file)
    }
  ]
