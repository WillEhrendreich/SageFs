/// Unit tests for `Affordances.authorityOfMember`/`cohortTools`/
/// `checkCohortToolAllowed`/`CohortTool` (Phase 1 item 11,
/// sagefs-multiagent-vision.md §8.1; cohort-integration-plan.md Slice 3)
/// against hand-built `Cohort.CohortFrame<MemberTable.MemberId>` values — no
/// daemon, no `CohortOwner`, no ledger. The live-daemon wiring (frame
/// resolution through a real owner, the gate in `Mcp.fs`) is covered by
/// `CohortMcpToolsIntegrationTests.fs`; this file is the fast, no-process
/// unit layer for the pure `Affordances.fs` functions themselves.
module SageFs.Tests.CohortAffordancesTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.MemberTable

let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"
let private carol = MemberId.Minted "carol"

/// A frame with the given conductor/members and everything else empty.
/// `authorityOfMember`/`cohortTools` never read claims or test data, so
/// those arrays are irrelevant to every test in this file.
let private frameOf
    (conductor: MemberId option)
    (members: (MemberId * JoinableRole * SeatState) list)
    : CohortFrame<MemberId> =
  { Version = 0L<Measures.ledgerSeq>
    SessionGens = [||]
    Dirty = FrameRegions.NoRegions
    Conductor = conductor
    MemberIds = members |> List.map (fun (m, _, _) -> m) |> List.toArray
    MemberRole = members |> List.map (fun (_, r, _) -> r) |> List.toArray
    MemberSeat = members |> List.map (fun (_, _, s) -> s) |> List.toArray
    ClaimIds = [||]
    ClaimScope = [||]
    ClaimHolderIndex = [||]
    ClaimFence = [||]
    ClaimState = [||]
    TestIds = [||]
    Pass = [||]
    Fail = [||]
    Stale = [||]
    IntegrationHead = Cohort.nullSha
    LandingIds = [||]
    LandingRequesterIndex = [||]
    LandingStatement = [||]
    LandingCommits = [||]
    LandingState = [||]
    LandingQueuePosition = [||] }

[<Tests>]
let cohortAffordancesTests =
  testList "Cohort affordances (Phase 1 item 11, §8.1, Slice 3)" [

    testList "authorityOfMember" [

      test "resolves the frame's Conductor binding to Authority.Conductor" {
        let frame = frameOf (Some alice) [ (alice, JoinableRole.Implementer, SeatState.Present) ]
        Affordances.authorityOfMember alice frame
        |> Expect.equal "conductor resolves to Authority.Conductor" (Authority.Conductor alice)
      }

      test "resolves a Present non-conductor member to Authority.Member with its recorded role" {
        let frame =
          frameOf (Some alice)
            [ (alice, JoinableRole.Implementer, SeatState.Present)
              (bob, JoinableRole.Verifier, SeatState.Present) ]
        Affordances.authorityOfMember bob frame
        |> Expect.equal "present member resolves to Authority.Member with its recorded role" (Authority.Member(bob, JoinableRole.Verifier))
      }

      test "resolves a Departed member to Authority.Anonymous" {
        let frame =
          frameOf (Some alice)
            [ (alice, JoinableRole.Implementer, SeatState.Present)
              (bob, JoinableRole.Verifier, SeatState.Departed System.DateTime.UtcNow) ]
        Affordances.authorityOfMember bob frame
        |> Expect.equal "a departed member is not Present, so it resolves to Anonymous" Authority.Anonymous
      }

      test "resolves a caller absent from the frame to Authority.Anonymous, never an error" {
        let frame = frameOf (Some alice) [ (alice, JoinableRole.Implementer, SeatState.Present) ]
        Affordances.authorityOfMember carol frame
        |> Expect.equal "a caller who never joined resolves to Anonymous" Authority.Anonymous
      }

      test "an empty cohort (no conductor bound yet, no members) resolves everyone to Anonymous" {
        let frame = frameOf None []
        Affordances.authorityOfMember alice frame
        |> Expect.equal "no Conductor binding and no members — Anonymous" Authority.Anonymous
      }
    ]

    testList "cohortTools / checkCohortToolAllowed" [

      test "the Conductor may call every declared cohort tool" {
        Affordances.cohortTools (Authority.Conductor alice)
        |> Expect.equal "the Conductor's tool set is every declared CohortTool" (Set.ofList Affordances.CohortTool.all)
      }

      test "an Implementer may claim/release/request-landing/leave but not reassign" {
        let tools = Affordances.cohortTools (Authority.Member(alice, JoinableRole.Implementer))
        tools |> Set.contains Affordances.CohortTool.AcquireClaim |> Expect.isTrue "Implementer can acquire a claim"
        tools |> Set.contains Affordances.CohortTool.ReleaseClaim |> Expect.isTrue "Implementer can release a claim"
        tools |> Set.contains Affordances.CohortTool.RequestLanding |> Expect.isTrue "Implementer can request a landing"
        tools |> Set.contains Affordances.CohortTool.Leave |> Expect.isTrue "Implementer can leave the cohort"
        tools |> Set.contains Affordances.CohortTool.ReassignClaim |> Expect.isFalse "reassign is conductor-only"
      }

      test "Observer and Verifier are status-read-only from cohortTools itself" {
        [ JoinableRole.Observer; JoinableRole.Verifier ]
        |> List.iter (fun role ->
          Affordances.cohortTools (Authority.Member(alice, role))
          |> Expect.equal (sprintf "%A sees only get_cohort_status from cohortTools" role) (set [ Affordances.CohortTool.GetStatus ]))
      }

      test "join_cohort and get_cohort_status are reachable to every authority via checkCohortToolAllowed" {
        [ Authority.Anonymous
          Authority.Member(alice, JoinableRole.Observer)
          Authority.Member(alice, JoinableRole.Verifier)
          Authority.Member(alice, JoinableRole.Implementer)
          Authority.Conductor alice ]
        |> List.iter (fun authority ->
          Affordances.checkCohortToolAllowed authority Affordances.CohortTool.Join
          |> Expect.isTrue (sprintf "join_cohort must stay reachable for %A" authority)
          Affordances.checkCohortToolAllowed authority Affordances.CohortTool.GetStatus
          |> Expect.isTrue (sprintf "get_cohort_status must stay reachable for %A" authority))
      }

      test "only the Conductor may reassign a claim" {
        [ Authority.Anonymous
          Authority.Member(alice, JoinableRole.Observer)
          Authority.Member(alice, JoinableRole.Verifier)
          Authority.Member(alice, JoinableRole.Implementer) ]
        |> List.iter (fun authority ->
          Affordances.checkCohortToolAllowed authority Affordances.CohortTool.ReassignClaim
          |> Expect.isFalse (sprintf "%A may not reassign a claim" authority))
        Affordances.checkCohortToolAllowed (Authority.Conductor alice) Affordances.CohortTool.ReassignClaim
        |> Expect.isTrue "the Conductor may reassign a claim"
      }
    ]

    testList "CohortTool.toToolName" [
      test "names exactly the 8 cohort MCP tool names — no more, no fewer" {
        let expected =
          set [ "join_cohort"; "leave_cohort"; "acquire_claim"; "release_claim"
                "reassign_claim"; "request_landing"; "get_cohort_status"; "set_integration_ref" ]
        Affordances.CohortTool.all
        |> List.map Affordances.CohortTool.toToolName
        |> Set.ofList
        |> Expect.equal "toToolName's image is exactly the cohort tool-name set the MCP server registers" expected
      }
    ]

    testList "set_integration_ref authority (item 14c)" [
      test "only the Conductor may call set_integration_ref" {
        [ Authority.Anonymous
          Authority.Member(alice, JoinableRole.Observer)
          Authority.Member(alice, JoinableRole.Verifier)
          Authority.Member(alice, JoinableRole.Implementer) ]
        |> List.iter (fun authority ->
          Affordances.checkCohortToolAllowed authority Affordances.CohortTool.SetIntegrationRef
          |> Expect.isFalse (sprintf "%A may not call set_integration_ref" authority))
        Affordances.checkCohortToolAllowed (Authority.Conductor alice) Affordances.CohortTool.SetIntegrationRef
        |> Expect.isTrue "the Conductor may call set_integration_ref"
      }
    ]
  ]
