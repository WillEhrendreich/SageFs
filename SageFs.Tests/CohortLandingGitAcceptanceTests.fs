/// Item 14c of sagefs-multiagent-vision.md — LIVE end-to-end acceptance test:
/// this is the one place that proves `request_landing` actually rebases a
/// member's commits, verifies them, and fast-forwards them onto a
/// per-cohort integration branch using REAL `git` subprocesses, not fakes.
///
/// What is REAL here:
///  - `Rebase`  → the real `SageFs.Features.CohortGit.rebase`, run against a
///    real integration git worktree of a real throwaway temp repo.
///  - `FastForward` → the real `SageFs.Features.CohortGit.fastForwardBranch`,
///    run against that same temp repo's main checkout.
///  - The cohort state machine (`Cohort.decide`) and its effect-dispatch
///    loop (`CohortOwner.startWithPerformer`) — untouched by this item,
///    driving the real performer exactly as production's DaemonMode.fs does.
///
/// What is STUBBED (documented, not real, and explicitly allowed by this
/// item's brief): `ComputeAffected` and `RunTests` return `[]` (no tests) —
/// there is no live FSI session or discovered test suite in a throwaway git
/// fixture, and item 14d's `CohortLandingVerify.runTestsInSession` requires
/// one. Production's DaemonMode.fs wires those to the real live-testing
/// session (see the performer built there); this test only proves the git
/// mechanics, which is item 14c's actual, stated risk.
///
/// Test-repo topology, once per test:
///   - A throwaway temp repo (never the SageFs repo, never the live daemon)
///     with one base commit C0 on `main`.
///   - A real integration git worktree, checked out on a fresh branch
///     `sagefs-cohort-test` at C0 — the same shape `set_integration_ref`
///     (Mcp.fs) produces at runtime, built here directly with
///     `CohortGit.addWorktree` since this test drives the git mechanics
///     without a daemon.
///   - "The member's commit" is made on a SEPARATE local branch
///     (`member-work`) checked out IN the integration worktree, off the
///     same base — never committed directly onto `sagefs-cohort-test`
///     itself (committing directly onto the shared branch would advance
///     its ref immediately, as a side effect of the commit, making the
///     later "landing" a no-op that proves nothing). `member-work` is what
///     stays checked out when the landing's `Rebase` effect fires —
///     matching `CohortGit.rebase`'s documented contract, "rebases the
///     checked-out branch in repoDir".
///
/// DISCOVERED GAP this test's performer works around (see `gitBackedPerformer`
/// below and `DaemonMode.fs`'s matching production performer for the full
/// explanation): `Cohort.decide`'s `FastForwardCompleted` handler compares
/// `LandingState.Verifying`'s `onto` field — which `RebaseCompleted` sets to
/// the REBASE'S OWN result — against `state.IntegrationHead` to detect
/// `LandingBlocker.HeadMoved`. For a real rebase (whose result is never
/// equal to the unchanged head it was based on) this always misfires. This
/// is a pre-existing defect in the already-merged `Cohort.fs` (items
/// 14a/14b) that item 14c is not permitted to fix (Cohort.fs may only gain
/// the additive `SetIntegrationHead` arm) — it is compensated for entirely
/// within the performer instead: `Rebase`'s success payload echoes `onto`
/// back (so decide's bookkeeping is evaluated correctly), and `FastForward`
/// independently re-reads the integration worktree's REAL current HEAD via
/// `CohortGit.currentHead` rather than trusting decide's (necessarily
/// fictional, post-echo) `toSha` argument.
module SageFs.Tests.CohortLandingGitAcceptanceTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private fixedClock (at: DateTime) : unit -> DateTime = fun () -> at

let private counterEntropy () : unit -> byte[] =
  let mutable n = 0
  fun () ->
    let bytes = BitConverter.GetBytes n
    n <- n + 1
    bytes

let private alice = MemberId.Minted "alice"

let private gitAvailable () : bool =
  try
    let psi =
      ProcessStartInfo(
        "git", "--version",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false)
    use proc = Process.Start psi
    proc.WaitForExit(5000) |> ignore
    proc.HasExited && proc.ExitCode = 0
  with _ -> false

/// Independent oracle: a SEPARATE, minimal git-process invocation from
/// `CohortGit`'s own (mirrors `CohortGitTests.fs`'s discipline) — fixture
/// setup and the final "did the integration branch really get the member's
/// commit" verification never go through `CohortGit`, so a bug shared by
/// both implementations can't hide.
let private git (dir: string) (args: string list) : Async<string> =
  async {
    let psi =
      ProcessStartInfo(
        "git",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = dir)
    for a in args do psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let! stdout = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
    let! stderr = proc.StandardError.ReadToEndAsync() |> Async.AwaitTask
    do! proc.WaitForExitAsync() |> Async.AwaitTask
    match proc.ExitCode with
    | 0 -> return stdout.Trim()
    | code -> return failwithf "git %s failed (%d) in %s: %s" (String.concat " " args) code dir stderr
  }

let private initRepo (dir: string) : Async<unit> =
  async {
    let! _ = git dir [ "init"; "--quiet"; "-b"; "main" ]
    let! _ = git dir [ "config"; "user.email"; "sagefs-test@example.com" ]
    let! _ = git dir [ "config"; "user.name"; "SageFs Test" ]
    return ()
  }

let private writeAndCommit (dir: string) (relPath: string) (content: string) (message: string) : Async<unit> =
  async {
    let full = Path.Combine(dir, relPath)
    let parent = Path.GetDirectoryName full
    if not (String.IsNullOrEmpty parent) then Directory.CreateDirectory parent |> ignore
    File.WriteAllText(full, content)
    let! _ = git dir [ "add"; relPath ]
    let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
    return ()
  }

/// The real, production-shaped `LandingPerformer` for this test: `Rebase`
/// and `FastForward` are the real `CohortGit` functions against the real
/// temp repo (with the DISCOVERED GAP workaround from this file's header —
/// exactly mirroring `DaemonMode.fs`'s production performer);
/// `ComputeAffected`/`RunTests` are the documented stubs (see this file's
/// header).
let private gitBackedPerformer (integrationWorktree: string) (mainRepo: string) (branch: string) : CohortOwner.LandingPerformer<MemberId> =
  { Rebase = fun _ onto ->
      async {
        let! result = CohortGit.rebase integrationWorktree onto
        match result with
        | Ok _realNewHead -> return Ok onto // see this file's header: DISCOVERED GAP
        | Error files -> return Error files
      }
    ComputeAffected = fun _ _ _ -> async { return [] }
    RunTests = fun _ _ -> async { return [] }
    FastForward = fun _ _toShaFromDecide ->
      async {
        let! headResult = CohortGit.currentHead integrationWorktree
        match headResult with
        | Error e -> return Error (sprintf "could not read the integration worktree's real HEAD to fast-forward to: %s" e)
        | Ok realHead -> return! CohortGit.fastForwardBranch mainRepo branch realHead
      }
    Notify = fun _ _ -> () }

/// Polls (via `Flush` + a short async sleep, never `Thread.Sleep`) until
/// `check` holds or `deadline` passes — completions from the real git
/// subprocesses arrive on the mailbox from background `Async.Start` workers,
/// same pattern as `CohortLandingLoopTests.fs`'s `waitUntil`.
let rec private waitUntil (owner: CohortOwner.Handle) (deadline: DateTime) (describe: unit -> string) (check: unit -> bool) : Async<unit> =
  async {
    do! owner.Flush() |> Async.AwaitTask
    if check () then
      return ()
    elif DateTime.UtcNow > deadline then
      failtestf "condition not met within timeout: %s" (describe ())
    else
      do! Async.Sleep 25
      return! waitUntil owner deadline describe check
  }

let private defaultDeadline () = DateTime.UtcNow.AddSeconds 30.0

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

let private requestLanding (owner: CohortOwner.Handle) (who: MemberId) (commits: string list) (statement: string) : Task<LandingId> =
  task {
    let! result = owner.Commit(CohortCommand.RequestLanding(who, [], commits, statement))
    match result with
    | Ok(events, _) -> return landingIdFrom events
    | Error err -> return failtestf "RequestLanding was refused: %A" err
  }

[<Tests>]
let tests =
  Integration.hostList "CohortLandingGit" [

    if not (gitAvailable ()) then
      testCase "git must be available on PATH" <| fun () ->
        failtest "git is not available on PATH — item 14c's real landing pipeline requires a real git executable"
    else

    yield! [

      testTask "WHY — request_landing really rebases, verifies, and fast-forwards a member's commit onto the integration branch (item 14c, real git)" {
        let mainRepo = Directory.CreateTempSubdirectory("cohort14c-main-").FullName
        let worktreeParent = Directory.CreateTempSubdirectory("cohort14c-wt-").FullName
        let integrationWorktree = Path.Combine(worktreeParent, "integration")
        let branch = "sagefs-cohort-test"
        try
          // ── Fixture: base commit, then the real integration worktree ──
          do! initRepo mainRepo
          do! writeAndCommit mainRepo "shared.txt" "base\n" "base commit"
          let! c0 = git mainRepo [ "rev-parse"; "HEAD" ]
          let! addResult = CohortGit.addWorktree mainRepo integrationWorktree branch c0
          match addResult with
          | Error e -> failtestf "fixture setup: addWorktree failed: %s" e
          | Ok () -> ()

          // "The member's commit" — on a SEPARATE local branch checked out
          // in the integration worktree (see this file's header: committing
          // directly onto `branch` would advance its ref immediately as a
          // side effect of the commit, making the later "landing" a no-op).
          let! _ = git integrationWorktree [ "checkout"; "-b"; "member-work" ]
          do! writeAndCommit integrationWorktree "member-feature.txt" "member work\n" "member commit"
          let! memberSha = git integrationWorktree [ "rev-parse"; "HEAD" ]

          // ── Drive the real cohort state machine + real performer ──
          let ledger = InMemory.create<MemberId> ()
          let performer = gitBackedPerformer integrationWorktree mainRepo branch
          use owner = CohortOwner.startWithPerformer silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) performer
          let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None)) // alice: first joiner, becomes conductor
          let! setHeadResult = owner.Commit(CohortCommand.SetIntegrationHead(alice, c0))
          setHeadResult |> Result.isOk |> Expect.isTrue "the conductor can configure the integration head"

          let! landingId = requestLanding owner alice [ memberSha ] "land the member's feature" |> Async.AwaitTask

          do!
            waitUntil owner (defaultDeadline ()) (fun () -> sprintf "%A" (landingOf owner landingId).State) (fun () ->
              match (landingOf owner landingId).State with
              | LandingState.Landed _ -> true
              | LandingState.Blocked _ -> true // fail fast with a real reason rather than timing out
              | _ -> false)

          match (landingOf owner landingId).State with
          | LandingState.Landed landedSha ->
            landedSha |> Expect.equal "the landed sha is the member's real commit sha" memberSha
          | LandingState.Blocked(blocker, _) -> failtestf "expected Landed, landing was Blocked: %A" blocker
          | other -> failtestf "expected Landed, got %A" other

          owner.ReadCohortState().IntegrationHead
          |> Expect.equal "IntegrationHead moved to the member's commit" memberSha
          owner.ReadCohortState().Queue
          |> Expect.equal "the landed landing is popped off the queue" []

          // ── Independent oracle verification: real `git log` on the real branch ──
          let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
          branchSha |> Expect.equal "the integration branch's ref now points at the member's commit" memberSha
          let! log = git mainRepo [ "log"; branch; "--oneline" ]
          log.Contains "member commit" |> Expect.isTrue "the integration branch's real git log contains the member's commit"

          // ── Worktree left clean ──
          let! status = git integrationWorktree [ "status"; "--porcelain" ]
          status |> Expect.isEmpty "the integration worktree is clean after a successful landing"
        finally
          (try Directory.Delete(mainRepo, true) with _ -> ())
          (try Directory.Delete(worktreeParent, true) with _ -> ())
      }

      testTask "WHY — a real rebase conflict blocks the landing, and the integration branch is left untouched (item 14c, real git)" {
        let mainRepo = Directory.CreateTempSubdirectory("cohort14c-conflict-main-").FullName
        let worktreeParent = Directory.CreateTempSubdirectory("cohort14c-conflict-wt-").FullName
        let integrationWorktree = Path.Combine(worktreeParent, "integration")
        let branch = "sagefs-cohort-conflict"
        try
          do! initRepo mainRepo
          do! writeAndCommit mainRepo "shared.txt" "base\n" "base commit"
          let! c0 = git mainRepo [ "rev-parse"; "HEAD" ]
          let! addResult = CohortGit.addWorktree mainRepo integrationWorktree branch c0
          match addResult with
          | Error e -> failtestf "fixture setup: addWorktree failed: %s" e
          | Ok () -> ()

          // The member's commit, on a SEPARATE local branch checked out in
          // the integration worktree (see this file's header — same reason
          // as the happy-path test), edits shared.txt.
          let! _ = git integrationWorktree [ "checkout"; "-b"; "member-work" ]
          do! writeAndCommit integrationWorktree "shared.txt" "member change\n" "member commit"
          let! memberSha = git integrationWorktree [ "rev-parse"; "HEAD" ]

          // The conductor independently advances the integration head (in the
          // MAIN repo's own checkout of `main`) with a CONFLICTING edit to the
          // same file — this is what makes the later rebase onto the new head
          // a real conflict, not a fast-forward no-op.
          do! writeAndCommit mainRepo "shared.txt" "conductor change\n" "conductor commit"
          let! c1 = git mainRepo [ "rev-parse"; "main" ]

          let ledger = InMemory.create<MemberId> ()
          let performer = gitBackedPerformer integrationWorktree mainRepo branch
          use owner = CohortOwner.startWithPerformer silentLogger ledger (fixedClock epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) performer
          let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None))
          let! setHeadResult = owner.Commit(CohortCommand.SetIntegrationHead(alice, c1))
          setHeadResult |> Result.isOk |> Expect.isTrue "the conductor can configure the integration head"

          let! landingId = requestLanding owner alice [ memberSha ] "land the member's conflicting feature" |> Async.AwaitTask

          do!
            waitUntil owner (defaultDeadline ()) (fun () -> sprintf "%A" (landingOf owner landingId).State) (fun () ->
              match (landingOf owner landingId).State with
              | LandingState.Blocked _ -> true
              | LandingState.Landed _ -> true // fail fast with a real reason rather than timing out
              | _ -> false)

          match (landingOf owner landingId).State with
          | LandingState.Blocked(LandingBlocker.RebaseConflict files, NextAction.RebaseAndResubmit) ->
            files |> Expect.equal "the real conflicting file is named" [ "shared.txt" ]
          | LandingState.Landed sha -> failtestf "expected a rebase conflict to block the landing, but it Landed at %s" sha
          | other -> failtestf "expected Blocked(RebaseConflict, RebaseAndResubmit), got %A" other

          // The integration branch must be untouched — FastForward was never
          // dispatched because the rebase never even reached Verifying.
          let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
          branchSha |> Expect.equal "the integration branch ref is untouched by a blocked landing" c0
          owner.ReadCohortState().IntegrationHead
          |> Expect.equal "IntegrationHead is untouched by a blocked landing" c1

          // The integration worktree itself must be clean — CohortGit.rebase
          // always aborts a genuinely-in-progress rebase before returning.
          let! status = git integrationWorktree [ "status"; "--porcelain" ]
          status |> Expect.isEmpty "the integration worktree is clean after an aborted rebase"
        finally
          (try Directory.Delete(mainRepo, true) with _ -> ())
          (try Directory.Delete(worktreeParent, true) with _ -> ())
      }
    ]
  ]
