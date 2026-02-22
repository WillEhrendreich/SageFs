module SageFs.Tests.WarmupContextTests

open Expecto
open Expecto.Flip
open SageFs.Core

let sampleAssembly: LoadedAssembly = {
  Name = "MyApp"
  Path = "/bin/MyApp.dll"
  NamespaceCount = 3
  ModuleCount = 2
}

let sampleCtx: WarmupContext = {
  SourceFilesScanned = 15
  AssembliesLoaded = [
    sampleAssembly
    { Name = "MyLib"; Path = "/bin/MyLib.dll"; NamespaceCount = 1; ModuleCount = 0 }
  ]
  NamespacesOpened = [
    { Name = "System"; IsModule = false; Source = "reflection" }
    { Name = "System.IO"; IsModule = false; Source = "reflection" }
    { Name = "MyApp.Utils"; IsModule = true; Source = "source-scan" }
    { Name = "MyApp.Domain"; IsModule = false; Source = "source-scan" }
  ]
  FailedOpens = [ ("BrokenNs", "type not found") ]
  WarmupDurationMs = 1234L
  StartedAt = System.DateTimeOffset.UtcNow
}

[<Tests>]
let warmupContextTests = testList "WarmupContext" [
  testCase "empty has zero counts" <| fun _ ->
    let ctx = WarmupContext.empty
    ctx.SourceFilesScanned
    |> Expect.equal "no files scanned" 0
    ctx.AssembliesLoaded
    |> Expect.isEmpty "no assemblies"
    ctx.NamespacesOpened
    |> Expect.isEmpty "no namespaces"
    ctx.FailedOpens
    |> Expect.isEmpty "no failures"

  testCase "totalOpenedCount returns count of all opened" <| fun _ ->
    WarmupContext.totalOpenedCount sampleCtx
    |> Expect.equal "4 opened" 4

  testCase "totalFailedCount returns count of failures" <| fun _ ->
    WarmupContext.totalFailedCount sampleCtx
    |> Expect.equal "1 failed" 1

  testCase "assemblyNames extracts names" <| fun _ ->
    WarmupContext.assemblyNames sampleCtx
    |> Expect.equal "two assemblies" ["MyApp"; "MyLib"]

  testCase "moduleNames filters to modules only" <| fun _ ->
    WarmupContext.moduleNames sampleCtx
    |> Expect.equal "one module" ["MyApp.Utils"]

  testCase "namespaceNames filters to non-modules only" <| fun _ ->
    WarmupContext.namespaceNames sampleCtx
    |> Expect.equal "three namespaces" ["System"; "System.IO"; "MyApp.Domain"]
]

[<Tests>]
let fileReadinessTests = testList "FileReadiness" [
  testCase "label returns human-readable string" <| fun _ ->
    FileReadiness.label NotLoaded
    |> Expect.equal "not loaded label" "not loaded"
    FileReadiness.label Loaded
    |> Expect.equal "loaded label" "loaded"
    FileReadiness.label Stale
    |> Expect.equal "stale label" "stale"
    FileReadiness.label LoadFailed
    |> Expect.equal "failed label" "load failed"

  testCase "icon returns glyph" <| fun _ ->
    FileReadiness.icon NotLoaded
    |> Expect.equal "not loaded icon" "○"
    FileReadiness.icon Loaded
    |> Expect.equal "loaded icon" "●"

  testCase "isAvailable only true for Loaded" <| fun _ ->
    FileReadiness.isAvailable Loaded
    |> Expect.isTrue "loaded is available"
    FileReadiness.isAvailable Stale
    |> Expect.isFalse "stale not available"
    FileReadiness.isAvailable NotLoaded
    |> Expect.isFalse "not loaded not available"
    FileReadiness.isAvailable LoadFailed
    |> Expect.isFalse "failed not available"
]

let sampleSession: SessionContext = {
  SessionId = "abc123"
  ProjectNames = ["MyApp.fsproj"]
  WorkingDir = "/code/myapp"
  Status = "Ready"
  Warmup = sampleCtx
  FileStatuses = [
    { Path = "Domain.fs"; Readiness = Loaded; LastLoadedAt = Some System.DateTimeOffset.UtcNow; IsWatched = true }
    { Path = "Utils.fs"; Readiness = Loaded; LastLoadedAt = Some System.DateTimeOffset.UtcNow; IsWatched = false }
    { Path = "Tests.fs"; Readiness = NotLoaded; LastLoadedAt = None; IsWatched = false }
    { Path = "Broken.fs"; Readiness = LoadFailed; LastLoadedAt = None; IsWatched = true }
    { Path = "Old.fs"; Readiness = Stale; LastLoadedAt = Some (System.DateTimeOffset.UtcNow.AddHours(-1)); IsWatched = true }
  ]
}

[<Tests>]
let sessionContextTests = testList "SessionContext" [
  testCase "summary includes status and counts" <| fun _ ->
    let s = SessionContext.summary sampleSession
    s |> Expect.stringContains "has status" "Ready"
    s |> Expect.stringContains "has file count" "2/5"
    s |> Expect.stringContains "has namespace count" "4 namespaces"
    s |> Expect.stringContains "has failed count" "1 failed"
    s |> Expect.stringContains "has duration" "1234ms"

  testCase "assemblyLine formats assembly info" <| fun _ ->
    SessionContext.assemblyLine sampleAssembly
    |> Expect.stringContains "has name" "MyApp"
    SessionContext.assemblyLine sampleAssembly
    |> Expect.stringContains "has ns count" "3 ns"

  testCase "openLine shows open statement with kind" <| fun _ ->
    SessionContext.openLine { Name = "System"; IsModule = false; Source = "reflection" }
    |> Expect.equal "namespace open" "open System // namespace via reflection"
    SessionContext.openLine { Name = "MyApp.Utils"; IsModule = true; Source = "source-scan" }
    |> Expect.equal "module open" "open MyApp.Utils // module via source-scan"

  testCase "fileLine shows icon and path" <| fun _ ->
    SessionContext.fileLine { Path = "Domain.fs"; Readiness = Loaded; LastLoadedAt = None; IsWatched = true }
    |> Expect.equal "loaded watched" "● Domain.fs 👁"
    SessionContext.fileLine { Path = "Tests.fs"; Readiness = NotLoaded; LastLoadedAt = None; IsWatched = false }
    |> Expect.equal "not loaded unwatched" "○ Tests.fs"
]
