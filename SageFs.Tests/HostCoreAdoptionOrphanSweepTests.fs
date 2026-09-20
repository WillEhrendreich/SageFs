module SageFs.Tests.HostCoreAdoptionOrphanSweepTests

open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The real end-to-end gate for the /tmp-exhaustion incident: a private
/// self-host launch root must be reclaimed once its worker has exited.
///
/// The subtlety this file exists to guard against: a BARE session (any
/// project that does not ship its own build of SageFs.Core) never adopts a
/// Core at all, so `resolveLaunchRoot` returns the SHARED root unchanged —
/// no private directory is ever materialized, `AdoptedCore` is `None`, and
/// a naive "assert the private root is gone after stop" test would pass
/// VACUOUSLY (there was never a directory to begin with). The fixture here
/// is SageFs's own SageFs.Tests project — exactly like DogfoodReplTests.fs
/// — because it is the one project in this repo guaranteed to ship a
/// same-version build of SageFs.Core and therefore force a REAL adoption.
/// The `AdoptedCore |> Expect.isSome` assertion below is not incidental:
/// it is what stops this test from being the vacuous one it warns about.
let private repoRoot =
  Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private testsProject =
  Path.Combine(repoRoot, "SageFs.Tests", "SageFs.Tests.fsproj")

let private testsDir =
  Path.Combine(repoRoot, "SageFs.Tests")

[<Tests>]
let tests =
  Integration.hostList "Host core adoption: orphan sweep (real worker)" [
    testCaseAsync
      "WHY — a real adoption's private launch root disappears once its worker exits, proven against a fixture that actually adopts a Core rather than one that passes vacuously (roast: 13 orphaned sagefs-host-adopt-* dirs, ~9.5GB)"
      (async {
        use cts = new CancellationTokenSource(240_000)
        let mgr, _ =
          SageFs.SessionManager.create
            cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())

        let! created =
          mgr.PostAndAsyncReply(fun reply ->
            SageFs.SessionManager.SessionCommand.CreateSession(
              [ testsProject ], testsDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))

        match created with
        | Error err -> failtestf "create failed: %s" (SageFsError.describe err)
        | Ok info ->
          let! ready = mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.AwaitReady(info.Id, reply))
          ready |> Expect.isOk "the SageFs.Tests session reaches Ready"

          let! sessionOpt = mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.GetSession(info.Id, reply))
          match sessionOpt with
          | None -> failtest "session vanished after Ready"
          | Some session ->
            // Non-vacuous fixture check — see the module doc comment above.
            session.AdoptedCore
            |> Expect.isSome "this fixture must actually adopt a Core, or the sweep assertion below proves nothing"

            let idValue = WorkerProtocol.SessionId.value info.Id
            let ownsThisSession (dir: string) =
              Path.GetFileName(dir)
                .StartsWith(sprintf "%s%s-" HostCoreAdoption.adoptedRootPrefix idValue, StringComparison.Ordinal)
            let root =
              Directory.GetDirectories(Path.GetTempPath(), HostCoreAdoption.adoptedRootPrefix + "*")
              |> Array.filter ownsThisSession
              |> Array.tryHead

            match root with
            | None ->
              failtest "expected a materialized private launch root for a session that adopted a Core"
            | Some root ->
              Directory.Exists root
              |> Expect.isTrue "the private launch root exists while its worker is alive"
              // The owner marker (this fix) must already be recorded — before
              // the worker ever exits, not just after.
              let markerPath = Path.Combine(root, HostCoreAdoption.adoptedRootOwnerMarkerFileName)
              File.Exists markerPath
              |> Expect.isTrue "the worker's pid is recorded as the root's owner as soon as it is known"
              File.ReadAllText(markerPath).Trim()
              |> Expect.equal "the recorded owner is this session's actual worker pid" (string session.Process.Id)

              let! _ = mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.StopSession(info.Id, reply))

              // Cleanup runs off `proc.Exited`, which races the StopSession
              // reply (WaitForExit and the event are two independent exit
              // detections) — poll rather than assert immediately.
              let deadline = DateTime.UtcNow.AddSeconds 20.0
              let mutable stillThere = Directory.Exists root
              while stillThere && DateTime.UtcNow < deadline do
                do! Async.Sleep 200
                stillThere <- Directory.Exists root

              stillThere
              |> Expect.isFalse "the private launch root is gone once its worker has exited — the proc.Exited happy path"
      })
  ]
