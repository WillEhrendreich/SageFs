module SageFs.Tests.AppRunnerTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppRun

let private freePort () =
  use l = new TcpListener(IPAddress.Loopback, 0)
  l.Start()
  (l.LocalEndpoint :?> IPEndPoint).Port

let private tempProject () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-apprunner", Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  Path.Combine(dir, "App.fsproj")

let private timeouts : AppRunner.StartTimeouts =
  { HostAppearGrace = TimeSpan.FromMilliseconds 500.
    HostStartTimeout = TimeSpan.FromSeconds 20. }

let private noEnv : AppRunner.SetEnv = fun _ -> ()

let private plan (project: string) = planLaunch project LaunchConfig.NoProfile

let private serving (body: string) : AppRunner.EntryPoint =
  { Name = "Web.Program.main"
    Invoke = fun args ->
      let app = WebApplication.CreateBuilder(args).Build()
      app.MapGet("/", Func<string>(fun () -> body)) |> ignore
      app.Run()
      0 }

let private primaryUrl (state: AppRunState) =
  match state with
  | AppRunState.Running { Endpoint = AppEndpoint.Http (url, _) } -> url
  | other -> failtestf "expected a running web app, got %A" other

let private getBody (url: string) = task {
  use client = new HttpClient(Timeout = TimeSpan.FromSeconds 5.)
  return! client.GetStringAsync(url)
}

[<Tests>]
let entryPointTests =
  testList "AppRunner entryPointOf" [
    testCase "WHY — AppRunner.entryPointOf — reads the compiler's entry point because scanning source guessed wrong" <| fun _ ->
      match AppRunner.entryPointOf (Assembly.GetExecutingAssembly()) with
      | Ok entry -> entry.Name |> Expect.equal "declaring type + method (this project's entry module is Program)" "Program.main"
      | Error e -> failtestf "expected the test assembly's entry point, got %s" e

    testCase "WHY — AppRunner.entryPointOf — a library has no entry point and says so because Run App needs an Exe" <| fun _ ->
      match AppRunner.entryPointOf typeof<RunRequest>.Assembly with
      | Error e -> e |> Expect.stringContains "names the assembly" "SageFs.Core"
      | Ok entry -> failtestf "a library must not have an entry point, got %s" entry.Name
  ]

[<Tests>]
let appRunnerTests =
  testList "AppRunner" [
    testTask "WHY — AppRunner.start — reports the address the app really listens on because the dashboard links to it" {
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! state = AppRunner.start runner project (serving "hello from the app") (plan project)
      let url = primaryUrl state
      url |> Expect.stringStarts "a free loopback port, not the 5000 default" "http://127.0.0.1:"
      let! body = getBody url
      body |> Expect.equal "the app's own response" "hello from the app"
    }

    testTask "WHY — AppRunner.start — respects a URL pinned in the app's code because the app's own choice wins" {
      use runner = AppRunner.create timeouts noEnv
      let port = freePort ()
      let pinned : AppRunner.EntryPoint =
        { Name = "Web.Program.main"
          Invoke = fun args ->
            let app = WebApplication.CreateBuilder(args).Build()
            app.MapGet("/", Func<string>(fun () -> "pinned")) |> ignore
            app.Run(sprintf "http://127.0.0.1:%d" port)
            0 }
      let project = tempProject ()
      let! state = AppRunner.start runner project pinned (plan project)
      primaryUrl state |> Expect.equal "the pinned url" (sprintf "http://127.0.0.1:%d" port)
    }

    testTask "WHY — AppRunner.start — applies the launch environment before the entry point runs because the app reads it at startup" {
      let log = ConcurrentQueue<string>()
      let recordEnv : AppRunner.SetEnv = fun vars -> for (k, v) in vars do log.Enqueue(sprintf "env %s=%s" k v)
      use runner = AppRunner.create timeouts recordEnv
      let project = tempProject ()
      let entry : AppRunner.EntryPoint = { Name = "Tool.main"; Invoke = fun _ -> log.Enqueue "invoked"; 0 }
      let! _ = AppRunner.start runner project entry (plan project)
      let entries = log.ToArray() |> Array.toList
      let invokedAt = entries |> List.findIndex (fun e -> e = "invoked")
      let contentRootAt = entries |> List.findIndex (fun e -> e.StartsWith("env ASPNETCORE_CONTENTROOT=", StringComparison.Ordinal))
      (contentRootAt < invokedAt) |> Expect.isTrue "the content root is set before invoking the entry point"
    }

    testTask "WHY — AppRunner.start — a startup exception is Crashed with the app's own message because the user must see why their app failed" {
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let failing : AppRunner.EntryPoint =
        { Name = "Web.Program.main"; Invoke = fun _ -> raise (InvalidOperationException "Missing connection string 'Db'") }
      let! state = AppRunner.start runner project failing (plan project)
      match state with
      | AppRunState.Crashed (_, reason, _) -> reason |> Expect.stringContains "the app's own message" "Missing connection string 'Db'"
      | other -> failtestf "expected Crashed, got %A" other
    }

    testTask "WHY — AppRunner.start — an entry point that returns without a server is Exited with its code because tools and scripts finish" {
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! state = AppRunner.start runner project { Name = "Tool.main"; Invoke = fun _ -> 3 } (plan project)
      match state with
      | AppRunState.Exited (_, code, _) -> code |> Expect.equal "the entry point's exit code" 3
      | other -> failtestf "expected Exited, got %A" other
    }

    testTask "WHY — AppRunner.start — a console app that keeps running has no server because not every executable is a web app" {
      let release = TaskCompletionSource()
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! state = AppRunner.start runner project { Name = "Worker.main"; Invoke = fun _ -> release.Task.Wait(); 0 } (plan project)
      release.SetResult()
      match state with
      | AppRunState.Running app -> app.Endpoint |> Expect.equal "no server" AppEndpoint.NoServer
      | other -> failtestf "expected Running without a server, got %A" other
    }

    testTask "WHY — AppRunner.start — starting while running returns the running app because a second host would fight for the port" {
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! first = AppRunner.start runner project (serving "one") (plan project)
      let! second = AppRunner.start runner project (serving "two") (plan project)
      match first, second with
      | AppRunState.Running a, AppRunState.Running b -> b.RunId |> Expect.equal "the same run" a.RunId
      | other -> failtestf "expected the same running app twice, got %A" other
    }

    testTask "WHY — AppRunner.stop — stops the web host and frees its port because Stop App must not kill the session" {
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! state = AppRunner.start runner project (serving "bye") (plan project)
      let url = primaryUrl state
      let! stopped = AppRunner.stop runner
      stopped |> Expect.equal "back to not running" (Ok AppRunState.NotRunning)
      let! reachable = task {
        try
          let! _ = getBody url
          return true
        with _ -> return false }
      reachable |> Expect.isFalse "the port no longer serves"
    }

    testTask "WHY — AppRunner.stop — with nothing running is a no-op because stop must be idempotent" {
      use runner = AppRunner.create timeouts noEnv
      let! stopped = AppRunner.stop runner
      stopped |> Expect.equal "still not running" (Ok AppRunState.NotRunning)
    }

    testTask "WHY — AppRunner.stop — a console app is refused with a hint because it cannot be stopped in place" {
      let release = TaskCompletionSource()
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! _ = AppRunner.start runner project { Name = "Worker.main"; Invoke = fun _ -> release.Task.Wait(); 0 } (plan project)
      let! stopped = AppRunner.stop runner
      release.SetResult()
      match stopped with
      | Error msg -> msg |> Expect.stringContains "tells the user what to do" "→"
      | Ok s -> failtestf "expected a refusal, got %A" s
    }

    testTask "WHY — AppRunner.awaitChange — completes when the app exits on its own because a dead app must not show as running" {
      use runner = AppRunner.create timeouts noEnv
      let appRef = TaskCompletionSource<WebApplication>()
      let entry : AppRunner.EntryPoint =
        { Name = "Web.Program.main"
          Invoke = fun args ->
            let app = WebApplication.CreateBuilder(args).Build()
            appRef.SetResult app
            app.Run()
            7 }
      let project = tempProject ()
      let! state = AppRunner.start runner project entry (plan project)
      let runId =
        match state with
        | AppRunState.Running app -> app.RunId
        | other -> failtestf "expected Running, got %A" other
      let! (app: WebApplication) = appRef.Task
      app.Lifetime.StopApplication()
      let! changed = AppRunner.awaitChange runner runId CancellationToken.None
      match changed with
      | AppRunState.Exited (_, code, _) -> code |> Expect.equal "the entry point's exit code" 7
      | other -> failtestf "expected Exited, got %A" other
    }

    testTask "WHY — AppRunner.start — concurrent runners each capture their own host because the hosting listener is process-global" {
      use r1 = AppRunner.create timeouts noEnv
      use r2 = AppRunner.create timeouts noEnv
      let p1 = tempProject ()
      let p2 = tempProject ()
      let! (states: AppRunState array) = Task.WhenAll<AppRunState> [| AppRunner.start r1 p1 (serving "one") (plan p1); AppRunner.start r2 p2 (serving "two") (plan p2) |]
      let! body1 = getBody (primaryUrl states.[0])
      let! body2 = getBody (primaryUrl states.[1])
      body1 |> Expect.equal "runner one serves its own app" "one"
      body2 |> Expect.equal "runner two serves its own app" "two"
    }
  ]

[<Tests>]
let requireRestartTests =
  let typeChange = SageFs.Features.ReloadPlanning.ReloadChange.TypeChanged "TodoItem"
  testList "AppRunner requireRestart" [
    testTask "WHY — AppRunner.requireRestart — ends a running web app as RestartRequired naming what changed and frees its port because the rebuilt app needs it" {
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! state = AppRunner.start runner project (serving "old code") (plan project)
      let url = primaryUrl state
      let! ended = AppRunner.requireRestart runner typeChange []
      match ended with
      | AppRunState.RestartRequired (p, first, rest, _) ->
        p |> Expect.equal "the project" project
        first :: rest |> Expect.equal "what changed" [ typeChange ]
      | other -> failtestf "expected RestartRequired, got %A" other
      let! reachable = task {
        try
          let! _ = getBody url
          return true
        with _ -> return false }
      reachable |> Expect.isFalse "the port no longer serves"
    }

    testTask "WHY — AppRunner.requireRestart — settles a waiting long-poll because the daemon learns about the restart through it" {
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! state = AppRunner.start runner project (serving "old code") (plan project)
      let runId =
        match state with
        | AppRunState.Running app -> app.RunId
        | other -> failtestf "expected Running, got %A" other
      let waiting = AppRunner.awaitChange runner runId CancellationToken.None
      let! _ = AppRunner.requireRestart runner typeChange []
      let! first = Task.WhenAny(waiting :> Task, Task.Delay(TimeSpan.FromSeconds 10.))
      (first = (waiting :> Task)) |> Expect.isTrue "the long-poll settles instead of waiting forever"
      match waiting.Result with
      | AppRunState.RestartRequired _ -> ()
      | other -> failtestf "expected the long-poll to see RestartRequired, got %A" other
    }

    testTask "WHY — AppRunner.requireRestart — with nothing running changes nothing because only a running app restarts" {
      use runner = AppRunner.create timeouts noEnv
      let! state = AppRunner.requireRestart runner typeChange []
      state |> Expect.equal "still not running" AppRunState.NotRunning
    }
  ]
