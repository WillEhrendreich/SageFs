/// Outcome gates for the cases where a project pins a version of something the FSI host also
/// carries, or where the host cannot give the project the runtime it needs.
///
/// FSI resolves assemblies by simple NAME and says nothing when the version or the runtime is wrong,
/// so each of these used to either work by luck or fail with a message that names the wrong thing.
/// A case here is a small fixture project (built offline by `ProjectPinFixtures`), a real session on a
/// real daemon, and one question: what does the USER see? The only passing answers are
///   - the session reaches Ready and the code that exercises the conflicting library gives the right
///     answer, or
///   - the session is refused, Faulted or Degraded with a message that names what conflicts.
/// A session that is Ready and Healthy and gives the wrong answer is the failure, and so is a probe
/// that "passes" because nothing ran.
module SageFs.Tests.ProjectPinOutcomeTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open SageFs.Tests.ProjectPinFixtures

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Http = SageFs.Tests.HttpApiIntegrationTests
module Daemon = SageFs.Tests.CohortMcpToolsIntegrationTests

/// What a case is allowed to settle on.
[<RequireQualifiedAccess>]
type private Standard =
  /// The session works, or it is refused or degraded loudly and the message names the conflict.
  | WorksOrLoud
  /// The session must work. Used where the product is expected to adapt, not just to warn.
  | MustWork
  /// The user has to be told: the session is refused, Faulted or Degraded and the message names the
  /// facts. A session that simply works is the failure, because it hid something the user must know.
  | MustBeLoud

/// One fixture and the question asked of its session.
type private Case =
  { /// The folder the session is created in.
    WorkingDir: string
    /// The project or solution paths handed to session create.
    Targets: string list
    /// F# the session evaluates; it must exercise the library that conflicts.
    Probe: string
    /// What the probe returns when the project got the version it pinned.
    Expected: string
    /// Facts a loud message must name (library, versions, projects), not prose.
    MustName: string list
    Standard: Standard }

/// What a Ready session says about its own usability.
[<RequireQualifiedAccess>]
type private Health =
  | Healthy
  | NotHealthy of reason: string

/// What the user ends up with.
[<RequireQualifiedAccess>]
type private Outcome =
  /// Ready, and the probe gave the right answer. `health` is what the session said about itself.
  | Worked of result: string * health: Health
  /// Session create refused the request.
  | RefusedAtCreate of status: int * message: string
  /// The session Faulted, with this reason.
  | Faulted of reason: string
  /// The session was there after create and then the daemon said it has no such session.
  | Vanished of reply: string
  /// The session is Ready but its health says it is not Healthy, with this reason.
  | Degraded of reason: string * probeResult: string
  /// Ready, Healthy, and the probe did not give the right answer: nothing told the user.
  | Silent of probeResult: string

let private describeOutcome (outcome: Outcome) : string =
  match outcome with
  | Outcome.Worked(result, Health.Healthy) -> sprintf "worked, healthy (%s)" result
  | Outcome.Worked(result, Health.NotHealthy reason) -> sprintf "worked, but degraded: %s (%s)" reason result
  | Outcome.RefusedAtCreate(status, message) -> sprintf "refused at create (HTTP %d): %s" status message
  | Outcome.Faulted reason -> sprintf "faulted: %s" reason
  | Outcome.Vanished reply -> sprintf "the session vanished and the daemon only says: %s" reply
  | Outcome.Degraded(reason, probe) -> sprintf "ready but degraded: %s (probe gave: %s)" reason probe
  | Outcome.Silent probe -> sprintf "SILENT: ready and healthy, and the probe gave: %s" probe

/// The first line of a tool reply. The daemon appends an events trailer after a blank line.
let private firstLine (text: string) : string =
  match text.Split('\n') with
  | [||] -> text
  | lines -> lines[0]

let private callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> = task {
  let! result = client.CallToolAsync(name, readOnlyDict args, null, null, CancellationToken.None)
  return
    result.Content
    |> Seq.choose (function
      | :? ModelContextProtocol.Protocol.TextContentBlock as t -> Some t.Text
      | _ -> None)
    |> String.concat ""
}

/// Where get_session_status's long wait ended.
[<RequireQualifiedAccess>]
type private Settled =
  | Ready of healthStatus: string * healthReason: string
  | Faulted of reason: string
  /// The daemon says the session is gone: the user is left with nothing to read.
  | Vanished of reply: string
  | StillStarting of lastStatus: string

/// Waits on the session until it is Ready or Faulted. `get_session_status` holds the request open
/// server-side until the session settles, so this is a long wait and not a poll with a sleep.
let private settle (mcp: McpClient) (sessionId: string) : Task<Settled> = task {
  let started = Stopwatch.StartNew()
  let mutable settled = Settled.StillStarting ""
  let mutable decided = false
  while not decided && started.Elapsed < TestTimeouts.sessionReadyColdBuild do
    let! text = callTool mcp "get_session_status" [ "session_id", box sessionId; "wait_seconds", box TestTimeouts.sessionStatusLongWaitSeconds ]
    use doc = JsonDocument.Parse(firstLine text)
    let root = doc.RootElement
    let stringOf (element: JsonElement) (name: string) =
      match element.TryGetProperty name with
      | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
      | _ -> ""
    let healthStatus, healthReason =
      match root.TryGetProperty "health" with
      | true, health -> stringOf health "status", stringOf health "reason"
      | false, _ -> "", ""
    // A Ready session reports `lifecycle` and `health`; a Faulted one reports `state` and `faultReason`.
    let lifecycle =
      match stringOf root "lifecycle" with
      | "" -> stringOf root "state"
      | named -> named
    match lifecycle with
    | "Ready" ->
      settled <- Settled.Ready(healthStatus, healthReason)
      decided <- true
    | "Faulted" ->
      eprintfn "PIN-STATUS faulted: %s" text
      let reason =
        match stringOf root "faultReason" with
        | "" -> healthReason
        | fault -> fault
      settled <- Settled.Faulted reason
      decided <- true
    | "" ->
      // No lifecycle at all: the daemon answered that it has no such session any more.
      eprintfn "PIN-STATUS no session: %s" text
      settled <- Settled.Vanished text
      decided <- true
    | other -> settled <- Settled.StillStarting other
  return settled
}

let private newClient (port: int) : HttpClient =
  let client = new HttpClient()
  client.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
  client.Timeout <- TestTimeouts.httpDaemon
  client

/// Creates the session through the HTTP API, waits for it, probes it, and reports what the user got.
let private attempt (port: int) (case: Case) : Task<Outcome> = task {
  use http = newClient port
  use! mcp = Daemon.connect port
  let! status, body =
    Http.postJson http "/api/sessions/create" {| projects = case.Targets |> List.toArray; workingDirectory = case.WorkingDir |}
  match status with
  | 200 ->
    use created = JsonDocument.Parse body
    let sessionId = created.RootElement.GetProperty("message").GetString()
    let! settled = settle mcp sessionId
    match settled with
    | Settled.Faulted reason -> return Outcome.Faulted reason
    | Settled.Vanished reply -> return Outcome.Vanished reply
    | Settled.StillStarting last -> return failwithf "the session never settled within the budget (last lifecycle: %s)" last
    | Settled.Ready(healthStatus, healthReason) ->
      let! execStatus, execBody =
        Http.postJson http "/exec" {| code = case.Probe; working_directory = case.WorkingDir |}
      use exec = JsonDocument.Parse execBody
      let result =
        match exec.RootElement.TryGetProperty "result" with
        | true, r when r.ValueKind = JsonValueKind.String -> r.GetString()
        | _ -> execBody
      // The /exec `success` flag matters: an error message can quote the very text the probe expects.
      let succeeded =
        match exec.RootElement.TryGetProperty "success" with
        | true, flag -> flag.ValueKind = JsonValueKind.True
        | false, _ -> false
      let probeWorked = execStatus = 200 && succeeded && result.Contains case.Expected
      let health =
        match healthStatus with
        | "Healthy" -> Health.Healthy
        | _ -> Health.NotHealthy healthReason
      match probeWorked, health with
      | true, _ -> return Outcome.Worked(result, health)
      | false, Health.Healthy -> return Outcome.Silent result
      | false, Health.NotHealthy reason -> return Outcome.Degraded(reason, result)
  | _ -> return Outcome.RefusedAtCreate(status, body)
}

/// Fails with the whole outcome unless it is one the case accepts.
let private verify (case: Case) (outcome: Outcome) : unit =
  let loud (message: string) =
    match case.Standard with
    | Standard.MustWork ->
      failtestf "this case must WORK, but it was %s" (describeOutcome outcome)
    | Standard.WorksOrLoud
    | Standard.MustBeLoud ->
      let missing = case.MustName |> List.filter (fun fact -> not (message.Contains fact))
      match missing with
      | [] -> ()
      | _ ->
        failtestf
          "a loud message has to name what conflicts. It is missing [%s]. Outcome: %s"
          (String.concat "; " missing)
          (describeOutcome outcome)
  match outcome with
  | Outcome.Worked(_, Health.NotHealthy reason) when case.Standard = Standard.MustBeLoud -> loud reason
  | Outcome.Worked _ ->
    match case.Standard with
    | Standard.MustBeLoud ->
      failtestf "the session just worked and said nothing, but the user had to be told. Outcome: %s" (describeOutcome outcome)
    | Standard.WorksOrLoud
    | Standard.MustWork -> ()
  | Outcome.Silent _ ->
    failtestf "the user was told nothing and got the wrong answer. Outcome: %s" (describeOutcome outcome)
  | Outcome.RefusedAtCreate(_, message) -> loud message
  | Outcome.Faulted reason -> loud reason
  | Outcome.Vanished reply -> loud reply
  | Outcome.Degraded(reason, _) -> loud reason

/// The dashboard's own page, on the port after the MCP port.
let private dashboardHtml (port: int) : Task<string> = task {
  use http = newClient (port + 1)
  let! _, body = Http.getJson http "/dashboard"
  return body
}

let private runCase (case: Case) : Task<unit> =
  Daemon.withDaemon (fun port -> task {
    let! outcome = attempt port case
    // Printed on every run, pass or fail, so the matrix of what the user saw is in the log.
    eprintfn "PIN-OUTCOME %s :: %s" (Path.GetFileName case.WorkingDir) (describeOutcome outcome)
    verify case outcome
    // A loud session has to be loud where the user looks, and the dashboard is where they look.
    match case.Standard, outcome with
    | Standard.MustBeLoud, Outcome.RefusedAtCreate _ -> ()
    | Standard.MustBeLoud, _ ->
      let! html = dashboardHtml port
      for fact in case.MustName do
        html |> Expect.stringContains (sprintf "the dashboard shows %s" fact) fact
    | Standard.WorksOrLoud, _
    | Standard.MustWork, _ -> ()
  })

/// The SDK pair, or a test failure that says what this machine lacks.
let private requireSdkPair () : Task<SdkPair> = task {
  let! pair = sdkPair ()
  match pair with
  | Result.Ok pair -> return pair
  | Result.Error reason -> return failwith reason
}

let private newestSdk () : Task<InstalledSdk> = task {
  let! sdks = installedSdks ()
  return List.last sdks
}

// ---------------------------------------------------------------------------------------------
// (a) A solution whose two projects resolve different versions of one package.
// ---------------------------------------------------------------------------------------------

let private twoVersionsCase (ws: Workspace) : Task<Case> = task {
  let! sdk = newestSdk ()
  let tfm = tfmOf sdk
  let libSource (n: int) = sprintf "module PinLib.Lib\n\nlet version () = \"pinlib-%d\"\n" n
  do! packLibrary ws sdk tfm "PinLib" "1.0.0" "1.0.0.0" (libSource 1)
  do! packLibrary ws sdk tfm "PinLib" "2.0.0" "2.0.0.0" (libSource 2)
  let solutionDir = Path.Combine(ws.Root, "Sol")
  let writeApp (name: string) (version: string) (body: string) =
    let dir = Path.Combine(solutionDir, name)
    writeFile (Path.Combine(dir, name + ".fsproj")) (fsharpProject tfm "" [ "PinLib", version ] [ "P.fs" ])
    writeFile (Path.Combine(dir, "P.fs")) body
    Path.Combine(dir, name + ".fsproj")
  let a = writeApp "A" "1.0.0" "module A.P\n\nlet run () = \"A sees \" + PinLib.Lib.version ()\n"
  let b = writeApp "B" "2.0.0" "module B.P\n\nlet run () = \"B sees \" + PinLib.Lib.version ()\n"
  writeFile
    (Path.Combine(solutionDir, "Sol.slnx"))
    "<Solution>\n  <Project Path=\"A/A.fsproj\" />\n  <Project Path=\"B/B.fsproj\" />\n</Solution>\n"
  do! buildProject a
  do! buildProject b
  return
    { WorkingDir = solutionDir
      Targets = [ Path.Combine(solutionDir, "Sol.slnx") ]
      // B pinned PinLib 2.0.0. In one FSI process only one PinLib can be loaded.
      Probe = "B.P.run ();;"
      Expected = "B sees pinlib-2"
      MustName = [ "PinLib"; "1.0.0"; "2.0.0"; "A.fsproj"; "B.fsproj" ]
      Standard = Standard.WorksOrLoud }
}

// ---------------------------------------------------------------------------------------------
// (b) A NuGet package compiled against a newer FSharp.Core than the SDK's.
// ---------------------------------------------------------------------------------------------

/// A project that names its own FSharp.Core version. The F# SDK's implicit reference would win over an
/// explicit one with a different version, so the implicit one is switched off, as it is in every project
/// that pins FSharp.Core on purpose.
let private ownFSharpCore =
  "    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>"

let private newerFSharpCorePackageCase (ws: Workspace) : Task<Case> = task {
  let! pair = requireSdkPair ()
  let tfm = tfmOf pair.Older
  // The package is built by the NEWER SDK, so it is compiled against the newer FSharp.Core and calls
  // Async.map, a member FSharp.Core 10 does not have. The app is hosted by the OLDER SDK.
  copyFSharpCorePack ws pair.Newer
  do!
    packLibrary
      ws
      pair.Newer
      tfm
      "NewCoreLib"
      "1.0.0"
      "1.0.0.0"
      "module NewCoreLib.Lib\n\nlet doubled () : int =\n  Async.RunSynchronously(Async.map (fun x -> x * 2) (async { return 21 }))\n"
  let appDir = Path.Combine(ws.Root, "App")
  pinSdk appDir pair.Older
  let app = Path.Combine(appDir, "App.fsproj")
  // An app that uses a package built against FSharp.Core N has to reference FSharp.Core N itself.
  writeFile
    app
    (fsharpProject tfm ownFSharpCore [ "NewCoreLib", "1.0.0"; "FSharp.Core", fsharpCoreVersionOf pair.Newer ] [ "P.fs" ])
  writeFile (Path.Combine(appDir, "P.fs")) "module App.P\n\nlet run () = \"doubled: \" + string (NewCoreLib.Lib.doubled ())\n"
  do! buildProject app
  return
    { WorkingDir = appDir
      Targets = [ app ]
      Probe = "App.P.run ();;"
      Expected = "doubled: 42"
      MustName = [ "FSharp.Core"; "NewCoreLib" ]
      Standard = Standard.MustWork }
}

// ---------------------------------------------------------------------------------------------
// (c) A project that pins a newer System.Text.Json than the shared framework's.
// ---------------------------------------------------------------------------------------------

let private newerSystemTextJsonCase (ws: Workspace) : Task<Case> = task {
  let! sdk = newestSdk ()
  let tfm = tfmOf sdk
  writeNupkg
    ws.Feed
    { Id = "System.Text.Json"
      Version = "99.0.0"
      Files = [ sprintf "lib/%s/System.Text.Json.dll" tfm, stampedSystemTextJson (System.Version(99, 0, 0, 0)) "stj-99" ]
      Dependencies = [] }
  let appDir = Path.Combine(ws.Root, "App")
  let app = Path.Combine(appDir, "App.fsproj")
  writeFile app (fsharpProject tfm "" [ "System.Text.Json", "99.0.0" ] [ "P.fs" ])
  writeFile
    (Path.Combine(appDir, "P.fs"))
    "module App.P\n\nlet marker () = System.Text.Json.PinMarker.Value ()\n\nlet roundTrip () = System.Text.Json.JsonSerializer.Serialize [| 1; 2 |]\n"
  do! buildProject app
  return
    { WorkingDir = appDir
      Targets = [ app ]
      Probe = "App.P.marker ();;"
      Expected = "stj-99"
      MustName = [ "System.Text.Json"; "99.0.0" ]
      Standard = Standard.MustWork }
}

// ---------------------------------------------------------------------------------------------
// (d) A project that carries its own FSharp.Compiler.Service next to the host's.
// ---------------------------------------------------------------------------------------------

let private ownCompilerServiceCase (ws: Workspace) : Task<Case> = task {
  let! pair = requireSdkPair ()
  let tfm = tfmOf pair.Older
  // The newer SDK's own compiler service, wrapped as a package: what a project built from a newer
  // compiler checkout, or pinning a newer FSharp.Compiler.Service, puts in its output folder. The
  // host is the OLDER SDK, whose compiler service does not have the F# 11 spread types.
  let fcsDll = Path.Combine(pair.Newer.SdkDir, "FSharp", "FSharp.Compiler.Service.dll")
  let fcsVersion = FileVersionInfo.GetVersionInfo(fcsDll).ProductVersion.Split('+').[0]
  let newerCore = fsharpCoreVersionOf pair.Newer
  copyFSharpCorePack ws pair.Newer
  packAssembly ws "FSharp.Compiler.Service" fcsVersion "netstandard2.0" fcsDll [ "FSharp.Core", newerCore ]
  let appDir = Path.Combine(ws.Root, "App")
  pinSdk appDir pair.Older
  let app = Path.Combine(appDir, "App.fsproj")
  writeFile
    app
    (fsharpProject tfm ownFSharpCore [ "FSharp.Compiler.Service", fcsVersion; "FSharp.Core", newerCore ] [ "P.fs" ])
  writeFile
    (Path.Combine(appDir, "P.fs"))
    "module App.P\n\nlet spreadTypeName () = typeof<FSharp.Compiler.Syntax.SynFieldOrSpread>.FullName\n"
  do! buildProject app
  return
    { WorkingDir = appDir
      Targets = [ app ]
      Probe = "App.P.spreadTypeName ();;"
      Expected = "FSharp.Compiler.Syntax.SynFieldOrSpread"
      MustName = [ "FSharp.Compiler.Service"; fcsVersion ]
      Standard = Standard.WorksOrLoud }
}

// ---------------------------------------------------------------------------------------------
// (e) What the user is told when the host cannot give the project the runtime it needs.
// ---------------------------------------------------------------------------------------------

/// A built executable project whose `runtimeconfig.json` is then replaced with `runtimeConfig`: what a
/// project built for another machine's runtime looks like, or a build that left a broken file.
let private builtExecutableWithRuntimeConfig (runtimeConfig: string) (mustName: string list) (ws: Workspace) : Task<Case> = task {
  let! sdk = newestSdk ()
  let tfm = tfmOf sdk
  let appDir = Path.Combine(ws.Root, "App")
  let app = Path.Combine(appDir, "App.fsproj")
  writeFile app (fsharpProject tfm "    <OutputType>Exe</OutputType>" [] [ "P.fs" ])
  writeFile
    (Path.Combine(appDir, "P.fs"))
    "module App.P\n\nlet run () = \"app ran\"\n\n[<EntryPoint>]\nlet main _ = 0\n"
  do! buildProject app
  let runtimeConfigPath =
    Directory.GetFiles(Path.Combine(appDir, "bin"), "App.runtimeconfig.json", SearchOption.AllDirectories)
    |> Array.exactlyOne
  File.WriteAllText(runtimeConfigPath, runtimeConfig)
  return
    { WorkingDir = appDir
      Targets = [ app ]
      Probe = "App.P.run ();;"
      Expected = "app ran"
      MustName = mustName
      Standard = Standard.MustBeLoud }
}

let private runtimeNobodyHasConfig =
  """{ "runtimeOptions": { "tfm": "net99.0", "framework": { "name": "Microsoft.NETCore.App", "version": "99.0.0" } } }"""

let private missingRuntimeCase : Workspace -> Task<Case> =
  builtExecutableWithRuntimeConfig runtimeNobodyHasConfig [ ".NET 99"; "install" ]

let private corruptRuntimeConfigCase : Workspace -> Task<Case> =
  builtExecutableWithRuntimeConfig "{ this is not json" [ "App.runtimeconfig.json" ]

/// A .NET Framework project whose target framework is NOT in the project file (a shared
/// Directory.Build.props sets it), so the daemon's read of the .fsproj cannot see it.
let private frameworkTfmFromPropsCase (ws: Workspace) : Task<Case> = task {
  let appDir = Path.Combine(ws.Root, "App")
  let app = Path.Combine(appDir, "App.fsproj")
  writeFile
    app
    "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Compile Include=\"P.fs\" />\n  </ItemGroup>\n</Project>\n"
  writeFile (Path.Combine(appDir, "P.fs")) "module App.P\n\nlet run () = \"app ran\"\n"
  writeFile
    (Path.Combine(appDir, "Directory.Build.props"))
    "<Project>\n  <PropertyGroup>\n    <TargetFramework>net48</TargetFramework>\n  </PropertyGroup>\n</Project>\n"
  return
    { WorkingDir = appDir
      Targets = [ app ]
      Probe = "App.P.run ();;"
      Expected = "app ran"
      MustName = [ "net48"; "135" ]
      Standard = Standard.MustBeLoud }
}

/// A .NET Framework project, named in the project file itself.
let private netFrameworkProjectCase (ws: Workspace) : Task<Case> = task {
  let appDir = Path.Combine(ws.Root, "App")
  let app = Path.Combine(appDir, "App.fsproj")
  writeFile app (fsharpProject "net48" "" [] [ "P.fs" ])
  writeFile (Path.Combine(appDir, "P.fs")) "module App.P\n\nlet run () = \"app ran\"\n"
  return
    { WorkingDir = appDir
      Targets = [ app ]
      Probe = "App.P.run ();;"
      Expected = "app ran"
      MustName = [ "App.fsproj"; "net48"; "135" ]
      Standard = Standard.MustBeLoud }
}

/// Set this to keep a case's fixture folder after the run, to build and inspect it by hand.
[<Literal>]
let private KeepFixturesVariable = "SAGEFS_KEEP_PIN_FIXTURES"

/// Builds a workspace under `prefix`, lets `build` fill it, runs the case, and deletes it afterwards
/// (unless `SAGEFS_KEEP_PIN_FIXTURES` is set, in which case the folder is named in the log and left).
let private withCase (prefix: string) (build: Workspace -> Task<Case>) : Task<unit> = task {
  let ws = createWorkspace prefix
  try
    let! case = build ws
    do! runCase case
  finally
    match Environment.GetEnvironmentVariable KeepFixturesVariable with
    | null
    | "" -> deleteWorkspace ws
    | _ -> eprintfn "PIN-WORKSPACE kept at %s" ws.Root
}

[<Tests>]
let projectPinOutcomeTests =
  testSequenced
  <| Integration.hostList "Project pins a version the host also carries: outcome" [

    testTask "WHY — a solution whose two projects resolve different versions of one package does not silently run one project against the other's version" {
      do! withCase "sagefs-pin-two-versions-" twoVersionsCase
    }

    testTask "WHY — a package compiled against a newer FSharp.Core than the host's SDK carries runs, because the host's older FSharp.Core lacks the members it calls" {
      do! withCase "sagefs-pin-newer-fscore-" newerFSharpCorePackageCase
    }

    testTask "WHY — a project that pins a newer System.Text.Json than the shared framework's gets the version it pinned" {
      do! withCase "sagefs-pin-newer-stj-" newerSystemTextJsonCase
    }

    testTask "WHY — a project that carries its own, newer FSharp.Compiler.Service is told that the host's compiler service answers for it" {
      do! withCase "sagefs-pin-own-fcs-" ownCompilerServiceCase
    }
  ]

[<Tests>]
let hostCannotServeProjectTests =
  testSequenced
  <| Integration.hostList "The host cannot serve the project: what the user is told" [

    testTask "WHY — a .NET Framework project is refused at create, and the HTTP reply and the MCP tool both say which project, which framework and where it is tracked" {
      do! withCase "sagefs-pin-netfx-" netFrameworkProjectCase
      let ws = createWorkspace "sagefs-pin-netfx-mcp-"
      try
        let! case = netFrameworkProjectCase ws
        do!
          Daemon.withDaemon (fun port -> task {
            use! mcp = Daemon.connect port
            let! text =
              callTool mcp "create_project_session" [ "project", box (List.head case.Targets); "working_directory", box case.WorkingDir ]
            for fact in case.MustName do
              text |> Expect.stringContains (sprintf "the MCP tool names %s" fact) fact
          })
      finally
        deleteWorkspace ws
    }

    testTask "WHY — a project built for a .NET runtime this machine does not have says which runtime and where to get it, on the session, the MCP status and the dashboard" {
      do! withCase "sagefs-pin-missing-runtime-" missingRuntimeCase
    }

    testTask "WHY — a project whose runtimeconfig.json cannot be read is not hosted on a guess: the session says it could not tell which runtime the project needs" {
      do! withCase "sagefs-pin-corrupt-runtimeconfig-" corruptRuntimeConfigCase
    }

    testTask "WHY — a .NET Framework target framework that a Directory.Build.props sets, which the project file does not show, is still refused with the framework and the tracking issue" {
      do! withCase "sagefs-pin-netfx-from-props-" frameworkTfmFromPropsCase
    }
  ]
