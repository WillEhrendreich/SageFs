/// One daemon, one owner PER SCOPE — so a repository gets its own cohort.
///
/// The requirement this pins: an agent in repository A and an agent in repository B
/// must each get a cohort, with a SEPARATE conductor seat. Before this, the daemon
/// started ONE owner bound to the scope it was launched from, so the second
/// repository's `join_cohort` computed a correct scope and was refused as a scope
/// collision — against a cohort the caller never wanted.
///
/// The negative control that matters: a second member in the SAME repository must be
/// refused as a conflict, because that contention is real. A registry that gave
/// everyone their own cohort would pass the first half of this and break the second.

module SageFs.Tests.CohortOwnersTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger
open SageFs.Features.CohortOwner

let private scopeOfPath (p: string) = SageFs.Scope.ofWorkingDirectory SageFs.Scope.defaultStrategy p

/// A silent logger. An owner logs through this on construction, so `null` is not an option
/// — every owner dereferences it, which is what all nine cases reported when they shared one
/// "Object reference not set" error.
let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

/// An in-memory ledger, and the deps every owner is started from.
let private harness () =
  let ledger = CohortLedger.InMemory.create ()
  let deps : CohortOwners.Deps =
    { Ledger = ledger
      Clock = fun () -> DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc)
      Entropy = CohortOwner.productionEntropy
      GetSessionTestOutcomes = fun _ -> ([], [], [], 0L)
      Performer = CohortOwner.LandingPerformer.stub
      Logger = silentLogger }
  let owners = CohortOwners.create (CohortOwners.factoryOf deps)
  owners, ledger

/// `Conductor` is a BINDING the reducer makes, never a role a caller asserts — the first
/// member to join a cohort becomes its conductor, which is exactly what these cases check.
let private joinAgent (owners: CohortOwners.Owners) (scope: CohortScope) (agent: string) =
  let who = MemberId.Minted agent
  let owner = owners.OwnerFor scope
  owner.Commit(CohortCommand.Join(who, JoinableRole.Implementer, None, scope)).Result

/// Did this commit bind a conductor? The shape the seat exists to have.
let private boundConductor (result: Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>>) =
  match result with
  | Ok(events, _) -> events |> List.exists (function CohortEvent.ConductorBound _ -> true | _ -> false)
  | Error _ -> false

/// The member name a ledger entry's command carries, so a test can ask whose it is
/// without knowing every command case.
let private commandAgents (e: LedgerEntry<MemberId>) : string list =
  match e.Command with
  | CohortCommand.Join(MemberId.Minted n, _, _, _) -> [ n ]
  | _ -> []

[<Tests>]
let tests =
  testList "one cohort owner per scope" [
    testCase "two repositories get two owners, not one shared one" <| fun _ ->
      let owners, _ = harness ()
      let sageFs = scopeOfPath "/tmp/probe/SageFs"
      let nehemiah = scopeOfPath "/tmp/probe/Nehemiah"

      let a = owners.OwnerFor sageFs
      let b = owners.OwnerFor nehemiah
      Object.ReferenceEquals(a, b)
      |> Expect.isFalse "a repository's owner is not another repository's owner"

    testCase "two repositories get TWO CONDUCTOR SEATS — the reason this exists" <| fun _ ->
      let owners, _ = harness ()
      let sageFs = scopeOfPath "/tmp/probe/SageFs"
      let nehemiah = scopeOfPath "/tmp/probe/Nehemiah"

      joinAgent owners sageFs "agent-a"
      |> boundConductor
      |> Expect.isTrue "the first repository binds a conductor"

      // THE CASE THAT WAS BROKEN: a different repository must get its own conductor, not
      // a refusal as a scope collision against a cohort it never asked for.
      let second = joinAgent owners nehemiah "agent-b"
      match second with
      | Error e -> failtestf "a second repository must not be refused: %A" e
      | Ok _ -> ()

      second
      |> boundConductor
      |> Expect.isTrue "the SECOND repository also gets a conductor seat, which is the whole point"

    testCase "NEGATIVE CONTROL — two agents in ONE repository still contend for the conductor seat" <| fun _ ->
      // The registry must not become a way to mint a cohort per caller: contention inside a
      // repository is real, and a change that silenced it would pass the test above.
      let owners, _ = harness ()
      let sageFs = scopeOfPath "/tmp/probe/SageFs"

      joinAgent owners sageFs "agent-a"
      |> boundConductor
      |> Expect.isTrue "the first member is the conductor"

      // Exactly ONE conductor seat exists in this cohort, so the second member must be refused
      // rather than becoming a second conductor.
      owners.OwnerFor sageFs |> ignore
      let second = joinAgent owners sageFs "agent-b"
      second
      |> boundConductor
      |> Expect.isFalse "a second member must NOT also become conductor in the same repository"

    testCase "one owner per scope, reused — the ledger is scope-keyed so this is safe" <| fun _ ->
      let owners, _ = harness ()
      let scope = scopeOfPath "/tmp/probe/SageFs"
      let first = owners.OwnerFor scope
      Object.ReferenceEquals(first, owners.OwnerFor scope)
      |> Expect.isTrue "the same scope gets the same owner, not a second one"

    testCase "Scopes and TryFind report what exists WITHOUT minting an owner for what does not" <| fun _ ->
      let owners, _ = harness ()
      let sageFs = scopeOfPath "/tmp/probe/SageFs"
      let nehemiah = scopeOfPath "/tmp/probe/Nehemiah"

      owners.Scopes () |> Expect.isEmpty "nothing exists yet, and asking must not create anything"
      owners.TryFind sageFs |> Expect.isNone "TryFind does not create"

      owners.OwnerFor sageFs |> ignore
      owners.Scopes () |> Expect.equal "exactly the scope asked for" [ sageFs ]
      owners.TryFind nehemiah |> Expect.isNone "a different scope still has no owner"

    testCase "two owners over ONE ledger keep their cohorts apart" <| fun _ ->
      // Each owner reads only its own scope's rows (`ledger.ReadAll scope`), so sharing a
      // scope-keyed store must not blend them. If it did, the second join would see the
      // first repository's members.
      let owners, ledger = harness ()
      let sageFs = scopeOfPath "/tmp/probe/SageFs"
      let nehemiah = scopeOfPath "/tmp/probe/Nehemiah"

      joinAgent owners sageFs "agent-a" |> ignore
      joinAgent owners nehemiah "agent-b" |> ignore

      let held scope who = ledger.ReadAll scope |> List.exists (fun e -> List.contains who (commandAgents e))

      held sageFs "agent-a" |> Expect.isTrue "the first repository's ledger holds ITS member"
      held nehemiah "agent-b" |> Expect.isTrue "the second repository's ledger holds ITS OWN member"
      held nehemiah "agent-a" |> Expect.isFalse "and NOT the first's member — one store, two cohorts, kept apart"
  ]

/// The registry must be able to let a scope go, or a daemon that has seen many
/// repositories keeps a mailbox per repository forever.
[<Tests>]
let lifetimeTests =
  testList "one cohort owner per scope — lifetime" [
    testCase "Evict forgets the scope, and reports it actually reclaimed one" <| fun _ ->
      let owners, _ = harness ()
      let scope = scopeOfPath "/tmp/probe/Transient"
      owners.OwnerFor scope |> ignore

      owners.Evict scope |> Expect.isTrue "there was an owner, so evicting it reclaimed something"
      owners.Evict scope |> Expect.isFalse "a second evict has nothing to reclaim, and must not claim it did"
      owners.TryFind scope |> Expect.isNone "the scope is gone"
      owners.Scopes () |> Expect.isEmpty "and it is gone from the reported scopes too"

    testCase "a scope is REBUILT after eviction, and rebuilds from the LEDGER not from nothing" <| fun _ ->
      // Evicting must not lose history: the fresh owner replays the scope's rows, which is
      // why the ledger is shared rather than owned. A registry that started from an empty
      // state here would silently reset a cohort's `seq`, and every fence a member still
      // holds would then be meaningless.
      let owners, ledger = harness ()
      let scope = scopeOfPath "/tmp/probe/Transient"

      joinAgent owners scope "agent-a" |> boundConductor |> Expect.isTrue "a conductor joins before eviction"
      let before = ledger.ReadAll scope |> List.length

      owners.Evict scope |> ignore
      let rebuilt = owners.OwnerFor scope
      let state = rebuilt.ReadCohortState()

      state.Scope |> Expect.equal "the rebuilt owner is still about this scope" scope
      // `Conductor` is a DU, not an option: `NeverBound` means the seat has never been
      // filled, and anything else means it holds a member. Matching it says which.
      match state.Conductor with
      | SageFs.Cohort.ConductorBinding.NeverBound ->
        failtest "the rebuilt owner has no conductor — it did NOT recover the seat from the ledger"
      | _ -> ()
      ledger.ReadAll scope |> List.length |> Expect.equal "with the ledger unchanged" before

    testCase "DisposeAll releases EVERY scope, and leaves none behind" <| fun _ ->
      let owners, _ = harness ()
      let sageFs = scopeOfPath "/tmp/probe/SageFs"
      let nehemiah = scopeOfPath "/tmp/probe/Nehemiah"
      owners.OwnerFor sageFs |> ignore
      owners.OwnerFor nehemiah |> ignore
      owners.Scopes () |> Expect.equal "two scopes are live" [ nehemiah; sageFs ]

      owners.DisposeAll ()
      owners.Scopes () |> Expect.isEmpty "shutdown leaves no scope holding a mailbox"
  ]