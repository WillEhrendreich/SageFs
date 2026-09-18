/// Roast-6 #7b: before this item, a `FastForward` EFFECT failure (the
/// performer's `Error reason` — an infra error like the branch moving
/// concurrently under a raw git command, or a transient I/O failure) had no
/// completion command `Cohort.decide` could apply — `CohortOwner.fs`'s
/// `dispatchLandingEffects` could only log it (see the git history of that
/// file's `FastForward` case), leaving the landing permanently stranded in
/// `Verifying`. `CohortCommand.FastForwardFailed` closes that: it re-enters
/// `decide`, which either reapplies the existing Property-11 `HeadMoved`
/// guard (the head genuinely moved) or retries the whole
/// rebase -> verify -> fast-forward pipeline from `Rebasing` (it did not).
/// These tests prove the transition directly against `Cohort.decide` (no
/// actor, no IO) and then prove the owner's effect-dispatch loop actually
/// posts `FastForwardFailed` — and the landing progresses to `Landed` on
/// retry — end to end through `CohortOwner.startWithPerformer`.
module SageFs.Tests.CohortFastForwardFailedTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private requester = MemberId.Minted "alice"

/// A `CohortState` with one landing already parked at the front of the
/// queue, in `Verifying(base', rebasedHead, ...)` — exactly the state a
/// `FastForward` effect is dispatched from. Mirrors
/// `CohortPropertyTests.fs`'s property 11 harness (`Verifying` constructed
/// directly, since driving a landing there through `RequestLanding` +
/// `RebaseCompleted` + `AffectedComputed` + `TestsCompleted` would only
/// restate machinery this file isn't testing).
let private stateWithLandingVerifying (integrationHead: string) (base': string) (rebasedHead: string) : CohortState<MemberId> * LandingId =
  let landingId = LandingId "l-0"
  match Purpose.tryCreate "purpose", Statement.tryCreate "land my change" with
  | Ok _, Ok statement ->
    let req : LandingRequest<MemberId> = {
      Id = landingId
      Requester = requester
      Claims = []
      Commits = [ "c1" ]
      BaseAtQueue = base'
      Statement = statement
      State = LandingState.Verifying(base', rebasedHead, 1, 0)
    }
    let state = {
      CohortState.empty () with
        IntegrationHead = integrationHead
        Members = Map.ofList [ requester, { Role = JoinableRole.Implementer; Presence = MemberPresence.Present; LastRenewal = epoch; Session = None } ]
        Landings = Map.ofList [ landingId, req ]
        Queue = [ landingId ]
    }
    state, landingId
  | _ -> failtest "fixture setup: purpose/statement construction failed"

[<Tests>]
let decideTests =
  testList "Cohort.decide FastForwardFailed" [

    testCase "WHY — a FastForward infra failure with an unmoved head retries: the landing re-enters Rebasing against the same base, not stuck in Verifying (roast-6 #7b)" <| fun () ->
      let state, landingId = stateWithLandingVerifying "H0" "H0" "H0-rebased"
      match decide epoch [||] state (CohortCommand.FastForwardFailed(landingId, "git: transient I/O error")) with
      | Ok(newState, events, effects) ->
        (match newState.Landings.[landingId].State with
         | LandingState.Rebasing onto -> onto |> Expect.equal "retries against the SAME base it was verifying against" "H0"
         | other -> failtestf "expected Rebasing, got %A" other)
        events
        |> Expect.equal "a LandingStateChanged event fires" [ CohortEvent.LandingStateChanged(landingId, LandingState.Rebasing "H0") ]
        effects
        |> Expect.equal "a fresh Rebase effect is emitted so the pipeline actually retries" [ CohortEffect.Rebase(landingId, "H0", [ "c1" ]) ]
      | Error err -> failtestf "expected Ok, got %A" err

    testCase "WHY — a FastForward infra failure with a MOVED head reuses the real HeadMoved diagnosis, never mislabeled as a plain retry" <| fun () ->
      let state, landingId = stateWithLandingVerifying "H1" "H0" "H0-rebased"
      match decide epoch [||] state (CohortCommand.FastForwardFailed(landingId, "git: ref moved")) with
      | Ok(newState, events, effects) ->
        let blockedState =
          match newState.Landings.[landingId].State with
          | LandingState.Blocked(LandingBlocker.HeadMoved(from', to'), NextAction.RebaseAndResubmit) as s ->
            from' |> Expect.equal "blocker names the base it verified against" "H0"
            to' |> Expect.equal "blocker names the current (moved) head" "H1"
            s
          | other -> failtestf "expected Blocked(HeadMoved, RebaseAndResubmit), got %A" other
        events
        |> Expect.equal "a LandingStateChanged event fires with the same blocked state" [ CohortEvent.LandingStateChanged(landingId, blockedState) ]
        effects |> Expect.equal "a blocked landing emits no new effect" []
      | Error err -> failtestf "expected Ok, got %A" err

    testCase "an unknown landing id is refused" <| fun () ->
      let state, _ = stateWithLandingVerifying "H0" "H0" "H0-rebased"
      match decide epoch [||] state (CohortCommand.FastForwardFailed(LandingId "nope", "boom")) with
      | Error(CohortError.UnknownLanding(LandingId "nope")) -> ()
      | other -> failtestf "expected UnknownLanding, got %A" other

    testCase "a landing not in Verifying (e.g. still Rebasing) is refused, not silently transitioned" <| fun () ->
      let landingId = LandingId "l-0"
      match Purpose.tryCreate "purpose", Statement.tryCreate "land my change" with
      | Ok _, Ok statement ->
        let req : LandingRequest<MemberId> = {
          Id = landingId; Requester = requester; Claims = []; Commits = [ "c1" ]
          BaseAtQueue = "H0"; Statement = statement; State = LandingState.Rebasing "H0"
        }
        let state = {
          CohortState.empty () with
            IntegrationHead = "H0"
            Members = Map.ofList [ requester, { Role = JoinableRole.Implementer; Presence = MemberPresence.Present; LastRenewal = epoch; Session = None } ]
            Landings = Map.ofList [ landingId, req ]
            Queue = [ landingId ]
        }
        match decide epoch [||] state (CohortCommand.FastForwardFailed(landingId, "boom")) with
        | Error(CohortError.LandingNotInExpectedState(id, "Verifying")) -> id |> Expect.equal "names the offending landing" landingId
        | other -> failtestf "expected LandingNotInExpectedState, got %A" other
      | _ -> failtest "fixture setup failed"
  ]

// ── End-to-end through the real owner: the performer's Error actually drives
//    the transition, not just `decide` in isolation ─────────────────────────

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private fixedClock (at: DateTime) : unit -> DateTime = fun () -> at

let private counterEntropy () : unit -> byte[] =
  let mutable n = 0
  fun () ->
    let bytes = BitConverter.GetBytes n
    n <- n + 1
    bytes

let rec private waitUntil (owner: CohortOwner.Handle) (deadline: DateTime) (describe: unit -> string) (check: unit -> bool) : Async<unit> =
  async {
    do! owner.Flush() |> Async.AwaitTask
    if check () then
      return ()
    elif DateTime.UtcNow > deadline then
      failtestf "condition not met within timeout: %s" (describe ())
    else
      do! Async.Sleep 15
      return! waitUntil owner deadline describe check
  }

let private landingOf (owner: CohortOwner.Handle) (id: LandingId) : LandingRequest<MemberId> =
  owner.ReadCohortState().Landings
  |> Map.tryFind id
  |> Option.defaultWith (fun () -> failtestf "expected landing %A to exist" id)

let private landingIdFrom (events: CohortEvent<MemberId> list) : LandingId =
  events
  |> List.tryPick (function
    | CohortEvent.LandingQueued(id, _) -> Some id
    | _ -> None)
  |> Option.defaultWith (fun () -> failtestf "expected a LandingQueued event, got %A" events)

[<Tests>]
let cohortFastForwardFailedOwnerTests =
  testList "CohortOwner FastForward failure recovery (roast-6 #7b)" [

    testTask "WHY — a FastForward that fails once then succeeds on retry still reaches Landed: the owner's effect-dispatch loop posts FastForwardFailed instead of stranding the landing in Verifying" {
      let ledger = InMemory.create<MemberId> ()
      let mutable fastForwardCalls = 0
      let performer : CohortOwner.LandingPerformer<MemberId> = {
        Rebase = fun _ onto _ -> async { return Ok(onto + "-rebased") }
        ComputeAffected = fun _ _ _ -> async { return Ok [ TestId "t1" ] }
        RunTests = fun _ _ -> async { return Ok [] }
        FastForward = fun _ toSha ->
          async {
            fastForwardCalls <- fastForwardCalls + 1
            match fastForwardCalls with
            | 1 -> return Error "git: transient I/O error"
            | _ -> return Ok(toSha + "-committed")
          }
        Notify = fun _ _ -> ()
      }
      use owner = CohortOwner.startWithPerformer silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) performer
      let! _ = owner.Commit(CohortCommand.Join(requester, JoinableRole.Implementer, None))
      let! requestResult = owner.Commit(CohortCommand.RequestLanding(requester, [], [ "c1" ], "land my change"))
      let landingId =
        match requestResult with
        | Ok(events, _) -> landingIdFrom events
        | Error err -> failtestf "RequestLanding was refused: %A" err

      do!
        waitUntil owner (DateTime.UtcNow.AddSeconds 10.0) (fun () -> sprintf "%A" (landingOf owner landingId).State) (fun () ->
          match (landingOf owner landingId).State with
          | LandingState.Landed _ -> true
          | _ -> false)

      match (landingOf owner landingId).State with
      | LandingState.Landed sha ->
        sha |> Expect.equal "the SECOND fast-forward attempt is what lands" (nullSha + "-rebased-committed")
      | other -> failtestf "expected Landed after retry, got %A" other

      fastForwardCalls |> Expect.equal "FastForward was retried exactly once after the first failure" 2
    }
  ]
