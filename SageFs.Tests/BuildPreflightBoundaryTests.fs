module SageFs.Tests.BuildPreflightBoundaryTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Server.DaemonMode

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private tempDir () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-preflight-boundary-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory(dir) |> ignore
  dir

[<Tests>]
let tests =
  testList "BuildPreflight session-create boundary" [
    testTask "NeedsRebuild returns before posting CreateSession to the mailbox" {
      let dir = tempDir ()
      let project = Path.Combine(dir, "App.fsproj")
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>")
      let mutable posted = 0
      let mailbox =
        MailboxProcessor<SessionManager.SessionCommand>.Start(fun inbox ->
          let rec loop () = async {
            let! command = inbox.Receive()
            let isCreate =
              match command with
              | SessionManager.SessionCommand.CreateSession _ -> true
              | _ -> false
            if isCreate then
              Interlocked.Increment(&posted) |> ignore
            return! loop ()
          }
          loop ())
      use manifest = Features.ManifestOwner.start silentLogger dir
      let ops = createSessionOps mailbox (fun () -> SessionManager.QuerySnapshot.empty) manifest
      try
        let create = ops.CreateSession [ SessionProjectTarget.Project project ] dir WorkflowTypes.SessionWorkflow.Interactive
        let! completed = Task.WhenAny(create, Task.Delay 2000)
        if not (obj.ReferenceEquals(completed, create :> Task)) then
          failtest "session create reached the mailbox instead of returning NeedsRebuild"
        let! result = create
        match result with
        | Error (SageFsError.NeedsRebuild missing) ->
          missing |> Expect.isNonEmpty "the error names exact missing generated state"
        | other -> failtestf "expected NeedsRebuild, got %A" other
        posted |> Expect.equal "CreateSession is never posted" 0
      finally
        (mailbox :> IDisposable).Dispose()
        try Directory.Delete(dir, true) with _ -> ()
    }
    testTask "recovery success posts CreateSession only after generated state exists" {
      let dir = tempDir ()
      let project = Path.Combine(dir, "App.fsproj")
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>")
      let mutable posted = 0
      let mutable recoveryCalls = 0
      let info : SessionInfo = {
        Id = SessionId.newId(); Name = None; Projects = [ "App.fsproj" ]
        WorkingDirectory = dir; SolutionRoot = None
        Status = SessionLifecycleStatus.Starting { Pid = 123; Port = None }
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
        ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning }
      let mailbox =
        MailboxProcessor<SessionManager.SessionCommand>.Start(fun inbox ->
          let rec loop () = async {
            let! command = inbox.Receive()
            match command with
            | SessionManager.SessionCommand.CreateSession(_, _, _, _, reply) ->
              Interlocked.Increment(&posted) |> ignore
              reply.Reply(Ok info)
            | _ -> ()
            return! loop ()
          }
          loop ())
      use manifest = Features.ManifestOwner.start silentLogger dir
      let recover =
        Some(fun (_targets) (_dir) -> task {
          Interlocked.Increment(&recoveryCalls) |> ignore
          Directory.CreateDirectory(Path.Combine(dir, "obj")) |> ignore
          File.WriteAllText(Path.Combine(dir, "obj", "project.assets.json"), "{}")
          File.WriteAllText(Path.Combine(dir, "obj", "App.fsproj.nuget.g.props"), "<Project />")
          return Result.Ok ()
        })
      let ops = createSessionOpsWithRecovery mailbox (fun () -> SessionManager.QuerySnapshot.empty) manifest recover
      try
        let! result = ops.CreateSession [ SessionProjectTarget.Project project ] dir WorkflowTypes.SessionWorkflow.Interactive
        result |> Expect.isOk "recovery success creates the session"
        recoveryCalls |> Expect.equal "recovery runs once" 1
        posted |> Expect.equal "create is posted only after recovery" 1
      finally
        (mailbox :> IDisposable).Dispose()
        try Directory.Delete(dir, true) with _ -> ()
    }

    testTask "recovery failure returns BuildFailed without posting CreateSession" {
      let dir = tempDir ()
      let project = Path.Combine(dir, "App.fsproj")
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>")
      let mutable posted = 0
      let mailbox =
        MailboxProcessor<SessionManager.SessionCommand>.Start(fun inbox ->
          let rec loop () = async {
            let! command = inbox.Receive()
            match command with
            | SessionManager.SessionCommand.CreateSession _ -> Interlocked.Increment(&posted) |> ignore
            | _ -> ()
            return! loop ()
          }
          loop ())
      use manifest = Features.ManifestOwner.start silentLogger dir
      let expected = SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "error FS0001: recovery failed" ])
      let recover = Some(fun _ _ -> Task.FromResult(Result.Error expected))
      let ops = createSessionOpsWithRecovery mailbox (fun () -> SessionManager.QuerySnapshot.empty) manifest recover
      try
        let! result = ops.CreateSession [ SessionProjectTarget.Project project ] dir WorkflowTypes.SessionWorkflow.Interactive
        result |> Expect.equal "the build error is preserved" (Result.Error expected)
        posted |> Expect.equal "failed recovery never posts create" 0
      finally
        (mailbox :> IDisposable).Dispose()
        try Directory.Delete(dir, true) with _ -> ()
    }

    testTask "successful recovery with missing post-build outputs still fails closed" {
      let dir = tempDir ()
      let project = Path.Combine(dir, "App.fsproj")
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>")
      let mutable posted = 0
      let mailbox =
        MailboxProcessor<SessionManager.SessionCommand>.Start(fun inbox ->
          let rec loop () = async {
            let! command = inbox.Receive()
            match command with
            | SessionManager.SessionCommand.CreateSession _ -> Interlocked.Increment(&posted) |> ignore
            | _ -> ()
            return! loop ()
          }
          loop ())
      use manifest = Features.ManifestOwner.start silentLogger dir
      let recover = Some(fun _ _ -> Task.FromResult(Result.Ok ()))
      let ops = createSessionOpsWithRecovery mailbox (fun () -> SessionManager.QuerySnapshot.empty) manifest recover
      try
        let! result = ops.CreateSession [ SessionProjectTarget.Project project ] dir WorkflowTypes.SessionWorkflow.Interactive
        match result with
        | Error (SageFsError.NeedsRebuild missing) -> missing |> Expect.isNonEmpty "postcondition names the missing state"
        | other -> failtestf "expected NeedsRebuild after an empty recovery, got %A" other
        posted |> Expect.equal "missing post-build outputs never post create" 0
      finally
        (mailbox :> IDisposable).Dispose()
        try Directory.Delete(dir, true) with _ -> ()
    }
  ]