module SageFs.Tests.WarmupOutcomeIntegrationTests

/// Host-tier outcome test for the fcs-onboarding-trial fix
/// (fcs-trial-a/b/c, 2026-09-22): real SessionManager, real spawned worker
/// process, a project that cannot resolve. `WarmupSupervisionTests.fs` and
/// `WarmupSimTests.fs` cover the pure decision layer; this proves the SAME
/// contract holds through the real IO shell — a spawned worker process and
/// real warmup — not just the pure model.
///
/// THE CLAIM: creating a session against an unresolvable project reaches a
/// STATED failure (never left reading "Starting"), and `stop_session`
/// afterwards returns promptly — never the 300-second hang two of the three
/// onboarding trials reported.
open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Best-effort cleanup — the test's own StopSession call is the assertion
/// under test, so this is only a safety net for a mid-test failure.
let private cleanupSession (mgr: MailboxProcessor<SageFs.SessionManager.SessionCommand>) (sessionId: SessionId) =
  try
    mgr.PostAndAsyncReply((fun reply -> SageFs.SessionManager.SessionCommand.StopSession(sessionId, reply)), 5000)
    |> Async.RunSynchronously
    |> ignore
  with _ -> ()

[<Tests>]
let tests =
  Integration.hostList "Warmup outcome — unresolvable project" [

    testTask "create against an unresolvable project reaches a stated failure, and stop_session then returns promptly" {
      // A REAL .fsproj on disk (so it isn't silently skipped in favor of
      // empty-directory auto-discovery) that references a project which
      // does not exist — the same shape as fcs-trial-c's "Not all DLLs are
      // found" and fcs-trial-a's missing FSharp.Test.Utilities path: a
      // project reference the worker can never resolve, no matter how long
      // it waits.
      let workingDir =
        Path.Combine(Path.GetTempPath(), "sagefs-test", "warmup-outcome-" + Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory(workingDir) |> ignore
      let unresolvableProject = Path.Combine(workingDir, "Unresolvable.fsproj")
      let projectXml =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
        "  <ItemGroup><Compile Include=\"Program.fs\" /></ItemGroup>\n" +
        "  <ItemGroup><ProjectReference Include=\"../NoSuchDependency/NoSuchDependency.fsproj\" /></ItemGroup>\n" +
        "</Project>\n"
      File.WriteAllText(unresolvableProject, projectXml)
      File.WriteAllText(Path.Combine(workingDir, "Program.fs"), "module Program\nlet x = 1\n")

      let cts = new CancellationTokenSource(int Timeouts.integrationDaemonReady.TotalMilliseconds)
      let mgr, _ =
        SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
      let mutable createdSessionId : SessionId option = None

      try
        let! createResult =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.CreateSession(
              [ SageFs.SessionProjectTarget.Project unresolvableProject ], workingDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
          |> Async.StartAsTask

        match createResult with
        | Error err ->
          // A synchronous refusal (e.g. an unhostable/invalid project caught
          // before spawning) is ALSO a stated failure — the invariant this
          // test protects ("never Starting forever") holds trivially here.
          SageFsError.describe err |> Expect.isNotNull "a real, described refusal"
        | Ok info ->
          createdSessionId <- Some info.Id
          // The common shape: a worker DOES spawn (CreateSession answers
          // immediately, per SessionManager's own doctrine — "register
          // immediately with pending proxy, don't block") and warmup fails
          // once it actually tries to resolve the project. AwaitReady is
          // the daemon's own bounded wait for exactly this outcome.
          let! (ready: Result<unit, SageFsError>) =
            mgr.PostAndAsyncReply(fun reply ->
              SageFs.SessionManager.SessionCommand.AwaitReady(info.Id, reply))
            |> Async.StartAsTask

          // THE CLAIM (part 1): a stated failure, never a silent "Starting".
          match ready with
          | Error err ->
            SageFsError.describe err |> Expect.isNotNull "a real, described failure reason"
          | Ok () ->
            failtest "an unresolvable project must not reach Ready"

          // Confirm the registry agrees — no reader of this session's state
          // should ever see Starting once AwaitReady has settled (defect #3:
          // "status must never disagree with itself").
          let! (afterAwait: SageFs.SessionManager.ManagedSession option) =
            mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.GetSession(info.Id, reply))
            |> Async.StartAsTask
          match afterAwait with
          | Some session ->
            match session.Info.Status with
            | SessionLifecycleStatus.Starting _ ->
              failtest "the registry must not still read Starting once AwaitReady has settled the outcome"
            | _ -> ()
          | None -> ()

          // THE CLAIM (part 2): stop_session returns promptly — never the
          // 300-second hang fcs-trial-b/c reported for a faulted session.
          let sw = System.Diagnostics.Stopwatch.StartNew()
          let! (stopResult: Result<unit, SageFsError>) =
            mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.StopSession(info.Id, reply))
            |> Async.StartAsTask
          sw.Stop()
          stopResult |> Expect.isOk "stop_session succeeds even on a faulted/never-ready session"
          (sw.Elapsed.TotalSeconds, 15.0)
          |> Expect.isLessThan "stop_session returns promptly, nowhere near the reported 300s hang"
      finally
        createdSessionId |> Option.iter (cleanupSession mgr)
        try Directory.Delete(workingDir, true) with _ -> ()
    }
  ]
