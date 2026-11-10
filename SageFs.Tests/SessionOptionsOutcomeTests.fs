/// Session options, read back from inside a real session. The pure tests prove that an option becomes an environment entry, a
/// build argument or a carrier value. These prove the effect a person asked for: the host reports Server GC, the host sees the
/// variable, the artifacts folder holds the build output, and a session keeps its own options when another is created in the
/// same directory. Each case starts real worker processes through the session manager, the way the daemon does.
module SageFs.Tests.SessionOptionsOutcomeTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Tests.DaemonIntegrationTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// A directory, with its own global settings folder, so nothing here reads or writes the person's real settings.
type private Place =
  { Directory: string
    GlobalDir: string }

/// A session manager and the sessions made through it, so every one is stopped whatever the case does.
type private Harness =
  { Manager: MailboxProcessor<SessionManager.SessionCommand>
    Created: ResizeArray<SessionId> }

/// Runs `body`, then `cleanup`, and rethrows what `body` threw. A task cannot await in a `finally`.
let private guarded (cleanup: unit -> Task) (body: unit -> Task<unit>) : Task<unit> =
  task {
    let! failure =
      task {
        try
          do! body ()
          return None
        with ex -> return Some ex
      }
    do! cleanup ()
    match failure with
    | Some ex -> rethrow ex
    | None -> ()
  }

let private withPlace (run: Place -> Task<unit>) : Task<unit> =
  let root = Path.Combine(Path.GetTempPath(), "sagefs-options-outcome-" + Guid.NewGuid().ToString "N")
  let place = { Directory = Path.Combine(root, "app"); GlobalDir = Path.Combine(root, "global") }
  Directory.CreateDirectory place.Directory |> ignore
  Directory.CreateDirectory place.GlobalDir |> ignore
  guarded
    (fun () ->
      SessionOptionsRegistry.remember place.Directory SessionOptions.none
      try Directory.Delete(root, true) with :? IOException -> ()
      Task.CompletedTask)
    (fun () -> run place)

let private withHarness (run: Harness -> Task<unit>) : Task<unit> =
  let cts = new CancellationTokenSource(int Timeouts.integrationDaemonReady.TotalMilliseconds)
  let mgr, _ = SessionManager.create cts.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ _ -> ())
  let harness = { Manager = mgr; Created = ResizeArray() }
  guarded
    (fun () ->
      task {
        for id in harness.Created do
          do! cleanupSession mgr id
        cts.Dispose()
      } :> Task)
    (fun () -> run harness)

let private writeConfig (place: Place) (text: string) =
  Directory.CreateDirectory(Path.Combine(place.Directory, ".SageFs")) |> ignore
  File.WriteAllText(Path.Combine(place.Directory, ".SageFs", "config.fsx"), text)

let private saveRow (place: Place) (descriptor: SettingDescriptor) (raw: string) =
  let paths = SettingsCatalog.pathsFor place.GlobalDir place.Directory
  match SettingsCatalog.edit paths LRepo raw descriptor with
  | Ok _ -> ()
  | Error why -> failtestf "%s should save %s: %s" descriptor.Key raw (ConfigError.describe why)

/// What the daemon does when a session is created: resolve the options, with no project asked about.
let private prepare (place: Place) (projects: string list) =
  DirectoryConfig.prepareSession (SettingsCatalog.pathsFor place.GlobalDir place.Directory) (fun _ -> SessionOptions.ProjectLeavesArtifacts, []) projects place.Directory
  |> Expect.isTrue "auto-open stays on"

let private create (harness: Harness) (targets: SessionProjectTarget list) (directory: string) : Task<SessionInfo> =
  task {
    let! created =
      harness.Manager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.CreateSession(targets, directory, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
      |> Async.StartAsTask
    match created with
    | Error err -> return failwithf "create failed: %s" (SageFsError.describe err)
    | Ok info ->
      harness.Created.Add info.Id
      let! ready = harness.Manager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.AwaitReady(info.Id, reply)) |> Async.StartAsTask
      match ready with
      | Error err -> return failwithf "the session did not become ready: %s" (SageFsError.describe err)
      | Ok () -> return info
  }

let private eval (harness: Harness) (info: SessionInfo) (code: string) : Task<string> =
  task {
    let! found = harness.Manager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.GetSession(info.Id, reply)) |> Async.StartAsTask
    match found with
    | None -> return failwith "the session is gone"
    | Some session ->
      let! response = session.Proxy(WorkerMessage.EvalCode(code, "outcome")) |> Async.StartAsTask
      match response with
      | WorkerResponse.EvalResult(_, Ok output, _, _) -> return output
      | WorkerResponse.EvalResult(_, Error err, _, _) -> return failwithf "eval error: %s" (SageFsError.describe err)
      | other -> return failwithf "unexpected eval response: %A" other
  }

let private isServerGc = "System.Runtime.GCSettings.IsServerGC;;"

let private probeVariable = "SAGEFS_OUTCOME_PROBE"

let private readProbe = sprintf "System.Environment.GetEnvironmentVariable \"%s\";;" probeVariable

let private smallProject (directory: string) : string =
  File.WriteAllText(Path.Combine(directory, "Small.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Lib.fs\" /></ItemGroup></Project>")
  File.WriteAllText(Path.Combine(directory, "Lib.fs"), "module Lib\nlet answer = 42\n")
  Path.Combine(directory, "Small.fsproj")

/// The daemon's create path for a project: resolve the options, build where the session's builds go, check the preflight
/// looks there, then create the session.
let private createProjectSession (harness: Harness) (place: Place) (project: string) : Task<SessionInfo> =
  task {
    prepare place [ project ]
    let! built = SessionBuild.runBuildAsync [ project ] place.Directory |> Async.StartAsTask
    built |> Expect.isOk "the session's build succeeds"
    let artifacts = (SessionOptionsRegistry.optionsFor place.Directory).Artifacts
    BuildPreflight.checkInUnder artifacts File.Exists File.ReadAllText place.Directory (SessionProjectTarget.Project project)
    |> Expect.equal "the preflight finds the build where the session wrote it" BuildPreflight.Ready
    return! create harness [ SessionProjectTarget.Project project ] place.Directory
  }

/// One session with no project, in the place, for `body`.
let private inBareSession (place: Place) (body: Harness -> SessionInfo -> Task<unit>) : Task<unit> =
  withHarness (fun harness ->
    task {
      let! info = create harness [ SessionProjectTarget.Bare ] place.Directory
      do! body harness info
    })

[<Tests>]
let outcomeTests =
  Integration.hostList "Session options reach a real session" [

    testTask "WHY - Gc = ServerGc in config.fsx makes the host report Server GC, and a directory that says nothing runs Workstation" {
      do! HostSlots.withSlot (fun () -> withPlace (fun place -> task {
        writeConfig place "{ DirectoryConfig.empty with Gc = ServerGc }"
        prepare place []
        do! inBareSession place (fun harness info -> task {
          let! output = eval harness info isServerGc
          output |> Expect.stringContains "the host reports Server GC" "true" })
      }))
      do! HostSlots.withSlot (fun () -> withPlace (fun place -> task {
        prepare place []
        do! inBareSession place (fun harness info -> task {
          let! output = eval harness info isServerGc
          output |> Expect.stringContains "the default host reports Workstation GC" "false" })
      }))
    }

    testTask "WHY - SessionEnv in config.fsx puts the variable in the host's environment" {
      do! HostSlots.withSlot (fun () -> withPlace (fun place -> task {
        writeConfig place (sprintf "{ DirectoryConfig.empty with SessionEnv = [ EnvEntry(\"%s\", \"from-config\") ] }" probeVariable)
        prepare place []
        do! inBareSession place (fun harness info -> task {
          let! output = eval harness info readProbe
          output |> Expect.stringContains "the host sees the value" "from-config" })
      }))
    }

    testTask "WHY - the settings rows reach the host: a saved Server collector and a saved variable are in force in the next session" {
      do! HostSlots.withSlot (fun () -> withPlace (fun place -> task {
        saveRow place SessionOptionSettings.gcSetting "Server"
        saveRow place SessionOptionSettings.environmentSetting (sprintf "%s=from-row" probeVariable)
        prepare place []
        do! inBareSession place (fun harness info -> task {
          let! gc = eval harness info isServerGc
          gc |> Expect.stringContains "the row's collector is in force" "true"
          let! variable = eval harness info readProbe
          variable |> Expect.stringContains "the row's variable is in force" "from-row" })
      }))
    }

    testTask "WHY - config.fsx wins over a saved row for the option it sets, and the row's value still reaches an option the file leaves alone" {
      do! HostSlots.withSlot (fun () -> withPlace (fun place -> task {
        writeConfig place (sprintf "{ DirectoryConfig.empty with SessionEnv = [ EnvEntry(\"%s\", \"from-config\") ] }" probeVariable)
        saveRow place SessionOptionSettings.environmentSetting (sprintf "%s=from-row" probeVariable)
        saveRow place SessionOptionSettings.gcSetting "Server"
        prepare place []
        do! inBareSession place (fun harness info -> task {
          let! variable = eval harness info readProbe
          variable |> Expect.stringContains "the file's value wins" "from-config"
          let! gc = eval harness info isServerGc
          gc |> Expect.stringContains "the row's collector stands where the file says nothing" "true" })
      }))
    }

    testTask "WHY - the artifacts path holds the session's build output, the project's own bin and obj stay absent, and the session loads the assembly from there" {
      do! HostSlots.withSlot (fun () -> withPlace (fun place -> task {
        let project = smallProject place.Directory
        let artifacts = Path.Combine(Path.GetDirectoryName place.Directory, "artifacts-out")
        saveRow place SessionOptionSettings.artifactsSetting artifacts
        do! withHarness (fun harness -> task {
          let! info = createProjectSession harness place project
          Directory.GetFiles(artifacts, "Small.dll", SearchOption.AllDirectories) |> Expect.isNonEmpty "the assembly is under the artifacts path"
          Directory.Exists(Path.Combine(place.Directory, "bin")) |> Expect.isFalse "the project has no bin"
          Directory.Exists(Path.Combine(place.Directory, "obj")) |> Expect.isFalse "the project has no obj"
          let! answer = eval harness info "Lib.answer;;"
          answer |> Expect.stringContains "the project's code runs in the session" "42"
          // The host loads a shadow copy of the project's assembly, so the loaded file's own path is a temp path. The host
          // sets AppContext.BaseDirectory to the project's real build output folder (`ProjectOutputEnvironmentVariable`), which
          // is where code in the session finds its own files.
          let! output = eval harness info "System.AppContext.BaseDirectory;;"
          output |> Expect.stringContains "the host's project output folder is under the artifacts path" artifacts })
      }))
    }

    testTask "WHY - a later create in the same directory does not change a running session: its restarted worker keeps its own collector" {
      do! HostSlots.withSlot (fun () -> withPlace (fun place -> task {
        saveRow place SessionOptionSettings.gcSetting "Server"
        prepare place []
        do! withHarness (fun harness -> task {
          let! first = create harness [ SessionProjectTarget.Bare ] place.Directory
          let! firstGc = eval harness first isServerGc
          firstGc |> Expect.stringContains "the first session starts under Server GC" "true"
          // The person changes the row and starts another session in the same directory.
          saveRow place SessionOptionSettings.gcSetting "Workstation"
          let project = smallProject place.Directory
          let! second = createProjectSession harness place project
          let! secondGc = eval harness second isServerGc
          secondGc |> Expect.stringContains "the second session starts under the new choice" "false"
          // The first session's worker is replaced. It must come back under its own options, not the second's.
          let! restarted = harness.Manager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.RestartSession(first.Id, RestartPlan.RespawnOnly, reply)) |> Async.StartAsTask
          restarted |> Expect.isOk "the first session restarts"
          let! ready = harness.Manager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.AwaitReady(first.Id, reply)) |> Async.StartAsTask
          ready |> Expect.isOk "the replacement worker reaches Ready"
          let! afterRestart = eval harness first isServerGc
          afterRestart |> Expect.stringContains "the restarted first session is still under Server GC" "true" })
      }))
    }
  ]
