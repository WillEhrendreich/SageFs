module SageFs.Tests.AppRunTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppRun
open SageFs.ProjectLoading

let private root = Path.Combine(Path.GetTempPath(), "sagefs-apprun-tests")

let private proj (role: ProjectRole) (name: string) : ClassifiedProject =
  { Path = Path.Combine(root, name, name + ".fsproj")
    Role = role
    PackageRefs = [] }

let private web = proj ProjectRole.Executable "Web"
let private api = proj ProjectRole.Executable "Api"
let private lib = proj ProjectRole.Library "Domain"
let private tests = proj ProjectRole.Test "Domain.Tests"

let private expectTarget (expected: ClassifiedProject) (result: Result<ClassifiedProject, RunTargetError>) =
  match result with
  | Ok p -> p.Path |> Expect.equal "should resolve to the expected project" expected.Path
  | Error e -> failtestf "expected %s, got error %A" expected.Path e

let private expectError (result: Result<ClassifiedProject, RunTargetError>) =
  match result with
  | Ok p -> failtestf "expected an error, resolved %s" p.Path
  | Error e -> e

[<Tests>]
let resolveTargetTests =
  testList "AppRun resolveTarget" [
    testCase "WHY — AppRun.resolveTarget — one executable runs by default because Run App should need no configuration in the common case" <| fun _ ->
      resolveTarget RunRequest.DefaultTarget None [ lib; web; tests ]
      |> expectTarget web

    testCase "WHY — AppRun.resolveTarget — no executable is an error naming the loaded projects because the user must see why nothing can run" <| fun _ ->
      match resolveTarget RunRequest.DefaultTarget None [ lib; tests ] |> expectError with
      | RunTargetError.NoExecutableProject loaded ->
        loaded |> Expect.equal "lists the loaded projects by name" [ "Domain"; "Domain.Tests" ]
      | other -> failtestf "wrong error: %A" other

    testCase "WHY — AppRun.resolveTarget — two executables without an active project are ambiguous because guessing would start the wrong app" <| fun _ ->
      match resolveTarget RunRequest.DefaultTarget None [ web; lib; api ] |> expectError with
      | RunTargetError.AmbiguousTarget candidates ->
        candidates |> Expect.equal "lists executable candidates in load order" [ "Web"; "Api" ]
      | other -> failtestf "wrong error: %A" other

    testCase "WHY — AppRun.resolveTarget — the active project disambiguates because the user already chose it" <| fun _ ->
      resolveTarget RunRequest.DefaultTarget (Some api.Path) [ web; api ]
      |> expectTarget api

    testCase "WHY — AppRun.resolveTarget — a stale active project falls back to the single executable because a removed project must not block Run App" <| fun _ ->
      resolveTarget RunRequest.DefaultTarget (Some (Path.Combine(root, "Gone", "Gone.fsproj"))) [ web; lib ]
      |> expectTarget web

    testCase "WHY — AppRun.resolveTarget — a non-executable active project is ignored because only Exe projects have an entry point" <| fun _ ->
      resolveTarget RunRequest.DefaultTarget (Some lib.Path) [ web; lib ]
      |> expectTarget web

    testCase "WHY — AppRun.resolveTarget — names match case-insensitively because agents type project names loosely" <| fun _ ->
      resolveTarget (RunRequest.Named "api") None [ web; api ]
      |> expectTarget api

    testCase "WHY — AppRun.resolveTarget — the project file name matches because tools report projects by file name" <| fun _ ->
      resolveTarget (RunRequest.Named "Api.fsproj") None [ web; api ]
      |> expectTarget api

    testCase "WHY — AppRun.resolveTarget — the full project path matches because the dashboard sends paths" <| fun _ ->
      resolveTarget (RunRequest.Named api.Path) None [ web; api ]
      |> expectTarget api

    testCase "WHY — AppRun.resolveTarget — naming a library says why it cannot run because a bare not-found would mislead" <| fun _ ->
      match resolveTarget (RunRequest.Named "Domain") None [ web; lib ] |> expectError with
      | RunTargetError.NotExecutable (name, role) ->
        name |> Expect.equal "names the project" "Domain"
        role |> Expect.equal "reports its role" ProjectRole.Library
      | other -> failtestf "wrong error: %A" other

    testCase "WHY — AppRun.resolveTarget — an unknown name lists the runnable candidates because the user needs the valid choices" <| fun _ ->
      match resolveTarget (RunRequest.Named "Nope") None [ web; api; lib ] |> expectError with
      | RunTargetError.UnknownProject (requested, candidates) ->
        requested |> Expect.equal "echoes the request" "Nope"
        candidates |> Expect.equal "lists executables only" [ "Web"; "Api" ]
      | other -> failtestf "wrong error: %A" other

    testProperty "WHY — AppRun.resolveTarget — exactly one executable resolves as the default wherever it sits because load order must not matter" <| fun (libsBefore: byte) (libsAfter: byte) ->
      let libs n prefix = List.init (int n % 8) (fun i -> proj ProjectRole.Library (sprintf "%s%d" prefix i))
      let projects = libs libsBefore "Before" @ [ web ] @ libs libsAfter "After"
      match resolveTarget RunRequest.DefaultTarget None projects with
      | Ok p -> p.Path = web.Path
      | Error _ -> false

    testCase "WHY — AppRun.RunTargetError.describe — every error has an actionable next step because failures must say what to do" <| fun _ ->
      [ RunTargetError.NoExecutableProject [ "Domain" ]
        RunTargetError.AmbiguousTarget [ "Web"; "Api" ]
        RunTargetError.UnknownProject ("Nope", [ "Web" ])
        RunTargetError.NotExecutable ("Domain", ProjectRole.Library) ]
      |> List.iter (fun e ->
        let text = RunTargetError.describe e
        text.Contains("→") |> Expect.isTrue (sprintf "has an actionable next step: %s" text))
  ]

let private launchSettingsJson = """{
  // generated by the template
  "$schema": "http://json.schemastore.org/launchsettings.json",
  "profiles": {
    "IIS Express": {
      "commandName": "IISExpress",
      "applicationUrl": "http://localhost:1111"
    },
    "Web": {
      "commandName": "Project",
      "applicationUrl": "https://localhost:7043; http://localhost:5043",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "FEATURE_X": "on"
      },
    },
    "Second": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:9999"
    }
  }
}"""

[<Tests>]
let launchSettingsTests =
  testList "AppRun parseLaunchSettings" [
    testCase "WHY — AppRun.parseLaunchSettings — picks the first Project profile because that is what dotnet run uses" <| fun _ ->
      match parseLaunchSettings launchSettingsJson with
      | Ok (LaunchConfig.Profile p) ->
        p.Name |> Expect.equal "first commandName=Project profile" "Web"
        p.ApplicationUrls |> Expect.equal "urls split on ';' and trimmed" [ "https://localhost:7043"; "http://localhost:5043" ]
        p.EnvironmentVariables
        |> Expect.equal "env vars in document order" [ "ASPNETCORE_ENVIRONMENT", "Development"; "FEATURE_X", "on" ]
      | other -> failtestf "expected a profile, got %A" other

    testCase "WHY — AppRun.parseLaunchSettings — IIS-only settings mean no profile because IIS Express profiles cannot run in-process" <| fun _ ->
      parseLaunchSettings """{ "profiles": { "IIS": { "commandName": "IISExpress" } } }"""
      |> Expect.equal "IIS-only settings are ignored" (Ok LaunchConfig.NoProfile)

    testCase "WHY — AppRun.parseLaunchSettings — a file without profiles means no profile because launchSettings may hold only iisSettings" <| fun _ ->
      parseLaunchSettings """{ "iisSettings": {} }"""
      |> Expect.equal "missing profiles" (Ok LaunchConfig.NoProfile)

    testCase "WHY — AppRun.parseLaunchSettings — a bare profile has empty urls and env vars because those keys are optional" <| fun _ ->
      match parseLaunchSettings """{ "profiles": { "P": { "commandName": "Project" } } }""" with
      | Ok (LaunchConfig.Profile p) ->
        p.ApplicationUrls |> Expect.isEmpty "no urls"
        p.EnvironmentVariables |> Expect.isEmpty "no env vars"
      | other -> failtestf "expected a profile, got %A" other

    testCase "WHY — AppRun.parseLaunchSettings — malformed json is an error because silently ignoring it would run with the wrong settings" <| fun _ ->
      match parseLaunchSettings "{ not json" with
      | Error msg -> msg |> Expect.stringContains "says it is launchSettings" "launchSettings.json"
      | Ok other -> failtestf "expected an error, got %A" other
  ]

let private projectPath = Path.Combine(root, "Web", "Web.fsproj")
let private projectDir = Path.Combine(root, "Web")

let private profile urls env : LaunchConfig =
  LaunchConfig.Profile { Name = "Web"; ApplicationUrls = urls; EnvironmentVariables = env }

[<Tests>]
let planLaunchTests =
  testList "AppRun planLaunch" [
    testCase "WHY — AppRun.planLaunch — without a profile the content root is the project dir and the port is free because a fixed 5000 collides across sessions" <| fun _ ->
      let plan = planLaunch projectPath LaunchConfig.NoProfile PreviousAddress.NoPreviousAddress
      plan.ContentRoot |> Expect.equal "content root" projectDir
      plan.EnvironmentVariables |> Expect.equal "only the content root" [ ("ASPNETCORE_CONTENTROOT", projectDir) ]
      plan.UrlPolicy |> Expect.equal "free loopback port when the app configures none" UrlPolicy.FreeLoopbackPortIfUnset

    testCase "WHY — AppRun.planLaunch — profile urls become ASPNETCORE_URLS because the project's configured address must be respected" <| fun _ ->
      let plan = planLaunch projectPath (profile [ "http://localhost:5043"; "https://localhost:7043" ] []) PreviousAddress.NoPreviousAddress
      plan.EnvironmentVariables
      |> List.contains ("ASPNETCORE_URLS", "http://localhost:5043;https://localhost:7043")
      |> Expect.isTrue "urls joined with ';'"
      plan.UrlPolicy |> Expect.equal "project-configured" UrlPolicy.ProjectConfigured

    testCase "WHY — AppRun.planLaunch — profile env vars override ours with one entry per key because the project's own settings win" <| fun _ ->
      let plan = planLaunch projectPath (profile [] [ "ASPNETCORE_CONTENTROOT", "/elsewhere"; "X", "1" ]) PreviousAddress.NoPreviousAddress
      plan.EnvironmentVariables
      |> Expect.equal "profile wins, one entry per key" [ "ASPNETCORE_CONTENTROOT", "/elsewhere"; "X", "1" ]
      plan.ContentRoot |> Expect.equal "content root follows the override" "/elsewhere"

    testProperty "WHY — AppRun.planLaunch — planned env var keys are always distinct because a duplicate key makes the effective value order-dependent" <| fun (keys: string list) ->
      let env = keys |> List.filter (String.IsNullOrWhiteSpace >> not) |> List.map (fun k -> k, "v")
      let plan = planLaunch projectPath (profile [ "http://localhost:1" ] env) PreviousAddress.NoPreviousAddress
      let planned = plan.EnvironmentVariables |> List.map fst
      planned = List.distinct planned
  ]

[<Tests>]
let endpointTests =
  testList "AppRun endpointFromAddresses" [
    testCase "WHY — AppRun.endpointFromAddresses — no addresses means no server because console apps run without a URL" <| fun _ ->
      endpointFromAddresses [] |> Expect.equal "console-style app" AppEndpoint.NoServer

    testCase "WHY — AppRun.endpointFromAddresses — wildcard hosts become localhost because a browser cannot open [::] or *" <| fun _ ->
      [ "http://[::]:5000"; "http://0.0.0.0:5000"; "http://*:5000"; "http://+:5000" ]
      |> List.iter (fun a ->
        endpointFromAddresses [ a ]
        |> Expect.equal (sprintf "%s → localhost" a) (AppEndpoint.Http ("http://localhost:5000", [])))

    testCase "WHY — AppRun.endpointFromAddresses — http is the primary url because local dev certificates are often untrusted" <| fun _ ->
      endpointFromAddresses [ "https://localhost:7001"; "http://127.0.0.1:5001" ]
      |> Expect.equal "http primary" (AppEndpoint.Http ("http://127.0.0.1:5001", [ "https://localhost:7001" ]))

    testCase "WHY — AppRun.endpointFromAddresses — https-only apps still get a primary url because some apps only listen on https" <| fun _ ->
      endpointFromAddresses [ "https://localhost:7001" ]
      |> Expect.equal "https primary" (AppEndpoint.Http ("https://localhost:7001", []))
  ]

let private at = DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc)

let private runningWeb (endpoint: AppEndpoint) : RunningApp =
  { RunId = "r1"; Project = projectPath; EntryPoint = "Web.Program.main"; Endpoint = endpoint; StartedAt = at }

[<Tests>]
let stateViewTests =
  testList "AppRun describeState and toView" [
    testCase "WHY — AppRun.describeState — every state about a project names it because several sessions may run apps at once" <| fun _ ->
      [ AppRunState.Starting (projectPath, StartPhase.RestartingIntoWebLive, at)
        AppRunState.Starting (projectPath, StartPhase.LaunchingEntryPoint, at)
        AppRunState.Running (runningWeb (AppEndpoint.Http ("http://127.0.0.1:5123", [])))
        AppRunState.Running (runningWeb AppEndpoint.NoServer)
        AppRunState.Exited (projectPath, 3, at)
        AppRunState.Crashed (projectPath, "boom", at) ]
      |> List.iter (fun s -> describeState s |> Expect.stringContains (sprintf "%A names the project" s) "Web")

    testCase "WHY — AppRun.toView — a running web app exposes every url because clients link to them" <| fun _ ->
      let view = toView (AppRunState.Running (runningWeb (AppEndpoint.Http ("http://127.0.0.1:5123", [ "https://localhost:7001" ]))))
      view.State |> Expect.equal "state" "Running"
      view.Urls |> Expect.equal "primary first" [ "http://127.0.0.1:5123"; "https://localhost:7001" ]
      view.RunId |> Expect.equal "run id" "r1"

    testCase "WHY — AppRun.toView — a crash carries its reason in the message because clients must show why" <| fun _ ->
      let view = toView (AppRunState.Crashed (projectPath, "Missing connection string 'Db'", at))
      view.State |> Expect.equal "state" "Crashed"
      view.Message |> Expect.stringContains "the reason" "Missing connection string 'Db'"
      view.Urls |> Expect.isEmpty "no urls"
  ]

[<Tests>]
let appRunFailedWordingTests =
  testList "AppRun failure wording" [
    testCase "WHY — SageFsError — a run refused before any project was chosen names no empty project because \"Could not run ''\" reads as a broken UI" <| fun _ ->
      SageFs.SageFsError.describe (SageFs.SageFsError.AppRunFailed ("", "No runnable project in this session."))
      |> Expect.equal "the refusal must read as a sentence" "Could not run the app: No runnable project in this session."
  ]

[<Tests>]
let restartWordingTests =
  let at = System.DateTime(2026, 9, 11, 0, 0, 0, System.DateTimeKind.Utc)
  let typeChange = SageFs.Features.ReloadPlanning.ReloadChange.TypeChanged "TodoItem"
  let valueChange = SageFs.Features.ReloadPlanning.ReloadChange.ValueChanged "getHome"
  testList "AppRun restart wording" [
    testCase "WHY — AppRun.describeState — a run ended for a restart names what changed because the user must know why their app went down" <| fun _ ->
      describeState (AppRunState.RestartRequired ("/src/Web/Web.fsproj", typeChange, [ valueChange ], at))
      |> Expect.equal "names the app and each change" "Web must restart: type TodoItem changed; getHome changed (it is built at startup)"

    testCase "WHY — AppRun.describeState — rebuilding for changes names them because a long rebuild must not look like a hang" <| fun _ ->
      describeState (AppRunState.Starting ("/src/Web/Web.fsproj", StartPhase.RebuildingForChanges (typeChange, []), at))
      |> Expect.equal "names the app and the change" "Rebuilding Web: type TodoItem changed…"

    testCase "WHY — AppRun.toView — a restart-required run reports its own state name because clients switch on it" <| fun _ ->
      (toView (AppRunState.RestartRequired ("/src/Web/Web.fsproj", typeChange, [], at))).State
      |> Expect.equal "state name" "RestartRequired"
  ]

[<Tests>]
let reuseAddressTests =
  let projectPath = Path.Combine(Path.GetTempPath(), "src", "Web", "Web.fsproj")
  testList "AppRun planLaunch reuse" [
    testCase "WHY — AppRun.planLaunch — a restart reuses the previous address when the project configures none because the user's open tab must keep working" <| fun _ ->
      let plan = planLaunch projectPath LaunchConfig.NoProfile (PreviousAddress.ReuseAddress "http://127.0.0.1:5123")
      plan.EnvironmentVariables |> List.contains (urlsVar, "http://127.0.0.1:5123") |> Expect.isTrue "listens at the previous address"
      plan.UrlPolicy |> Expect.equal "the reused address is not overridden by a free port" UrlPolicy.ReusedAddress

    testCase "WHY — AppRun.planLaunch — the project's configured address wins over the previous one because the app's own choice wins" <| fun _ ->
      let config =
        LaunchConfig.Profile { Name = "Web"; ApplicationUrls = [ "http://localhost:5043" ]; EnvironmentVariables = [] }
      let plan = planLaunch projectPath config (PreviousAddress.ReuseAddress "http://127.0.0.1:5123")
      plan.EnvironmentVariables |> List.contains (urlsVar, "http://localhost:5043") |> Expect.isTrue "the project's address"
      plan.UrlPolicy |> Expect.equal "project-configured" UrlPolicy.ProjectConfigured
  ]

[<Tests>]
let acrossWorkerRestartTests =
  let at = System.DateTime(2026, 9, 11, 0, 0, 0, System.DateTimeKind.Utc)
  let web = "/src/Web/Web.fsproj"
  let rebuilding =
    AppRunState.Starting (web, StartPhase.RebuildingForChanges (SageFs.Features.ReloadPlanning.ReloadChange.TypeChanged "Priority", []), at)
  let running =
    AppRunState.Running
      { RunId = "r1"; Project = web; EntryPoint = "Web.Program.main"; Endpoint = AppEndpoint.Http ("http://127.0.0.1:5123", []); StartedAt = at }
  testList "AppRun acrossWorkerRestart" [
    testCase "WHY — AppRun.acrossWorkerRestart — an app being rebuilt stays Starting because the card must not flash Not running mid-rebuild" <| fun _ ->
      acrossWorkerRestart rebuilding |> Expect.equal "still rebuilding" rebuilding

    testCase "WHY — AppRun.acrossWorkerRestart — a running app is not running once its worker is replaced because the process that hosted it is gone" <| fun _ ->
      acrossWorkerRestart running |> Expect.equal "not running" AppRunState.NotRunning

    testCase "WHY — AppRun.acrossWorkerRestart — how the last run ended is kept because the card still explains it" <| fun _ ->
      let crashed = AppRunState.Crashed (web, "boom", at)
      acrossWorkerRestart crashed |> Expect.equal "still crashed" crashed
  ]

[<Tests>]
let buildFailedStateTests =
  let at = System.DateTime(2026, 9, 11, 0, 0, 0, System.DateTimeKind.Utc)
  let reason = "Build failed (exit 1):\nProgram.fs(172,1): error FS0433: An entry point must be last.\n→ Fix the build errors, then press ▶ Run to rebuild and start the app."
  let failed = AppRunState.BuildFailed ("/src/Web/Web.fsproj", reason, at)
  testList "AppRun build failed" [
    testCase "WHY — AppRun.describeState — a failed rebuild says the app could not be rebuilt, not that it crashed, because the user's code did not compile" <| fun _ ->
      describeState failed |> Expect.equal "names the app and the build output" (sprintf "Web could not be rebuilt: %s" reason)

    testCase "WHY — AppRun.toView — a failed rebuild reports its own state name because clients switch on it" <| fun _ ->
      (toView failed).State |> Expect.equal "state name" "BuildFailed"

    testCase "WHY — AppRun.acrossWorkerRestart — a failed rebuild is kept because the card still has to say why" <| fun _ ->
      acrossWorkerRestart failed |> Expect.equal "still failed" failed
  ]
