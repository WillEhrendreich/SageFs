module SageFs.Tests.DogfoodReplTests

open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Roast-4 #0, verified live before these were written: in a session on
/// SageFs's own test project, `typeof<SageFs.SageFsError>.Assembly.Location`
/// was the INSTALLED TOOL's `host/SageFs.Core.dll` — the REPL ran the host's
/// bits, not the project build it was asked to load — and
/// `SageFs.Tests.EvalTimelineTests.evalTimelineTests` failed to resolve
/// because warmup's auto-open put `PaneId.Tests` (a RequireQualifiedAccess
/// union case from a REFERENCED assembly) in the way of the `SageFs.Tests`
/// namespace, even through `global.`.
///
/// The subject is SageFs developing SageFs, so the fixture is the real
/// SageFs.Tests.fsproj: any other project would not collide with the host's
/// own SageFs.Core the way this one does.
let private repoRoot =
  Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private testsProject =
  Path.Combine(repoRoot, "SageFs.Tests", "SageFs.Tests.fsproj")

let private testsDir =
  Path.Combine(repoRoot, "SageFs.Tests")

let private evalIn (proxy: SessionProxy) (rid: string) (code: string) : Result<string, SageFsError> =
  match proxy (WorkerMessage.EvalCode(code, rid)) |> Async.RunSynchronously with
  | WorkerResponse.EvalResult(_, result, _, _) -> result
  | other -> Error (SageFsError.Unexpected (Exception (sprintf "unexpected eval response: %A" other)))

/// One session shared by both probes: warming up SageFs.Tests is the
/// expensive part, and both questions are about the same loaded session.
let private withDogfoodSession (run: SessionProxy -> unit) =
  use cts = new CancellationTokenSource(240_000)
  let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
  let created =
    mgr.PostAndAsyncReply(fun reply ->
      SageFs.SessionManager.SessionCommand.CreateSession(
        [ testsProject ], testsDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
    |> Async.RunSynchronously
  match created with
  | Error err -> failtestf "create failed: %s" (SageFsError.describe err)
  | Ok info ->
    try
      let ready =
        mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.AwaitReady(info.Id, reply))
        |> Async.RunSynchronously
      ready |> Expect.isOk "the SageFs.Tests session reaches Ready"
      let session =
        mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.GetSession(info.Id, reply))
        |> Async.RunSynchronously
      match session with
      | None -> failtest "session vanished after Ready"
      | Some s -> run s.Proxy
    finally
      mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.StopSession(info.Id, reply))
      |> Async.RunSynchronously
      |> ignore

[<Tests>]
let dogfoodReplTests =
  Integration.hostList "Dogfood REPL: SageFs developing SageFs" [
    // roast-4 #0(a). SageFs.Host statically links SageFs.Core, and —
    // empirically confirmed via AppDomain.CurrentDomain.GetAssemblies() at
    // process start and via direct /eval probes of a live host process —
    // this is NOT a simple "first load wins" ordering race that an
    // in-process trick can fix. SageFs.Core.dll is listed in
    // SageFs.Host.deps.json, so it is a Trusted-Platform-Assembly (TPA) for
    // the host process; TPA-listed dependencies of the entry assembly are
    // resolved by the NATIVE hostfxr/hostpolicy binder, which the CLR
    // consults BEFORE any managed AssemblyLoadContext's "is this identity
    // already loaded" check ever runs. Confirmed by eagerly loading the
    // session project's own SageFs.Core.dll via
    // AssemblyLoadContext.Default.LoadFromAssemblyPath as the very first
    // action in the host's `main` (before any other Core-typed code in the
    // process was JIT-compiled, verified empty via GetAssemblies()
    // immediately prior): the eager load did not throw, but the resulting
    // eval still resolved `typeof<SageFs.SageFsError>.Assembly.Location` to
    // the host's own TPA path, and the "adopted" assembly never appeared in
    // AppDomain.CurrentDomain.GetAssemblies() at all.
    //
    // The fix instead happens BEFORE the host process exists:
    // SessionManager.startWorkerProcess (SageFs.Core/SessionManager.fs)
    // calls HostCoreAdoption.resolveLaunchRoot, which — when a session
    // project ships its own same-version build of SageFs.Core — materializes
    // a fresh PER-SESSION private copy of the shared host/ directory with
    // SageFs.Core.dll (+.pdb) substituted for the project's own build, and
    // launches the worker from THAT directory instead of the shared one.
    // The spawned process's own TPA list is then built from a directory
    // whose SageFs.Core.dll already IS the project's build — no ALC
    // trickery needed. The shared host/ directory is never mutated (that
    // would race concurrent sessions' spawns); a genuine version mismatch
    // is refused (fail-closed) before the worker is ever spawned.
    testCase "WHY — the project's own SageFs.Core wins over the host's copy, because a REPL that runs the installed tool's bits instead of the build it was asked to load cannot show new code (roast-4 #0)" <| fun _ ->
      withDogfoodSession (fun proxy ->
        match evalIn proxy "dogfood-core" "typeof<SageFs.SageFsError>.Assembly.Location;;" with
        | Error err -> failtestf "eval failed: %s" (SageFsError.describe err)
        | Ok output ->
          let normalized = output.Replace('\\', '/')
          // Evidence that HostCoreAdoption.materialize actually ran: the
          // REPL's SageFs.Core resolves out of the per-session private host
          // directory the daemon materializes for a project that ships its
          // own build, not out of the shared/original host closure.
          normalized.Contains "sagefs-host-adopt-"
          |> Expect.isTrue (sprintf "SageFs.Core must resolve to the per-session materialized private host copy (see HostCoreAdoption), got: %s" output))

    testCase "WHY — the SageFs.Tests namespace is reachable by name, because warmup auto-open must never let a referenced assembly's union case (PaneId.Tests) shadow a namespace of the project being developed (roast-4 #0)" <| fun _ ->
      withDogfoodSession (fun proxy ->
        evalIn proxy "dogfood-ns" "SageFs.Tests.EvalTimelineTests.evalTimelineTests |> ignore;;"
        |> Result.mapError SageFsError.describe
        |> Expect.isOk "SageFs.Tests.EvalTimelineTests resolves as a namespace path, not as PaneId.Tests")

    // F5b Wave 2: the self-host staleness signal, proven end-to-end against a
    // REAL adoption rather than synthetic inputs. A session on SageFs.Tests
    // adopts the project's own SageFs.Core at spawn (HostCoreAdoption), and the
    // daemon records that adopted build's identity on the ManagedSession. When
    // a newer build later lands on disk, the freshness decision must flip
    // Current -> Stale so get_fsi_status can tell the agent to hard-reset. The
    // "newer build on disk" is simulated by bumping the candidate's write time
    // and restoring it in a finally, so the repo's build output is left
    // byte-identical.
    testCase "WHY — a real self-host session records its adopted SageFs.Core and reports Current, then Stale once a newer build lands on disk, because a self-hosting agent must be told when its REPL is running code the disk has moved past (F5b)" <| fun _ ->
      use cts = new CancellationTokenSource(240_000)
      let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
      let created =
        mgr.PostAndAsyncReply(fun reply ->
          SageFs.SessionManager.SessionCommand.CreateSession(
            [ testsProject ], testsDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
        |> Async.RunSynchronously
      match created with
      | Error err -> failtestf "create failed: %s" (SageFsError.describe err)
      | Ok info ->
        try
          mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.AwaitReady(info.Id, reply))
          |> Async.RunSynchronously
          |> Expect.isOk "the SageFs.Tests session reaches Ready"
          let session =
            mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.GetSession(info.Id, reply))
            |> Async.RunSynchronously
          match session with
          | None -> failtest "session vanished after Ready"
          | Some s ->
            // (a) A real adoption was captured at spawn.
            s.AdoptedCore
            |> Expect.isSome "a session on SageFs.Tests adopts its own SageFs.Core, so AdoptedCore is recorded"
            // (b) At spawn, the adopted build IS the newest on disk -> Current.
            let newestAtSpawn = HostCoreAdoption.newestCandidateIdentity s.Projects
            newestAtSpawn |> Expect.isSome "the project ships a SageFs.Core build on disk"
            HostCoreAdoption.selfHostFreshness s.AdoptedCore newestAtSpawn
            |> Expect.equal "a freshly adopted build is Current" HostCoreAdoption.SelfHostFreshness.Current
            // (c) Simulate a rebuild landing: bump the candidate's write time
            // past the epsilon, then restore it so the build output is unchanged.
            match HostCoreAdoption.findCandidates (HostCoreAdoption.projectDirsOf s.Projects) with
            | [] -> failtest "no SageFs.Core candidate found on disk for the self-host session"
            | candidate :: _ ->
              let original = File.GetLastWriteTimeUtc candidate
              try
                File.SetLastWriteTimeUtc(candidate, original.AddSeconds 30.0)
                let newestAfterRebuild = HostCoreAdoption.newestCandidateIdentity s.Projects
                let freshness = HostCoreAdoption.selfHostFreshness s.AdoptedCore newestAfterRebuild
                match freshness with
                | HostCoreAdoption.SelfHostFreshness.Stale _ ->
                  HostCoreAdoption.formatFreshnessAffordance freshness
                  |> Expect.isSome "a newer build on disk surfaces the actionable staleness affordance"
                | other -> failtestf "expected Stale after a newer build landed on disk, got %A" other
              finally
                File.SetLastWriteTimeUtc(candidate, original)
        finally
          mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.StopSession(info.Id, reply))
          |> Async.RunSynchronously
          |> ignore
  ]
