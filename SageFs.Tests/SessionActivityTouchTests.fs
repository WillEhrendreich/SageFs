/// The bug this pins: `SessionCommand.TouchSession` existed, had a handler,
/// and nothing ever posted it — so `SessionInfo.LastActivity` only moved on
/// create/restart/fault, and every busy session read "Idle" ten minutes after
/// it was created. This drives the REAL SessionManager mailbox (no mocked
/// mailbox, no bypassed handler) through the same `SessionProxy.touching`
/// wrapper production wires into `SessionManagementOps.GetProxy` and
/// `EffectDeps.GetProxy`, and proves LastActivity genuinely advances in real
/// wall-clock time when the wrapped proxy carries an eval — and that the
/// display is honest about it before and after.
module SageFs.Tests.SessionActivityTouchTests

open System
open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol
open SageFs.ProjectLoading

type private Harness = {
  Mailbox: MailboxProcessor<SessionCommand>
  ReadSnapshot: unit -> QuerySnapshot
}

/// A fake worker that answers GetStatus (Ready) and EvalCode (a trivial
/// success) — enough surface for a session to reach Ready and for a
/// wrapped proxy to carry a real "the user did something" message.
let private fakeWorker : SessionProxy =
  fun msg ->
    async {
      match msg with
      | WorkerMessage.GetStatus rid ->
        let snap : WorkerStatusSnapshot = {
          Status = SessionStatus.Ready
          StatusMessage = None
          EvalCount = 0
          AvgDurationMs = 0L
          MinDurationMs = 0L
          MaxDurationMs = 0L
          Projects = []
          CoreVersion = "0.0.0-test"
        }
        return WorkerResponse.StatusResult(rid, snap)
      | WorkerMessage.EvalCode(_, rid) ->
        return WorkerResponse.EvalResult(rid, Ok "val it = 2", [], Map.empty)
      | _ ->
        return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unexpected message")
    }

let private mkRuntime () : SessionManagerRuntime =
  { StartWorkerProcess = fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ -> ()
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

let private withHarness (run: Harness -> Threading.Tasks.Task<unit>) = task {
  use cancellation = new CancellationTokenSource()
  let mailbox, readSnapshot =
    createWith
      (mkRuntime ())
      cancellation.Token
      ignore
      (fun _ _ -> ())
      (fun _ _ -> ())
      ignore
      (fun _ _ -> ())
      (fun _ _ -> ())
      (fun _ _ -> ())
  try
    do! run { Mailbox = mailbox; ReadSnapshot = readSnapshot }
  finally
    try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ()
    cancellation.Cancel()
}

/// Stands in for what the worker would report for "Test.fsproj" actually
/// resolving — the earned-Ready gate (SessionManager.fs's
/// WorkerReportedReady handler) now Faults a session whose requested
/// project resolved to nothing, so a fake `WorkerReportedReady` for a
/// session that named a project must report it as resolved, exactly like a
/// real worker would. This test is about the touch mechanism, not project
/// resolution.
let private resolvedTestProject : ClassifiedProject =
  { Path = "Test.fsproj"; Role = ProjectRole.Library; PackageRefs = [] }

let private createSession (harness: Harness) : SessionInfo =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession(["Test.fsproj"], @"C:\Test", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

/// Blocks until every command posted before this call has been processed —
/// `TouchSession` carries no reply channel, so a round-trip through the SAME
/// mailbox is what proves it already ran (MailboxProcessor processes posts
/// strictly in the order they were enqueued).
let private flush (harness: Harness) (id: SessionId) : SessionInfo =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) with
  | Some session -> session.Info
  | None -> failtestf "expected session %s to still exist" (SessionId.value id)

[<Tests>]
let sessionActivityTouchTests =
  testList "TouchSession — real mailbox outcome" [

    testTask "WHY — a real eval through SessionProxy.touching advances LastActivity, and the display reflects it" {
      do! withHarness (fun harness -> task {
        let created = createSession harness
        let pid = SessionLifecycleStatus.workerPid created.Status |> Option.defaultWith (fun () -> failtest "expected a worker pid")
        harness.Mailbox.Post(SessionCommand.WorkerReady(created.Id, pid, "http://localhost:4123", fakeWorker))
        // WorkerReady only lands Starting; the real Ready transition normally
        // comes from a 1s-interval background poll of the worker's own
        // GetStatus (see SessionManager.fs's WorkerReady handler). Post the
        // same WorkerReportedReady it would eventually post, so the test
        // proves the touch mechanism, not the real poll cadence.
        harness.Mailbox.Post(SessionCommand.WorkerReportedReady(created.Id, pid, [ resolvedTestProject ]))
        let ready = flush harness created.Id
        ready.Status |> function SessionLifecycleStatus.Ready _ -> () | other -> failtestf "expected Ready, got %A" other
        let beforeTouch = ready.LastActivity

        // This is the SAME wrapper production installs at
        // SessionManagementOps.GetProxy / EffectDeps.GetProxy — not a
        // hand-rolled substitute.
        let wrapped =
          SessionProxy.touching
            (fun () -> harness.Mailbox.Post(SessionCommand.TouchSession created.Id))
            fakeWorker

        // Real wall-clock gap so a later LastActivity is unambiguously later,
        // not a same-tick coincidence.
        Thread.Sleep(30)

        let! _ = wrapped (WorkerMessage.EvalCode("1+1", "r1")) |> Async.StartAsTask
        let afterTouch = flush harness created.Id

        (afterTouch.LastActivity > beforeTouch)
        |> Expect.isTrue "LastActivity must advance past its pre-eval value after a routed eval"

        // Pick the instant just 1ms past the OLD (untouched) LastActivity's
        // idle boundary — since the real touch only moved LastActivity
        // forward by the ~30ms sleep above, that same instant is still
        // WITHIN the threshold measured from the fresh, touched value. One
        // instant, two verdicts: Idle from the stale timestamp, Running from
        // the one the touch actually wrote.
        let now = beforeTouch + Timeouts.idleSessionThreshold + TimeSpan.FromMilliseconds(1.0)
        SessionDisplay.displayStatus now { afterTouch with LastActivity = beforeTouch }
        |> Expect.equal "sanity: the untouched timestamp WOULD have read Idle at this instant" SessionDisplayStatus.Idle
        SessionDisplay.displayStatus now afterTouch
        |> Expect.equal "the touched session must read Running at the very same instant" SessionDisplayStatus.Running
      })
    }

    testTask "WHY — a GetStatus poll through the SAME wrapper does not advance LastActivity" {
      do! withHarness (fun harness -> task {
        let created = createSession harness
        let pid = SessionLifecycleStatus.workerPid created.Status |> Option.defaultWith (fun () -> failtest "expected a worker pid")
        harness.Mailbox.Post(SessionCommand.WorkerReady(created.Id, pid, "http://localhost:4123", fakeWorker))
        harness.Mailbox.Post(SessionCommand.WorkerReportedReady(created.Id, pid, [ resolvedTestProject ]))
        let ready = flush harness created.Id
        let beforeTouch = ready.LastActivity

        let wrapped =
          SessionProxy.touching
            (fun () -> harness.Mailbox.Post(SessionCommand.TouchSession created.Id))
            fakeWorker

        Thread.Sleep(30)
        let! _ = wrapped (WorkerMessage.GetStatus "poll") |> Async.StartAsTask
        let afterPoll = flush harness created.Id

        afterPoll.LastActivity
        |> Expect.equal "a bare status poll must never touch LastActivity" beforeTouch
      })
    }
  ]
