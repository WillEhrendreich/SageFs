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
  let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ())
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
    // PENDING, not done: roast-4 #0(a). SageFs.Host statically links SageFs.Core,
    // so the host's copy is already in the Default ALC before FSI starts and
    // the same-identity project copy dedupes to it. The fix is a host
    // bootstrapper that loads the project's copy first (tracked as its own
    // gated item); this test is un-pended by that change and must pass then.
    ptestCase "WHY — the project's own SageFs.Core wins over the host's copy, because a REPL that runs the installed tool's bits instead of the build it was asked to load cannot show new code (roast-4 #0)" <| fun _ ->
      withDogfoodSession (fun proxy ->
        match evalIn proxy "dogfood-core" "typeof<SageFs.SageFsError>.Assembly.Location;;" with
        | Error err -> failtestf "eval failed: %s" (SageFsError.describe err)
        | Ok output ->
          let normalized = output.Replace('\\', '/')
          // The host process's own closure: the installed tool's host/ dir or
          // the dev build's SageFs.Host/bin. Either means the REPL is running
          // the host's SageFs.Core, not the project's.
          (normalized.Contains "/host/" || normalized.Contains "SageFs.Host/")
          |> Expect.isFalse (sprintf "SageFs.Core must not resolve to the host's closure, got: %s" output)
          // The project's build reaches the session through its shadow copy.
          normalized.Contains "sagefs-shadow"
          |> Expect.isTrue (sprintf "SageFs.Core must be the project's shadow-copied build, got: %s" output))

    testCase "WHY — the SageFs.Tests namespace is reachable by name, because warmup auto-open must never let a referenced assembly's union case (PaneId.Tests) shadow a namespace of the project being developed (roast-4 #0)" <| fun _ ->
      withDogfoodSession (fun proxy ->
        evalIn proxy "dogfood-ns" "SageFs.Tests.EvalTimelineTests.evalTimelineTests |> ignore;;"
        |> Result.mapError SageFsError.describe
        |> Expect.isOk "SageFs.Tests.EvalTimelineTests resolves as a namespace path, not as PaneId.Tests")
  ]
