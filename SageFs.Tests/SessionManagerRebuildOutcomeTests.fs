/// What the last rebuild of a session did, recorded by the one owner that
/// knows. It used to be recorded by the MCP adapter, so a rebuild started from
/// the dashboard button, the live-testing effect or the app-run orchestration
/// left it blank, and a failed dashboard rebuild looked like nothing had
/// happened. These tests post `RestartSession` straight to the mailbox, the way
/// every one of those callers does, and read the outcome back off the session.
module SageFs.Tests.SessionManagerRebuildOutcomeTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

type private Harness = { Mailbox: MailboxProcessor<SessionCommand> }

let private mkRuntime (build: Async<Result<string, SageFsError>>) : SessionManagerRuntime =
  { StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ -> ()
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> build }

let private withHarness (build: Async<Result<string, SageFsError>>) (run: Harness -> Task) : Task =
  task {
    use cancellation = new CancellationTokenSource()
    let mailbox, _readSnapshot =
      createWith (mkRuntime build) cancellation.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
    try
      do! run { Mailbox = mailbox }
    finally
      try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ()
      cancellation.Cancel()
  }

let private createSession (harness: Harness) : SessionInfo =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession([ SessionProjectTarget.Project "Foo.fsproj" ], "/nonexistent/does-not-matter", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private rebuildOf (harness: Harness) (id: SessionId) : LastRebuild =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) with
  | Some session -> session.Info.Rebuild
  | None -> failtestf "expected session %s to exist" (SessionId.value id)

let private restartWithRebuild (harness: Harness) (id: SessionId) : Async<Result<string, SageFsError>> =
  harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(id, RestartPlan.Rebuild GranularRestart.RestartSubject.Worker, reply))

let private buildFailed = SageFsError.HardResetFailed "build failed: error FS0001"

[<Tests>]
let tests =
  testList "SessionManager records the outcome of every rebuild" [
    testTask "WHY — a session nobody rebuilt says so, and is not reported as a rebuild that succeeded" {
      do! withHarness (async { return Ok "unused" }) (fun harness -> task {
        let info = createSession harness
        rebuildOf harness info.Id |> Expect.equal "nothing recorded" LastRebuild.NeverRebuilt })
    }

    testTask "WHY — a rebuild that fails while the worker is alive is recorded as still serving, with the compiler's error" {
      do! withHarness (async { return Error buildFailed }) (fun harness -> task {
        let info = createSession harness
        let! result = restartWithRebuild harness info.Id
        result |> Expect.equal "the caller gets the error" (Error buildFailed)
        match rebuildOf harness info.Id with
        | LastRebuild.Latest (RebuildOutcome.FailedStillServing (error, _)) -> error |> Expect.equal "the error is kept" buildFailed
        | other -> failtestf "expected FailedStillServing, got %A" other })
    }

    testTask "WHY — a rebuild that fails when no worker is serving is recorded as not serving" {
      do! withHarness (async { return Error buildFailed }) (fun harness -> task {
        let info = createSession harness
        harness.Mailbox.Post(SessionCommand.UpdateSessionStatus(info.Id, SessionLifecycleStatus.Faulted (FaultReason.Reported "worker died")))
        let! result = restartWithRebuild harness info.Id
        result |> Expect.equal "the caller gets the error" (Error buildFailed)
        match rebuildOf harness info.Id with
        | LastRebuild.Latest (RebuildOutcome.FailedNotServing (error, _)) -> error |> Expect.equal "the error is kept" buildFailed
        | other -> failtestf "expected FailedNotServing, got %A" other })
    }

    testTask "WHY — a rebuild that builds and starts its replacement is recorded as succeeded" {
      do! withHarness (async { return Ok "build ok" }) (fun harness -> task {
        let info = createSession harness
        let! result = restartWithRebuild harness info.Id
        match result with
        | Ok _ -> ()
        | Error err -> failtestf "expected the restart to be accepted, got %s" (SageFsError.describe err)
        match rebuildOf harness info.Id with
        | LastRebuild.Latest (RebuildOutcome.Succeeded _) -> ()
        | other -> failtestf "expected Succeeded, got %A" other })
    }

    testTask "WHY — while the build runs the session says in progress, and a second reset refused meanwhile does not overwrite that" {
      let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let build = async {
        do! release.Task |> Async.AwaitTask
        return Ok "build ok" }
      do! withHarness build (fun harness -> task {
        let info = createSession harness
        let first = restartWithRebuild harness info.Id |> Async.StartAsTask
        let startedAt =
          match rebuildOf harness info.Id with
          | LastRebuild.Latest (RebuildOutcome.InProgress startedAt) -> startedAt
          | other -> failtestf "expected InProgress while the build runs, got %A" other
        let! second = restartWithRebuild harness info.Id
        RestartRefusal.isAlreadyInProgress (match second with Error e -> e | Ok m -> failtestf "the second reset must be refused, got Ok %s" m)
        |> Expect.isTrue "refused as already in progress"
        rebuildOf harness info.Id
        |> Expect.equal "a refusal is not an outcome: still the first rebuild, still in progress" (LastRebuild.Latest (RebuildOutcome.InProgress startedAt))
        release.SetResult()
        let! firstResult = first
        match firstResult with
        | Ok _ -> ()
        | Error err -> failtestf "the first rebuild should have been accepted, got %s" (SageFsError.describe err)
        match rebuildOf harness info.Id with
        | LastRebuild.Latest (RebuildOutcome.Succeeded _) -> ()
        | other -> failtestf "expected Succeeded after the build, got %A" other })
    }

    testTask "WHY — a rebuild that does not rebuild (a plain respawn) records nothing" {
      do! withHarness (async { return Error buildFailed }) (fun harness -> task {
        let info = createSession harness
        let! _ = harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, RestartPlan.RespawnOnly, reply))
        rebuildOf harness info.Id |> Expect.equal "no build ran, so there is no build outcome" LastRebuild.NeverRebuilt })
    }
  ]

let private reloadOf (harness: Harness) (id: SessionId) : SessionReload =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) with
  | Some session -> session.Info.Reload
  | None -> failtestf "expected session %s to exist" (SessionId.value id)

let private freshnessOf (harness: Harness) (id: SessionId) : ReplFreshness =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) with
  | Some session -> session.Info.Freshness
  | None -> failtestf "expected session %s to exist" (SessionId.value id)

let private deltaFacts (case: ReloadCase) (declarations: string list) : SessionReload =
  SessionReload.Finished
    { Case = case; Patched = 1; Considered = 1; Message = "m"; SuggestedAction = ""
      Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.MetadataDelta; Declarations = declarations }

let private deltaPending (declarations: string list) = deltaFacts ReloadCase.PatchPending declarations
let private deltaPatched (declarations: string list) = deltaFacts ReloadCase.Patched declarations

let private restartRequired : SessionReload =
  SessionReload.Finished
    { Case = ReloadCase.RestartRequired; Patched = 0; Considered = 2; Message = "restart the app to apply this"; SuggestedAction = "restart"; Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.NoPatch; Declarations = [] }

[<Tests>]
let reloadTests =
  testList "SessionManager records what the worker said a save did" [
    testTask "WHY — a session no save has resolved for says so" {
      do! withHarness (async { return Ok "unused" }) (fun harness -> task {
        let info = createSession harness
        reloadOf harness info.Id |> Expect.equal "nothing yet" SessionReload.NoReloadYet })
    }

    testTask "WHY — what the worker reports is recorded on the session, latest first, so every surface reads the same thing" {
      do! withHarness (async { return Ok "unused" }) (fun harness -> task {
        let info = createSession harness
        harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, SessionReload.Compiling (Some "/src/Ticker.fs")))
        reloadOf harness info.Id |> Expect.equal "compiling first" (SessionReload.Compiling (Some "/src/Ticker.fs"))
        harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, restartRequired))
        reloadOf harness info.Id |> Expect.equal "then the verdict replaces it" restartRequired })
    }

    testTask "WHY — a report for a session that is gone is dropped, not resurrected" {
      do! withHarness (async { return Ok "unused" }) (fun harness -> task {
        let gone = SessionId.newId ()
        harness.Mailbox.Post(SessionCommand.ReloadObserved(gone, restartRequired))
        // A round trip through the mailbox guarantees the report was processed.
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(gone, reply))
        |> Expect.isNone "still no such session" })
    }

    testTask "WHY — a replacement worker starts with no reload history, because the old worker's verdict is about a process that is gone" {
      do! withHarness (async { return Ok "build ok" }) (fun harness -> task {
        let info = createSession harness
        harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, restartRequired))
        let! _ = harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, RestartPlan.RespawnOnly, reply))
        reloadOf harness info.Id |> Expect.equal "the new worker has not been saved to" SessionReload.NoReloadYet })
    }

    testTask "WHY — a delta that landed in the worker puts the session's REPL behind its app, and the session carries that for every surface to read" {
      do! withHarness (async { return Ok "unused" }) (fun harness -> task {
        let info = createSession harness
        freshnessOf harness info.Id |> Expect.equal "a fresh session is level" ReplFreshness.InSync
        harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, deltaPending [ "Handlers.describe" ]))
        freshnessOf harness info.Id |> Expect.equal "behind after the first save" (ReplFreshness.BehindApp (1, [ "Handlers.describe" ]))
        harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, deltaPatched [ "Handlers.describe" ]))
        harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, restartRequired))
        freshnessOf harness info.Id |> Expect.equal "the confirmation and an unrelated verdict change nothing" (ReplFreshness.BehindApp (1, [ "Handlers.describe" ])) })
    }

    testTask "WHY — a replacement worker is built fresh, so it is level again, by a respawn and by a rebuild alike" {
      for plan in [ RestartPlan.RespawnOnly; RestartPlan.Rebuild GranularRestart.RestartSubject.Worker ] do
        do! withHarness (async { return Ok "build ok" }) (fun harness -> task {
          let info = createSession harness
          harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, deltaPending [ "Handlers.describe" ]))
          let! _ = harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, plan, reply))
          freshnessOf harness info.Id |> Expect.equal (sprintf "level after %A" plan) ReplFreshness.InSync })
    }

    testTask "WHY — a swap keeps a Restarted verdict, because the swap is the restart it reports and an agent reading status right after would otherwise see nothing happened" {
      do! withHarness (async { return Ok "build ok" }) (fun harness -> task {
        let info = createSession harness
        let restarted =
          SessionReload.Finished
            { Case = ReloadCase.Restarted; Patched = 0; Considered = 1; Message = "Restarted the app"; SuggestedAction = ""; Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.NoPatch; Declarations = [] }
        harness.Mailbox.Post(SessionCommand.ReloadObserved(info.Id, restarted))
        let! _ = harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.RestartSession(info.Id, RestartPlan.RespawnOnly, reply))
        reloadOf harness info.Id |> Expect.equal "the verdict that caused the swap survives it" restarted })
    }
  ]
