/// Item 14c of sagefs-multiagent-vision.md: `Cohort.CohortCommand.SetIntegrationHead`
/// is the one additive command this item adds to the pure core — the shell
/// (`SageFs/Mcp.fs`'s `set_integration_ref`) resolves a git ref to a sha and
/// records it here; `decide` never runs git itself. Gated exactly like
/// `ReassignClaim`/`DelegateConductor`: conductor-only, refused with
/// `NotConductor` otherwise. These are fast, pure, no-IO unit tests against
/// `decide` directly — the real end-to-end git wiring is
/// `CohortLandingGitAcceptanceTests.fs`.
module SageFs.Tests.CohortIntegrationHeadTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.MemberTable

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

let private join (who: MemberId) (state: CohortState<MemberId>) =
  match decide epoch [||] state (CohortCommand.Join(who, JoinableRole.Implementer, None, CohortScope.Machine)) with
  | Ok(s, _, _) -> s
  | Error e -> failwithf "unexpected join failure: %A" e

[<Tests>]
let cohortIntegrationHeadTests =
  testList "Cohort.SetIntegrationHead (item 14c)" [

    test "the conductor may set the integration head, recorded verbatim on CohortState" {
      let state = CohortState.empty () |> join alice // alice: first joiner, becomes conductor
      match decide epoch [||] state (CohortCommand.SetIntegrationHead(alice, "deadbeef", CohortScope.Machine)) with
      | Ok(newState, events, effects) ->
        newState.IntegrationHead |> Expect.equal "IntegrationHead is set verbatim" "deadbeef"
        events |> Expect.equal "exactly one IntegrationConfigured event" [ CohortEvent.IntegrationConfigured "deadbeef" ]
        effects |> Expect.equal "SetIntegrationHead performs no shell effects itself" []
      | Error e -> failwithf "expected the conductor's SetIntegrationHead to succeed, got %A" e
    }

    test "a non-conductor member is refused with NotConductor, and the state is unchanged" {
      let state =
        CohortState.empty ()
        |> join alice // conductor
        |> join bob   // plain Implementer member
      match decide epoch [||] state (CohortCommand.SetIntegrationHead(bob, "deadbeef", CohortScope.Machine)) with
      | Error(CohortError.NotConductor who) -> who |> Expect.equal "the refused member is named" bob
      | Error other -> failwithf "expected NotConductor, got %A" other
      | Ok _ -> failwith "a non-conductor member must not be able to set the integration head"
    }

    test "an anonymous (never-joined) caller is refused with NotConductor" {
      let state = CohortState.empty () |> join alice
      match decide epoch [||] state (CohortCommand.SetIntegrationHead(bob, "deadbeef", CohortScope.Machine)) with
      | Error(CohortError.NotConductor who) -> who |> Expect.equal "the refused caller is named" bob
      | other -> failwithf "expected NotConductor, got %A" other
    }

    // A VACANT seat is a DIFFERENT refusal and gets different advice, because it
    // implies a different user action: there is no conductor to ask. Before the
    // typed binding this case was indistinguishable from the two above, and the
    // agent-facing text said "ask the cohort conductor" — with no conductor
    // existing. `CohortVacancyTests.fs` covers the refusal shape itself; this
    // pins it on the tool this file is about.
    test "a present member facing a VACANT conductor seat is refused with ConductorVacant, not NotConductor" {
      let departed =
        CohortState.empty ()
        |> join alice   // conductor
        |> join bob     // plain member, survives
        |> fun s ->
          match decide epoch [||] s (CohortCommand.Depart(alice, CohortScope.Machine)) with
          | Ok(s, _, _) -> s
          | Error e -> failwithf "unexpected departure failure: %A" e
      match decide epoch [||] departed (CohortCommand.SetIntegrationHead(bob, "deadbeef", CohortScope.Machine)) with
      | Error(CohortError.ConductorVacant(former, _, why)) ->
        former |> Expect.equal "the refusal names who vacated the seat" (Some alice)
        why |> Expect.equal "and why the seat emptied" VacancyReason.ConductorLeft
      | other -> failwithf "expected ConductorVacant, got %A" other
    }

    test "setting a new integration head does not disturb an unrelated queued landing" {
      let state = CohortState.empty () |> join alice
      let state1 =
        match decide epoch [||] state (CohortCommand.RequestLanding(alice, [], [ "c1" ], "statement", CohortScope.Machine)) with
        | Ok(s, _, _) -> s
        | Error e -> failwithf "unexpected RequestLanding failure: %A" e
      match decide epoch [||] state1 (CohortCommand.SetIntegrationHead(alice, "newhead", CohortScope.Machine)) with
      | Ok(newState, _, _) ->
        newState.IntegrationHead |> Expect.equal "the head moved" "newhead"
        newState.Queue |> Expect.equal "the queued landing is untouched" state1.Queue
        newState.Landings |> Expect.equal "landings are untouched" state1.Landings
      | Error e -> failwithf "unexpected SetIntegrationHead failure: %A" e
    }
  ]
