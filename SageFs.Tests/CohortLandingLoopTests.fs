/// Item 14b of sagefs-multiagent-vision.md: `CohortOwner`'s effect-dispatch
/// loop is the KEYSTONE that makes a queued landing actually progress —
/// `Cohort.decide` (SageFs.Core/Cohort.fs, untouched by this item) only
/// returns `CohortEffect`s as data; `CohortOwner.handle` now performs them
/// via an injected `LandingPerformer` and posts the typed completion command
/// back onto its own mailbox. These tests drive a real landing end-to-end
/// through `CohortOwner.startWithPerformer` with FAKE, deterministic
/// performers — no real git, no real sessions — proving the wiring, not the
/// git/test integration (that is item 14c).
module SageFs.Tests.CohortLandingLoopTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private fixedClock (at: DateTime) : unit -> DateTime = fun () -> at

/// Deterministic, distinct entropy per call, exactly `CohortOwnerTests`'s
/// helper — real landing ids are minted from this.
let private counterEntropy () : unit -> byte[] =
  let mutable n = 0
  fun () ->
    let bytes = BitConverter.GetBytes n
    n <- n + 1
    bytes

let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

/// An `Async<'a>` that never completes — used for effects a test deliberately
/// never wants to resolve (e.g. to freeze a landing mid-`Rebasing` so a
/// second, queued landing's state can be inspected without a timing race).
let private neverCompletes<'a> () : Async<'a> = Async.AwaitTask(TaskCompletionSource<'a>().Task)

/// A performer whose `Rebase` never completes; the other fields are never
/// meant to be reached by the scenarios that use this (nothing past
/// `Rebasing` can happen), so they also never complete — reaching them would
/// itself be a test bug, not a value worth returning.
let private frozenAtRebasePerformer : CohortOwner.LandingPerformer<MemberId> = {
  Rebase = fun _ _ -> neverCompletes ()
  ComputeAffected = fun _ _ _ -> neverCompletes ()
  RunTests = fun _ _ -> neverCompletes ()
  FastForward = fun _ _ -> neverCompletes ()
  Notify = fun _ _ -> ()
}

/// A performer that always succeeds. `Rebase` returns a DISTINCT new head
/// (`onto + "-rebased"`), NOT an echo of `onto` — a real rebase always
/// produces a fresh commit that differs from the base it sits on. This is the
/// regression guard for the HeadMoved bug: `decide` must compare the landing's
/// BASE (what it rebased onto) — not its rebased head — against IntegrationHead
/// at land time, so a landing whose head hasn't moved still reaches `Landed`.
/// An earlier echo here masked the bug entirely. `affectedTests`/`failingTests`/
/// `fastForwardSha` are injected so each test controls what gets verified.
let private happyPathPerformer
  (affectedTests: TestId list)
  (failingTests: TestId list)
  (fastForwardShaOf: string -> string)
  : CohortOwner.LandingPerformer<MemberId> =
  { Rebase = fun _ onto -> async { return Ok(onto + "-rebased") }
    ComputeAffected = fun _ _ _ -> async { return Ok affectedTests }
    RunTests = fun _ _ -> async { return Ok failingTests }
    FastForward = fun _ toSha -> async { return Ok(fastForwardShaOf toSha) }
    Notify = fun _ _ -> () }

/// Polls (via `Flush` + a short async sleep — never `Thread.Sleep`, never an
/// unbounded wait) until `check` holds or `deadline` passes. The effect
/// loop's completions arrive on the mailbox from background `Async.Start`
/// workers, not from any message the test itself posts, so there is no
/// single `Flush` that is guaranteed to observe the fully-settled state —
/// this is the standard "wait for eventual consistency" shape for that.
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

let private defaultDeadline () = DateTime.UtcNow.AddSeconds 10.0

let private landingOf (owner: CohortOwner.Handle) (id: LandingId) : LandingRequest<MemberId> =
  owner.ReadCohortState().Landings
  |> Map.tryFind id
  |> Option.defaultWith (fun () -> failtestf "expected landing %A to exist" id)

/// Extracts the freshly-minted `LandingId` from a `RequestLanding` commit's
/// own events — `decide` always emits `LandingQueued(id, requester)` first
/// for an accepted `RequestLanding` (Cohort.fs).
let private landingIdFrom (events: CohortEvent<MemberId> list) : LandingId =
  events
  |> List.tryPick (function
    | CohortEvent.LandingQueued(id, _) -> Some id
    | _ -> None)
  |> Option.defaultWith (fun () -> failtestf "expected a LandingQueued event, got %A" events)

let private requestLanding (owner: CohortOwner.Handle) (who: MemberId) (statement: string) : Task<LandingId> =
  task {
    let! result = owner.Commit(CohortCommand.RequestLanding(who, [], [ "c1" ], statement))
    match result with
    | Ok(events, _) -> return landingIdFrom events
    | Error err -> return failtestf "RequestLanding was refused: %A" err
  }

[<Tests>]
let cohortLandingLoopTests =
  testList "CohortLandingLoop" [

    testTask "WHY — a landing with no conflicts and no failing tests reaches Landed and moves IntegrationHead (item 14b)" {
      let ledger = InMemory.create<MemberId> ()
      let performer = happyPathPerformer [ TestId "t1" ] [] (fun toSha -> toSha + "-committed")
      use owner = CohortOwner.startWithPerformer silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) performer
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let! landingId = requestLanding owner alice "land my change" |> Async.AwaitTask

      do!
        waitUntil owner (defaultDeadline ()) (fun () -> sprintf "%A" (landingOf owner landingId).State) (fun () ->
          match (landingOf owner landingId).State with
          | LandingState.Landed _ -> true
          | _ -> false)

      match (landingOf owner landingId).State with
      | LandingState.Landed sha -> sha |> Expect.equal "the landed commit is the fast-forward of the REAL rebased head (onto -> onto-rebased -> onto-rebased-committed)" (nullSha + "-rebased-committed")
      | other -> failtestf "expected Landed, got %A" other

      owner.ReadCohortState().IntegrationHead
      |> Expect.equal "IntegrationHead moved to the landed commit" (nullSha + "-rebased-committed")

      owner.ReadCohortState().Queue
      |> Expect.equal "the landed landing is popped off the queue" []
    }

    testTask "WHY — a rebase conflict blocks the landing with the real conflicting files (item 14b)" {
      let ledger = InMemory.create<MemberId> ()
      let performer = {
        frozenAtRebasePerformer with
          Rebase = fun _ _ -> async { return Error [ "Conflicting.fs" ] }
      }
      use owner = CohortOwner.startWithPerformer silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) performer
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let! landingId = requestLanding owner alice "land my change" |> Async.AwaitTask

      do!
        waitUntil owner (defaultDeadline ()) (fun () -> sprintf "%A" (landingOf owner landingId).State) (fun () ->
          match (landingOf owner landingId).State with
          | LandingState.Blocked _ -> true
          | _ -> false)

      match (landingOf owner landingId).State with
      | LandingState.Blocked(LandingBlocker.RebaseConflict files, NextAction.RebaseAndResubmit) ->
        files |> Expect.equal "the blocker names the real conflicting file" [ "Conflicting.fs" ]
      | other -> failtestf "expected Blocked(RebaseConflict, RebaseAndResubmit), got %A" other
    }

    testTask "WHY — a failing test blocks the landing with the failing TestIds (item 14b)" {
      let ledger = InMemory.create<MemberId> ()
      let failing = [ TestId "SomeTest.fails" ]
      let performer = happyPathPerformer [ TestId "SomeTest.fails" ] failing (fun toSha -> toSha + "-committed")
      use owner = CohortOwner.startWithPerformer silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) performer
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let! landingId = requestLanding owner alice "land my change" |> Async.AwaitTask

      do!
        waitUntil owner (defaultDeadline ()) (fun () -> sprintf "%A" (landingOf owner landingId).State) (fun () ->
          match (landingOf owner landingId).State with
          | LandingState.Blocked _ -> true
          | _ -> false)

      match (landingOf owner landingId).State with
      | LandingState.Blocked(LandingBlocker.FailingTests fails, NextAction.FixTests fixTests) ->
        fails |> Expect.equal "the blocker names the failing test" failing
        fixTests |> Expect.equal "the next action names the same failing test" failing
      | other -> failtestf "expected Blocked(FailingTests, FixTests), got %A" other
    }

    testTask "WHY — an INCONCLUSIVE verification (couldn't run the tests, NOT a real failure) blocks with Inconclusive, is popped from the queue, and lets the next landing advance — it never permanently jams the queue the way FailingTests does (Gap 3, roast-7 mode-shift)" {
      let ledger = InMemory.create<MemberId> ()
      // Rebase and ComputeAffected succeed; the verifier cannot reach a verdict
      // (e.g. the integration session is still warming up after the rebase
      // rebuild). This is `Error`, distinct from `Ok failingTests`.
      let performer =
        { happyPathPerformer [ TestId "t1" ] [] (fun toSha -> toSha + "-committed") with
            RunTests = fun _ _ -> async { return Error "integration session still warming up" } }
      use owner = CohortOwner.startWithPerformer silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) performer
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let! _ = owner.Commit(CohortCommand.Join(bob, JoinableRole.Verifier, None))
      let! landingA = requestLanding owner alice "A" |> Async.AwaitTask
      let! landingB = requestLanding owner bob "B" |> Async.AwaitTask

      // A cannot verify -> Blocked(Inconclusive) and is popped; B then ADVANCES
      // (proving the queue un-jammed) and hits the same inconclusive. If an
      // inconclusive jammed the queue the way FailingTests does, B would stay
      // Queued forever and this would time out.
      do!
        waitUntil owner (defaultDeadline ())
          (fun () -> sprintf "A=%A B=%A" (landingOf owner landingA).State (landingOf owner landingB).State)
          (fun () ->
            match (landingOf owner landingA).State, (landingOf owner landingB).State with
            | LandingState.Blocked(LandingBlocker.Inconclusive _, _), LandingState.Blocked(LandingBlocker.Inconclusive _, _) -> true
            | _ -> false)

      match (landingOf owner landingA).State with
      | LandingState.Blocked(LandingBlocker.Inconclusive reason, NextAction.RebaseAndResubmit) ->
        reason |> Expect.stringContains "the blocker carries the verifier's own reason, not a fabricated test failure" "warming up"
      | other -> failtestf "expected Blocked(Inconclusive, RebaseAndResubmit), got %A" other

      owner.ReadCohortState().Queue
      |> Expect.equal "an inconclusive landing is popped (it never jams the serial queue); B advanced past it and was popped too" []
    }

    testTask "WHY — a second queued landing does not start rebasing while the first is still in flight (item 14b, strict FIFO)" {
      let ledger = InMemory.create<MemberId> ()
      // `Rebase` never completes, so landing A is pinned in `Rebasing`
      // forever — this removes any timing race from the assertion below:
      // `decide`'s own `advanceQueue` (Cohort.fs, untouched) is what keeps B
      // at `Queued`, not luck about how fast a fake async resolves.
      use owner =
        CohortOwner.startWithPerformer silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) frozenAtRebasePerformer
      let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None))
      let! _ = owner.Commit(CohortCommand.Join(bob, JoinableRole.Verifier, None))
      let! landingA = requestLanding owner alice "A" |> Async.AwaitTask
      let! landingB = requestLanding owner bob "B" |> Async.AwaitTask

      match (landingOf owner landingA).State with
      | LandingState.Rebasing _ -> ()
      | other -> failtestf "expected landing A to be Rebasing, got %A" other

      match (landingOf owner landingB).State with
      | LandingState.Queued -> ()
      | other -> failtestf "expected landing B to still be Queued while A is in flight, got %A" other

      owner.ReadCohortState().Queue
      |> Expect.equal "the queue is strictly FIFO: A in front, B behind it" [ landingA; landingB ]
    }
  ]
