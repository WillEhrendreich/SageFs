/// Outcome gate for sessions on a project that references a MULTI-TARGETED
/// project.
///
/// Reported 2026-09-22 against the published 0.6.782 daemon: a session on
/// SageFs.Tests (net11.0) faulted in warmup with "Not all DLLs are found (3
/// missing: SageFs.Core.dll, SageFs.Host.dll, SageFs.dll) -- this project
/// isn't built yet", right after a clean `dotnet build`. The three "missing"
/// DLLs were exactly the references with `<TargetFrameworks>net10.0;net11.0`.
/// Ionide loads each referenced project at its first TFM, so SageFs looked in
/// bin/Debug/net10.0 while `dotnet build` of a net11.0 consumer only ever
/// writes bin/Debug/net11.0.
///
/// This builds the smallest version of that shape in a temp dir (nothing from
/// the repo's Directory.Build.props leaks in): Lib targets net10.0;net11.0 and
/// returns a different string per TFM, App targets net11.0 and references Lib.
/// Only App is built, the way a user builds their own project. The session
/// has to reach Ready AND evaluate Lib's code, and the value has to come from
/// the net11.0 build, the one `dotnet build` picked.
module SageFs.Tests.MultiTargetReferenceOutcomeTests

open System
open System.IO
open System.Net.Http
open System.Diagnostics
open System.Text.Json
open Expecto
open Expecto.Flip

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Http = SageFs.Tests.HttpApiIntegrationTests

let private libProject =
  """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net11.0</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Marker.fs" />
  </ItemGroup>
</Project>
"""

let private libSource =
  """module Lib.Marker

let builtFor =
#if NET11_0_OR_GREATER
  "lib-net11.0"
#else
  "lib-net10.0"
#endif

let greet (name: string) = sprintf "hello %s from %s" name builtFor
"""

let private appProject =
  """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Lib/Lib.fsproj" />
  </ItemGroup>
</Project>
"""

let private appSource =
  """module App.Program

let answer () = Lib.Marker.greet "app"
"""

/// Writes the two projects under `root` and returns App's project path.
let private writeFixture (root: string) : string =
  let libDir = Directory.CreateDirectory(Path.Combine(root, "Lib")).FullName
  let appDir = Directory.CreateDirectory(Path.Combine(root, "App")).FullName
  File.WriteAllText(Path.Combine(libDir, "Lib.fsproj"), libProject, Http.utf8NoBom)
  File.WriteAllText(Path.Combine(libDir, "Marker.fs"), libSource, Http.utf8NoBom)
  File.WriteAllText(Path.Combine(appDir, "App.fsproj"), appProject, Http.utf8NoBom)
  File.WriteAllText(Path.Combine(appDir, "Program.fs"), appSource, Http.utf8NoBom)
  // A Directory.Build.props that stops MSBuild's upward search, so whatever
  // sits above the temp dir can't change the build.
  File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />", Http.utf8NoBom)
  File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project />", Http.utf8NoBom)
  Path.Combine(appDir, "App.fsproj")

/// Where the session for `dir` ended up: Ready, Faulted (with the body, which
/// carries the reason), or still going when the budget ran out. A fault ends
/// the wait at once, so a regression shows up in seconds with its reason
/// instead of after the whole budget.
[<RequireQualifiedAccess>]
type private Settled =
  | Ready
  | Faulted of sessionsBody: string
  | TimedOut of sessionsBody: string

let private waitForSettled (client: HttpClient) (dir: string) (budget: TimeSpan) = task {
  let started = Stopwatch.StartNew()
  let expected = Http.normalizeDir dir
  let mutable outcome = None
  let mutable lastBody = ""
  while outcome.IsNone && started.Elapsed < budget do
    let! status, body = Http.getJson client "/api/sessions"
    lastBody <- body
    match status with
    | 200 ->
      use doc = JsonDocument.Parse body
      let mine =
        doc.RootElement.GetProperty("sessions").EnumerateArray()
        |> Seq.tryFind (fun s -> Http.normalizeDir (s.GetProperty("workingDirectory").GetString()) = expected)
        |> Option.map (fun s -> s.GetProperty("status").GetString())
      match mine with
      | Some "Ready" -> outcome <- Some Settled.Ready
      | Some "Faulted" -> outcome <- Some (Settled.Faulted body)
      | _ -> do! System.Threading.Tasks.Task.Delay(500)
    | _ -> do! System.Threading.Tasks.Task.Delay(500)
  return outcome |> Option.defaultValue (Settled.TimedOut lastBody)
}

/// (success, result) from an `/exec` response body.
let private execOutcome (body: string) : bool * string =
  use doc = JsonDocument.Parse body
  doc.RootElement.GetProperty("success").GetBoolean(),
  doc.RootElement.GetProperty("result").GetString() |> Option.ofObj |> Option.defaultValue ""

[<Tests>]
let multiTargetReferenceOutcomeTests =
  testSequenced
  <| Integration.hostList "Multi-targeted project reference outcome" [

    testTask "WHY: a session on a net11.0 project that references a net10.0;net11.0 library reaches Ready and runs the library's net11.0 build" {
      let root = Directory.CreateTempSubdirectory("sagefs-multitfm-").FullName
      let mutable daemon : Process = null
      let mutable client : HttpClient = null
      try
        let appProjectPath = writeFixture root
        let appDir = Path.GetDirectoryName appProjectPath
        Http.runProcessExpectSuccess "dotnet" appDir [ "build"; appProjectPath; "--nologo"; "-v:q" ]

        // The exact on-disk shape of the bug: the library has a net11.0 build
        // and NO net10.0 build, because nothing asked for one.
        let libBin = Path.Combine(root, "Lib", "bin", "Debug")
        File.Exists(Path.Combine(libBin, "net11.0", "Lib.dll"))
        |> Expect.isTrue "building App builds Lib at net11.0"
        File.Exists(Path.Combine(libBin, "net10.0", "Lib.dll"))
        |> Expect.isFalse "building App never builds Lib at net10.0, which is the output SageFs used to look for"

        let port = Http.reserveLoopbackPort ()
        let! proc, http = Http.startDaemonWithArgs port appDir [ "--no-resume" ]
        daemon <- proc
        client <- http

        let! createStatus, createBody = Http.createSession http appProjectPath appDir
        createStatus |> Expect.equal (sprintf "session create is accepted (%s)" createBody) 200

        let! settled = waitForSettled http appDir (TimeSpan.FromSeconds 180.0)
        match settled with
        | Settled.Ready -> ()
        | Settled.Faulted body ->
          failtestf "the session faulted instead of reaching Ready (this is the 'Not all DLLs are found' bug). Sessions: %s" body
        | Settled.TimedOut body ->
          failtestf "the session neither reached Ready nor faulted within 180s. Sessions: %s" body

        let! execStatus, execBody =
          Http.postJson http "/exec" {| code = "Lib.Marker.greet \"repl\";;"; working_directory = appDir |}
        execStatus |> Expect.equal (sprintf "/exec reaches the session (%s)" execBody) 200
        let succeeded, result = execOutcome execBody
        succeeded |> Expect.isTrue (sprintf "the referenced library's code evaluates (%s)" execBody)
        result
        |> Expect.stringContains
             "the session loaded Lib's net11.0 build, the one dotnet build made for a net11.0 consumer"
             "hello repl from lib-net11.0"
      finally
        match isNull client with
        | true -> ()
        | false -> client.Dispose()
        match isNull daemon with
        | true -> ()
        | false -> Http.killDaemon daemon
        try Directory.Delete(root, true) with _ -> ()
    }
  ]
