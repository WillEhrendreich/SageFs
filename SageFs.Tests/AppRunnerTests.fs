module SageFs.Tests.AppRunnerTests

open System
open System.Collections.Concurrent
open System.Diagnostics
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

module Integration = SageFs.Tests.TestInfrastructure.Integration

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

let private plan (project: string) = planLaunch project LaunchConfig.NoProfile PreviousAddress.NoPreviousAddress

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
      let! stopped = AppRunner.stop runner StopScope.CurrentApp
      stopped |> Expect.equal "back to not running" (Ok AppRunState.NotRunning)
      let! reachable = task {
        try
          let! _ = getBody url
          return true
        with _ -> return false }
      reachable |> Expect.isFalse "the port no longer serves"
    }

    testTask "WHY — AppRunner.stop — a stop for a run that is already over leaves the live app running because it must not end a run someone started since" {
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! state = AppRunner.start runner project (serving "still here") (plan project)
      let url = primaryUrl state
      let! stopped = AppRunner.stop runner (StopScope.OnlyRun "an-earlier-run")
      stopped |> Expect.equal "the live app is untouched" (Ok state)
      let! body = getBody url
      body |> Expect.equal "it still serves" "still here"
    }

    testTask "WHY — AppRunner.stop — with nothing running is a no-op because stop must be idempotent" {
      use runner = AppRunner.create timeouts noEnv
      let! stopped = AppRunner.stop runner StopScope.CurrentApp
      stopped |> Expect.equal "still not running" (Ok AppRunState.NotRunning)
    }

    testTask "WHY — AppRunner.stop — a console app is refused with a hint because it cannot be stopped in place" {
      let release = TaskCompletionSource()
      use runner = AppRunner.create timeouts noEnv
      let project = tempProject ()
      let! _ = AppRunner.start runner project { Name = "Worker.main"; Invoke = fun _ -> release.Task.Wait(); 0 } (plan project)
      let! stopped = AppRunner.stop runner StopScope.CurrentApp
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

// ─── ManagedDependencyResolution (run_app + shadow copy + NuGet deps) ──────
//
// Bug, reproduced live: `POST /api/sessions/{sid}/run-app` on the documented
// Falco demo threw `FileNotFoundException: Could not load file or assembly
// 'Falco, Version=5.2.0.0'`. Root cause, confirmed empirically (a clean
// `dotnet fsi` process with the ASP.NET Core shared framework preloaded —
// exactly `SageFs.Host.exe`'s own <FrameworkReference> shape — reproduced
// the identical exception against the real built WebappDatastar sample, and
// installing this fix's resolver made it disappear): `ShadowCopy` shadow-
// copies ONLY a project's own top-level assembly into a directory that
// contains nothing else (by design — see `ShadowCopy.shadowCopyFile`'s doc
// comment); `resolveProjectAssembly`'s `Assembly.LoadFrom` on that shadow
// copy then has no way to find the project's NuGet/project-reference
// dependencies, because .NET only probes the directory an assembly was
// loaded from.

[<Tests>]
let managedDependencyResolutionTests =
  testList "AppRunner.ManagedDependencyResolution" [

    testCase "WHY — candidatePath is <root>/<name>.dll, absolute" <| fun _ ->
      AppRunner.ManagedDependencyResolution.candidatePath "/proj/bin/Debug/net10.0" "Falco"
      |> Expect.equal "matches the real on-disk layout dotnet build produces" (Path.GetFullPath "/proj/bin/Debug/net10.0/Falco.dll")

    testCase "candidatePath normalizes a relative root to an absolute path" <| fun _ ->
      AppRunner.ManagedDependencyResolution.candidatePath "relative/dir" "Falco"
      |> Path.IsPathRooted
      |> Expect.isTrue "every candidate must be rooted, like NativeResolution's candidates"
  ]

/// Repo root, derived from this source file's own location — never a
/// hardcoded path (AGENTS.md: no hardcoded Windows/absolute paths).
let private repoRoot =
  Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

/// The exact sample the reported bug used, and the one the website's own
/// demo documents. Its build output holds the real Falco/Falco.Markup/
/// Falco.Datastar NuGet dependencies this fix must make resolvable.
let private webappDatastarSampleBinDir =
  let projectDir = Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.WebappDatastar")
  // Whatever configuration built this test binary built the sample too
  // (project reference of SageFs.Tests' own dependency chain isn't direct,
  // but CI/local dev builds both) — probe both, prefer Debug.
  [ "Debug"; "Release" ]
  |> List.map (fun cfg -> Path.Combine(projectDir, "bin", cfg, "net10.0"))
  |> List.tryFind (fun dir -> File.Exists(Path.Combine(dir, "SageFs.Samples.WebappDatastar.dll")))

[<Tests>]
let managedDependencyResolutionIntegrationTests =
  Integration.hostList "AppRunner.ManagedDependencyResolution (real sample)" [

    testCase "WHY — the shadow dir alone lacks Falco.dll (the bug's precondition)" <| fun _ ->
      match webappDatastarSampleBinDir with
      | None -> skiptest "WebappDatastar sample not built for this config"
      | Some sampleBinDir ->
        let shadowDir = SageFs.ShadowCopy.createShadowDir ()
        try
          let originalDll = Path.Combine(sampleBinDir, "SageFs.Samples.WebappDatastar.dll")
          let shadowDll = SageFs.ShadowCopy.shadowCopyFile shadowDir originalDll
          Path.GetDirectoryName shadowDll
          |> fun shadowedDir -> File.Exists(Path.Combine(shadowedDir, "Falco.dll"))
          |> Expect.isFalse "the shadow dir must contain ONLY the entry assembly — this is exactly why Assembly.LoadFrom alone fails"
        finally
          SageFs.ShadowCopy.cleanupShadowDir shadowDir

    testCase "the original build output DOES have Falco.dll beside the entry assembly" <| fun _ ->
      match webappDatastarSampleBinDir with
      | None -> skiptest "WebappDatastar sample not built for this config"
      | Some sampleBinDir ->
        File.Exists(Path.Combine(sampleBinDir, "Falco.dll"))
        |> Expect.isTrue "the fix's whole premise: dotnet build already copied every dependency here"

    // The real end-to-end proof, in a CLEAN child process: SageFs.Tests
    // itself references Falco (FalcoTests.fs, DashboardBrowserTests.fs), so
    // ANY in-process attempt to resolve "Falco" here would succeed via the
    // test process's own dependency graph regardless of whether this fix
    // works — a false positive. `Assembly.LoadFrom` alone never resolves a
    // loaded assembly's dependencies (that only happens lazily, when
    // something actually USES a member from them), so this proves the fix
    // directly by resolving "Falco" by name through the exact
    // `AssemblyLoadContext.Default` `Resolving` handler
    // `resolveProjectAssembly` installs, rather than by invoking the
    // sample's entry point (which would block forever on its own
    // `app.Run()`).
    //
    // Runs as a tiny compiled harness built via `<ProjectReference>` to the
    // REAL SageFs.Core.fsproj/SageFs.Host.fsproj — never `dotnet fsi`: this
    // repo pins FSharp.Core to a specific preview build
    // (Directory.Packages.props) independent of its net10.0 TargetFramework,
    // and plain `dotnet fsi`'s own bundled FSharp.Core (whichever SDK it
    // resolves) never matches that exact identity, so `#r`-ing SageFs.Core.dll
    // into fsi throws its own unrelated FileLoadException before the test
    // logic even runs (confirmed empirically). A ProjectReference build
    // resolves the correct version transitively via central package
    // management, exactly like the real `SageFs.Host.exe` does.
    testCase "WHY — resolveProjectAssembly makes Falco resolvable in a clean process that never referenced it, via the REAL shipped code" <| fun _ ->
      match webappDatastarSampleBinDir with
      | None -> skiptest "WebappDatastar sample not built for this config"
      | Some sampleBinDir ->
        let coreFsproj = Path.Combine(repoRoot, "SageFs.Core", "SageFs.Core.fsproj")
        let hostFsproj = Path.Combine(repoRoot, "SageFs.Host", "SageFs.Host.fsproj")
        // The harness lives OUTSIDE the repo's directory tree, so it does not
        // inherit Directory.Packages.props' central FSharp.Core pin — its own
        // implicit F#-SDK FSharp.Core reference then "wins" as the primary
        // reference over the transitive want from SageFs.Core -> FCS (NU1605
        // package downgrade, confirmed empirically), and the harness runs
        // with the WRONG FSharp.Core physically present. Pin it explicitly to
        // the exact version this repo builds SageFs.Core/SageFs.Host against.
        let fsharpCoreVersion =
          let packagesProps = Path.Combine(repoRoot, "Directory.Packages.props")
          match File.Exists packagesProps with
          | false -> None
          | true ->
            let m = Text.RegularExpressions.Regex.Match(File.ReadAllText packagesProps, "\"FSharp\\.Core\"\\s+Version=\"([^\"]+)\"")
            match m.Success with
            | true -> Some m.Groups.[1].Value
            | false -> None
        match File.Exists coreFsproj, File.Exists hostFsproj, fsharpCoreVersion with
        | false, _, _ | _, false, _ -> skiptest "SageFs.Core.fsproj/SageFs.Host.fsproj not found"
        | _, _, None -> skiptest "could not read the repo's pinned FSharp.Core version from Directory.Packages.props"
        | true, true, Some fsharpCoreVersion ->
          let harnessDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-apprunner-harness-%s" (Guid.NewGuid().ToString("N").[..7]))
          Directory.CreateDirectory harnessDir |> ignore
          try
            let harnessFsproj = Path.Combine(harnessDir, "Harness.fsproj")
            let harnessFsprojContent =
              // DisableImplicitFSharpCoreReference: the F# SDK otherwise
              // injects its OWN default FSharp.Core reference, which wins
              // MSBuild's assembly-conflict resolution as "primary" over
              // both an explicit PackageReference here AND the transitive
              // want from SageFs.Core -> FSharp.Compiler.Service — the
              // harness would then run against the wrong FSharp.Core
              // (confirmed empirically: 10.1.0.0, not the 11.0.0.0 every
              // SageFs.Core/.Host build actually needs) despite specifying
              // the version explicitly. Disabling it leaves exactly one
              // real candidate.
              sprintf
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>\n  </PropertyGroup>\n  <ItemGroup>\n    <Compile Include=\"Program.fs\" />\n  </ItemGroup>\n  <ItemGroup>\n    <PackageReference Include=\"FSharp.Core\" Version=\"%s\" />\n  </ItemGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"%s\" />\n    <ProjectReference Include=\"%s\" />\n  </ItemGroup>\n</Project>\n"
                fsharpCoreVersion coreFsproj hostFsproj
            let harnessProgramContent =
              String.concat "\n" [
                "open System.IO"
                "open System.Reflection"
                "open System.Runtime.Loader"
                ""
                "[<EntryPoint>]"
                "let main argv ="
                "  match argv with"
                "  | [| sampleBinDir |] ->"
                "    let shadowDir = SageFs.ShadowCopy.createShadowDir ()"
                "    let originalDll = Path.Combine(sampleBinDir, \"SageFs.Samples.WebappDatastar.dll\")"
                "    let shadowDll = SageFs.ShadowCopy.shadowCopyFile shadowDir originalDll"
                "    match SageFs.AppRunner.resolveProjectAssembly [ (\"proj\", shadowDll) ] \"proj\" with"
                "    | Error e -> printfn \"RESOLVE_ERROR: %s\" e; 1"
                "    | Ok _ ->"
                "      try"
                "        let falco = AssemblyLoadContext.Default.LoadFromAssemblyName(AssemblyName(\"Falco\"))"
                "        printfn \"RESOLVED_FALCO: %s\" falco.FullName"
                "        0"
                "      with ex ->"
                "        printfn \"FALCO_FAILED: %s: %s\" (ex.GetType().FullName) ex.Message"
                "        2"
                "  | _ ->"
                "    printfn \"USAGE: harness <sampleBinDir>\""
                "    3"
              ]
            File.WriteAllText(harnessFsproj, harnessFsprojContent)
            File.WriteAllText(Path.Combine(harnessDir, "Program.fs"), harnessProgramContent)
            let runDotnet (args: string list) (timeoutSec: float) =
              let psi = ProcessStartInfo(
                FileName = "dotnet",
                WorkingDirectory = repoRoot,   // the repo's global.json SDK pin
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true)
              for a in args do psi.ArgumentList.Add a
              use p = Process.Start psi
              let stdout = p.StandardOutput.ReadToEndAsync()
              let stderr = p.StandardError.ReadToEndAsync()
              let finished = p.WaitForExitAsync().Wait(TimeSpan.FromSeconds timeoutSec)
              match finished with
              | false ->
                (try p.Kill(true) with _ -> ())
                Error (sprintf "'dotnet %s' did not finish within %.0fs" (String.concat " " args) timeoutSec)
              | true -> Ok (p.ExitCode, stdout.Result, stderr.Result)
            match runDotnet [ "build"; harnessFsproj; "-c"; "Debug"; "--nologo" ] 180.0 with
            | Error msg -> failtest msg
            | Ok (buildExit, buildOut, buildErr) when buildExit <> 0 ->
              failtestf "harness build failed (exit=%d):\n%s\n%s" buildExit buildOut buildErr
            | Ok _ ->
              let harnessDll = Path.Combine(harnessDir, "bin", "Debug", "net10.0", "Harness.dll")
              match runDotnet [ "exec"; harnessDll; sampleBinDir ] 30.0 with
              | Error msg -> failtest msg
              | Ok (_, out, err) ->
                out
                |> Expect.stringContains (sprintf "expected the fix to resolve Falco in a clean process (stderr=%s)" err) "RESOLVED_FALCO:"
          finally
            try Directory.Delete(harnessDir, true) with _ -> ()
  ]
