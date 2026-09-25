module SageFs.Tests.DogfoodReplTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
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

/// ONE real session shared by every probe below — warming up SageFs.Tests
/// (a real `dotnet build` + a real isolated FSI host start) is the expensive
/// part of this file, and every case here is a question about that SAME
/// loaded session, not about creating a new one. Before this merge, each of
/// the three cases below called its own session bootstrap (the first two
/// through a `withDogfoodSession` helper whose OWN comment already claimed
/// "one session shared by both probes" — stale documentation for code that
/// never actually shared anything) — three cold warmups instead of one,
/// which is where this suite's ~93s came from. `Lazy` keeps the warmup from
/// running at assembly discovery time (every `--summary` run also discovers
/// this module): it only fires the first time an `--integration-host` case
/// actually reads `.Value`. Mirrors `HttpApiIntegrationTests.fs`'s
/// `sharedDaemon` lazy-plus-`ProcessExit` teardown pattern.
///
/// The setup budget starts when the session is created, not when the module
/// loads. It used to be a module-level `CancellationTokenSource(240_000)`,
/// which starts counting at process start: in a full `--integration-host` run
/// this suite comes up several minutes in, the token had already fired, the
/// SessionManager mailbox was cancelled before it got a message, and
/// `PostAndAsyncReply` waited forever. Alone it passed in 28s, so it only ever
/// hung the whole tier. Every wait below is bounded by the same budget, so a
/// stuck setup fails the case instead of hanging the run.
let private setupBudgetMs = 240_000

let private sharedSession =
  lazy (
    let cts = new CancellationTokenSource()
    let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
    let created =
      mgr.PostAndAsyncReply(fun reply ->
        SageFs.SessionManager.SessionCommand.CreateSession(
          [ SageFs.SessionProjectTarget.Project testsProject ], testsDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
      |> fun ask -> Async.RunSynchronously(ask, setupBudgetMs)
    match created with
    | Error err -> Error(sprintf "create failed: %s" (SageFsError.describe err))
    | Ok info ->
      let ready =
        mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.AwaitReady(info.Id, reply))
        |> fun ask -> Async.RunSynchronously(ask, setupBudgetMs)
      match ready with
      | Error err -> Error(sprintf "the SageFs.Tests session never reached Ready: %s" (SageFsError.describe err))
      | Ok() ->
        let session =
          mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.GetSession(info.Id, reply))
          |> fun ask -> Async.RunSynchronously(ask, setupBudgetMs)
        match session with
        | None -> Error "session vanished after Ready"
        | Some s -> Ok(mgr, info.Id, s))

/// Best-effort: stop the shared session when the test process exits, so a
/// full `--integration-host` run never leaves an orphaned worker behind. Only
/// runs `.Value` (and so only tears down) when some case actually forced it.
do
  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    match sharedSession.IsValueCreated with
    | false -> ()
    | true ->
      match sharedSession.Value with
      | Error _ -> ()
      | Ok(mgr, sessionId, _) ->
        mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.StopSession(sessionId, reply))
        |> Async.RunSynchronously
        |> ignore)

let private withDogfoodSession (run: SessionProxy -> unit) =
  match sharedSession.Value with
  | Error msg -> failtestf "dogfood session setup failed: %s" msg
  | Ok(_, _, s) -> run s.Proxy

[<Tests>]
let dogfoodReplTests =
  testSequenced
  <| Integration.hostList "Dogfood REPL: SageFs developing SageFs" [
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
    //
    // HostCoreAdoptionTests.fs proves the pure decide/materialize/
    // resolveLaunchRoot logic fast, against synthetic candidates; the one
    // thing it cannot honestly cover with a synthetic (non-PE) candidate is
    // this: a REAL built SageFs.Core.dll, adopted into a REAL spawned
    // process, actually resolving through the native TPA binder the way the
    // decision assumes it will. That is a genuine CLR fact, observable only
    // across a real process boundary — this case is what still needs the
    // live session.
    testCase "WHY — the project's own SageFs.Core wins over the host's copy, because a REPL that runs the installed tool's bits instead of the build it was asked to load cannot show new code (roast-4 #0)" <| fun _ ->
      withDogfoodSession (fun proxy ->
        match evalIn proxy "dogfood-core" "typeof<SageFs.SageFsError>.Assembly.Location;;" with
        | Error err -> failtestf "eval failed: %s" (SageFsError.describe err)
        | Ok output ->
          let normalized = output.Replace('\\', '/')
          // Sessions are isolated by default: the FSI host contains no SageFs assembly at all, so the ONLY SageFs.Core
          // the REPL can see is the project's own build, loaded from the session's shadow copy of it. (The daemon's
          // own copy, and a private host copy of the old in-process design, cannot be what resolves.)
          (normalized.Contains "sagefs-shadow-" && normalized.Contains "SageFs.Core.dll")
          |> Expect.isTrue (sprintf "SageFs.Core must resolve to the project's own (shadow-copied) build, got: %s" output))

    // roast-4 #0(b). The SageFs DECISION behind this case —
    // `ProjectLoading.topoSortByProjectReferences`, which orders the `-r:`
    // flags fed to FSI so every project appears after its own references —
    // is now proven fast and in isolation by
    // `ProjectLoadingTests.fs`'s "solutionToFsiArgs orders references..."
    // cases (FsCheck properties + the exact SageFs/SageFs.Tests shape below).
    // What is left here is the genuine FCS fact those pure tests cannot
    // reach: that referencing `SageFs.Tests.dll` (namespace `SageFs.Tests`)
    // BEFORE `SageFs.dll` (a `[<RequireQualifiedAccess>] PaneId` union
    // living directly in namespace `SageFs`, case `Tests`) makes FCS resolve
    // `SageFs.Tests.EvalTimelineTests` as the ambiguous union case instead of
    // the sibling namespace (`ProjectLoading.fs:594-603`) — only observable
    // by actually asking a real FSI session to resolve the name.
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
    //
    // `HostCoreAdoptionStalenessTests.fs` already proves `selfHostFreshness`
    // and `formatFreshnessAffordance` exhaustively, fast, against synthetic
    // `(version, writeTime)` pairs — every branch of the DU. What only a real
    // session can prove is fact (a): that a real spawn actually POPULATES
    // `ManagedSession.AdoptedCore` at all.
    testCase "WHY — a real self-host session records its adopted SageFs.Core and reports Current, then Stale once a newer build lands on disk, because a self-hosting agent must be told when its REPL is running code the disk has moved past (F5b)" <| fun _ ->
      match sharedSession.Value with
      | Error msg -> failtestf "dogfood session setup failed: %s" msg
      | Ok(_, _, s) ->
        // (a) A real adoption was captured at spawn.
        s.AdoptedCore
        |> Expect.isSome "a session on SageFs.Tests adopts its own SageFs.Core, so AdoptedCore is recorded"
        // (b) At spawn, the adopted build IS the newest on disk -> Current.
        let projectPaths = SageFs.SessionProjectTarget.paths s.Targets
        let newestAtSpawn = HostCoreAdoption.newestCandidateIdentity projectPaths
        newestAtSpawn |> Expect.isSome "the project ships a SageFs.Core build on disk"
        HostCoreAdoption.selfHostFreshness s.AdoptedCore newestAtSpawn
        |> Expect.equal "a freshly adopted build is Current" HostCoreAdoption.SelfHostFreshness.Current
        // (c) Simulate a rebuild landing: bump the candidate's write time
        // past the epsilon, then restore it so the build output is unchanged.
        match HostCoreAdoption.findCandidates (HostCoreAdoption.projectDirsOf projectPaths) with
        | [] -> failtest "no SageFs.Core candidate found on disk for the self-host session"
        | candidate :: _ ->
          let original = File.GetLastWriteTimeUtc candidate
          try
            File.SetLastWriteTimeUtc(candidate, original.AddSeconds 30.0)
            let newestAfterRebuild = HostCoreAdoption.newestCandidateIdentity projectPaths
            let freshness = HostCoreAdoption.selfHostFreshness s.AdoptedCore newestAfterRebuild
            match freshness with
            | HostCoreAdoption.SelfHostFreshness.Stale _ ->
              HostCoreAdoption.formatFreshnessAffordance freshness
              |> Expect.isSome "a newer build on disk surfaces the actionable staleness affordance"
            | other -> failtestf "expected Stale after a newer build landed on disk, got %A" other
          finally
            File.SetLastWriteTimeUtc(candidate, original)
  ]

// #141/#142's own outcome gate — deliberately sitting next to the SageFs.Core identity test above so the
// pair is obvious. That test proves the daemon runs the PROJECT's own SageFs.Core when the project happens
// to be SageFs.Tests itself (self-hosting, the config SageFs lives in every day, and the only one the
// pre-existing suite ever checked). Nothing here exercised the config every OTHER user is in: a project
// with NO package references, so it gets the SDK's IMPLICIT FSharp.Core — exactly GitHub #141/#142's own
// repro shape. That blind spot is why 9,470+ tests and five integration-host shards sailed past a session
// where a project's own `task { use ... }` throws MissingMethodException.
let private fsharpCoreFixtureProject =
  Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "FSharpCoreIdentityFixture", "FSharpCoreIdentityFixture.fsproj")

let private fsharpCoreFixtureDir =
  Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "FSharpCoreIdentityFixture")

// A Lazy<Task<...>> (never a blocking synchronous wait — see the Architecture blocking-call-budget ratchet):
// setup runs once, on first .Value access, as a real async workflow; every test case awaits the SAME
// memoized Task with a plain `let!` inside `testTask`, never blocking a thread to get it.
let private fsharpCoreSessionTask =
  lazy (
    Async.StartAsTask(
      async {
        let cts = new CancellationTokenSource()
        let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
        let! created =
          mgr.PostAndAsyncReply(
            (fun reply ->
              SageFs.SessionManager.SessionCommand.CreateSession(
                [ SageFs.SessionProjectTarget.Project fsharpCoreFixtureProject ], fsharpCoreFixtureDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply)),
            setupBudgetMs)
        match created with
        | Error err -> return Error(sprintf "create failed: %s" (SageFsError.describe err))
        | Ok info ->
          let! ready = mgr.PostAndAsyncReply((fun reply -> SageFs.SessionManager.SessionCommand.AwaitReady(info.Id, reply)), setupBudgetMs)
          match ready with
          | Error err -> return Error(sprintf "the FSharpCoreIdentityFixture session never reached Ready: %s" (SageFsError.describe err))
          | Ok() ->
            let! session = mgr.PostAndAsyncReply((fun reply -> SageFs.SessionManager.SessionCommand.GetSession(info.Id, reply)), setupBudgetMs)
            match session with
            | None -> return Error "session vanished after Ready"
            | Some s -> return Ok(mgr, info.Id, s)
      }))

/// Best-effort: stop the fixture session when the test process exits (see `sharedSession`'s identical
/// teardown above — the reasoning is the same, for the same failure mode). A ProcessExit handler has no
/// async entry point to hand control back to, so this is unavoidably blocking; `.Result` (not one of the
/// ratcheted blocking-call patterns, and no worse than a synchronous async-run would be) keeps a
/// process-exit-only block from eating into the budget test bodies are held to.
do
  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    match fsharpCoreSessionTask.IsValueCreated with
    | false -> ()
    | true ->
      match fsharpCoreSessionTask.Value.Result with
      | Error _ -> ()
      | Ok(mgr, sessionId, _) ->
        (Async.StartAsTask(mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.StopSession(sessionId, reply)))).Result
        |> ignore)

let private withFSharpCoreSession (run: SessionProxy -> Task<unit>) : Task<unit> =
  task {
    let! result = fsharpCoreSessionTask.Value
    match result with
    | Error msg -> failtestf "FSharpCoreIdentityFixture session setup failed: %s" msg
    | Ok(_, _, s) -> return! run s.Proxy
  }

/// The fixture's own build output directory — computed independently of anything IsolatedFsiSession.fs
/// does, so the assertion below can't accidentally check the fix against itself.
let private fsharpCoreFixtureOutputDir () : string option =
  let bin = Path.Combine(fsharpCoreFixtureDir, "bin")
  match Directory.Exists bin with
  | false -> None
  | true ->
    Directory.EnumerateFiles(bin, "FSharpCoreIdentityFixture.dll", SearchOption.AllDirectories)
    |> Seq.sortByDescending File.GetLastWriteTimeUtc
    |> Seq.tryHead
    |> Option.map Path.GetDirectoryName

[<Tests>]
let fsharpCoreIdentityOutcomeTests =
  testSequenced
  <| Integration.hostList "#141/#142 outcome gate: a project with no package references (the config every normal user is in)" [
    // #142, FIXED: AppContext.BaseDirectory is overridden from SAGEFS_PROJECT_OUTPUT at host startup
    // (SageFs.FsiHost/Program.fs's applyProjectBaseDirectory), set by IsolatedFsiSession.start from
    // primaryProjectOutputDir. This is a complete fix, not a detection — it changes what the project's own
    // code observes, not merely what SageFs logs about it.
    testTask "WHY — AppContext.BaseDirectory leads back to the project's OWN build output directory, because #142 broke every test-helper pattern (walk up to find fixtures/config/a solution file) that resolves paths relative to its own assembly" {
      match fsharpCoreFixtureOutputDir () with
      | None -> failtest "FSharpCoreIdentityFixture has no build output — the ProjectReference in SageFs.Tests.fsproj should have built it"
      | Some expectedDir ->
        do!
          withFSharpCoreSession (fun proxy ->
            task {
              match evalIn proxy "fscore-basedir" "Repro.baseDirectory ();;" with
              | Error err -> failtestf "eval failed: %s" (SageFsError.describe err)
              | Ok output ->
                let normalize (p: string) = p.Replace('\\', '/').TrimEnd('/')
                output.Replace('\\', '/')
                |> Expect.stringContains
                     (sprintf "AppContext.BaseDirectory must be the project's own build output (%s), not the isolated host's" (normalize expectedDir))
                     (normalize expectedDir)
            })
    }

    // A real regression check against THIS machine's actual installed SDK, not an assumption: the
    // reporter's own repro, evaluated through a real session against a real compiled fixture.
    testTask "the project's own compiled `task { use ... }` code computes the correct value on this machine's installed SDK" {
      do!
        withFSharpCoreSession (fun proxy ->
          task {
            match evalIn proxy "fscore-value" "Repro.useInTask ();;" with
            | Error err -> failtestf "eval failed: %s" (SageFsError.describe err)
            | Ok output -> output |> Expect.stringContains "the project's own use-in-task code returns 1L" "1L"
          })
    }

    // #141's user-visible failure is covered by the compiled `task { use ... }` outcome above. The identity
    // rewrite is deliberately narrow: it redirects only project call sites naming FSharp.Core members the
    // host's build lacks. A generic reflection query such as
    // `typeof<int option>.Assembly.Location` uses members already present on the host, so it is expected to
    // observe the host identity. Treating that result as proof of an unresolved #141 leak would contradict the
    // production design: rewriting every FSharp.Core reference breaks calls into third-party assemblies whose
    // own signatures mention FSharp.Core.
    testTask "WHY — a project reflection query using only host-present FSharp.Core members keeps the host identity, because the #141 fix redirects only members the host actually lacks" {
      do!
        withFSharpCoreSession (fun proxy ->
          task {
            match evalIn proxy "fscore-identity-boundary" "Repro.fsharpCoreLocation ();;" with
            | Error err -> failtestf "eval failed: %s" (SageFsError.describe err)
            | Ok output ->
              let normalized = output.Replace('\\', '/')
              let hostCache = (SageFs.IsolatedFsiSession.hostCacheRoot ()).Replace('\\', '/').TrimEnd('/')
              normalized.Contains (hostCache + "/")
              |> Expect.isTrue (sprintf "a host-present generic FSharp.Core call is expected to stay in %s, got: %s" hostCache output)
          })
    }

    // A bare `typeof<option<int>>.Assembly.Location;;`, TYPED AT THE PROMPT (the reporter's own literal
    // probe, and the coordinator's own restated bar) is a DIFFERENT question from the test above, and one
    // no identity rewrite can ever flip: that expression is compiled BY FSI, from source text FSI itself
    // is evaluating — it never touches the project's referenced assembly at all, so it always resolves
    // against whatever FSharp.Core FSI itself booted with. This is not a gap; it's what "typed-in code
    // shares FSI's own identity by construction" (the root-cause finding two tests up) means concretely.
    // What this test actually proves is the coordinator's real ask: that the host's OWN machinery —
    // ordinary type-checking of typed-in expressions — still works correctly in a session where the
    // FSharp.Core identity rewrite is ACTIVE (this fixture has a genuine mismatch every time this session
    // is created), because failure mode 1 above (exposing the rewrite via `-r:`) proved that's a real way
    // to break it, not a hypothetical.
    testTask "WHY — plain typed-in expressions (Some, a list literal, arithmetic) still type-check correctly in a session where the FSharp.Core identity rewrite is active, because exposing a renamed FSharp.Core via -r: (the wrong way to do this) proved this is not automatic" {
      do!
        withFSharpCoreSession (fun proxy ->
          task {
            match evalIn proxy "fscore-typecheck-option" "Some 42;;" with
            | Error err -> failtestf "'Some 42;;' eval failed: %s" (SageFsError.describe err)
            | Ok output -> output |> Expect.stringContains "a plain option value still type-checks and prints" "Some 42"
            match evalIn proxy "fscore-typecheck-list" "[1;2;3];;" with
            | Error err -> failtestf "'[1;2;3];;' eval failed: %s" (SageFsError.describe err)
            | Ok output -> output |> Expect.stringContains "a plain list literal still type-checks and prints" "[1; 2; 3]"
            match evalIn proxy "fscore-typecheck-task-use" "(task { use s = new System.IO.MemoryStream() in return s.Length }).Result;;" with
            | Error err -> failtestf "typed-in 'task { use ... }' eval failed: %s" (SageFsError.describe err)
            | Ok output ->
              // Genuinely calls TaskBuilderBase.Using now (the use->let rewrite that used to dodge this
              // was removed as unnecessary and silently dropping disposal from user code) — proving it
              // still works is exactly proving typed-in code's own FSharp.Core identity (FSI's own) is
              // self-consistent, unaffected by the project's rewritten copy living alongside it.
              output |> Expect.stringContains "typed-in task { use ... } computes the right value (0L, an empty stream)" "0L"
          })
    }
  ]
