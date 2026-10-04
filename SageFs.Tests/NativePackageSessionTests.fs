module SageFs.Tests.NativePackageSessionTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// A NuGet package that carries a native library, used from a project's own compiled code, through a real isolated
/// FSI host. The roadmap asked whether the isolated host fixes the "#r a package with a native library" report; this
/// is the answer as a test. The fixture is a class library, so its build output does not hold the package's native
/// file: the file is in the NuGet cache, and the host has to be told where from the project's restore.
let repoRoot = RepoPaths.repoPathFull [||]

let fixtureDir = Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "NativeSqliteFixture")

let fixtureProject = Path.Combine(fixtureDir, "NativeSqliteFixture.fsproj")

let setupBudgetMs = TestTimeouts.asMs TestTimeouts.sessionReadyColdBuild

let evalIn (proxy: SessionProxy) (replyId: string) (code: string) : Task<Result<string, SageFsError>> =
  task {
    let! reply = proxy (WorkerMessage.EvalCode(code, replyId)) |> Async.StartAsTask
    match reply with
    | WorkerResponse.EvalResult(_, result, _, _) -> return result
    | other -> return Error(SageFsError.Unexpected(Exception(sprintf "unexpected eval response: %A" other)))
  }

/// Lazy so that discovering this module (every run does) never starts a session: the first case that runs does.
let sessionTask =
  lazy (
    Async.StartAsTask(
      async {
        let cts = new CancellationTokenSource()
        let mgr, _ = SageFs.SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
        let! created =
          mgr.PostAndAsyncReply(
            (fun reply ->
              SageFs.SessionManager.SessionCommand.CreateSession(
                [ SageFs.SessionProjectTarget.Project fixtureProject ], fixtureDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply)),
            setupBudgetMs)
        match created with
        | Error err -> return Error(sprintf "create failed: %s" (SageFsError.describe err))
        | Ok info ->
          let! ready = mgr.PostAndAsyncReply((fun reply -> SageFs.SessionManager.SessionCommand.AwaitReady(info.Id, reply)), setupBudgetMs)
          match ready with
          | Error err -> return Error(sprintf "the native fixture session never reached Ready: %s" (SageFsError.describe err))
          | Ok() ->
            let! session = mgr.PostAndAsyncReply((fun reply -> SageFs.SessionManager.SessionCommand.GetSession(info.Id, reply)), setupBudgetMs)
            match session with
            | None -> return Error "session vanished after Ready"
            | Some s -> return Ok(mgr, info.Id, s)
      }))

/// Stop the session when the test process exits, so a run never leaves a worker behind.
do
  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    match sessionTask.IsValueCreated with
    | false -> ()
    | true ->
      match sessionTask.Value.Result with
      | Error _ -> ()
      | Ok(mgr, sessionId, _) ->
        (Async.StartAsTask(mgr.PostAndAsyncReply(fun reply -> SageFs.SessionManager.SessionCommand.StopSession(sessionId, reply)))).Result
        |> ignore)

let withSession (run: SessionProxy -> Task<unit>) : Task<unit> =
  task {
    let! result = sessionTask.Value
    match result with
    | Error msg -> return failtestf "native fixture session setup failed: %s" msg
    | Ok(_, _, s) -> return! run s.Proxy
  }

[<Tests>]
let tests =
  testSequenced
  <| Integration.hostList "native package outcome gate: a project's package that carries a native library loads it in the isolated host" [

    testTask "WHY — a project that uses Microsoft.Data.Sqlite gets SQLite's real version from the native library the package carries, because the library is in the NuGet cache and the host must be told where from the project's restore (the report this answers: a package with a native library failing to load)" {
      do!
        withSession (fun proxy ->
          task {
            let! reply = evalIn proxy "native-sqlite" "Repro.sqliteVersion ();;"
            match reply with
            | Error err -> failtestf "the native library did not load: %s" (SageFsError.describe err)
            | Ok output -> output |> Expect.stringContains "SQLite answers with its version, so libe_sqlite3 loaded" "3."
          })
    }

    testTask "WHY — a native library nothing provides fails with its name, this machine's runtime and what to do, not the type initializer's bare message that hides the cause" {
      do!
        withSession (fun proxy ->
          task {
            let! reply = evalIn proxy "native-missing" "Repro.callMissingNative ();;"
            match reply with
            | Ok output -> failtestf "a library nothing provides cannot load, got: %s" output
            | Error err ->
              let said = SageFsError.describe err
              said |> Expect.stringContains "names the library" "sagefs_fixture_no_such_native_library"
              said |> Expect.stringContains "names this machine's runtime" RuntimeInformation.RuntimeIdentifier
              said |> Expect.stringContains "says what to do" "install the library on this machine"
          })
    }
  ]
