module SageFs.Tests.WarmupContextTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState
open SageFs.WarmUp
open SageFs.McpAdapter
open SageFs.WorkflowTypes

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
    { Name = "System"; Kind = OpenableKind.Namespace; Source = "reflection"; DurationMs = 0.0 }
    { Name = "System.IO"; Kind = OpenableKind.Namespace; Source = "reflection"; DurationMs = 0.0 }
    { Name = "MyApp.Utils"; Kind = OpenableKind.Module; Source = "source-scan"; DurationMs = 0.0 }
    { Name = "MyApp.Domain"; Kind = OpenableKind.Namespace; Source = "source-scan"; DurationMs = 0.0 }
  ]
  FailedOpens = [ { Name = "BrokenNs"; Kind = OpenableKind.Namespace; ErrorMessage = "type not found"; Diagnostics = []; RetryCount = 1; DurationMs = 0.0 } ]
  PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 1234L }
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
  Workflow = WorkflowTypes.SessionWorkflow.Interactive
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
    SessionContext.openLine { Name = "System"; Kind = OpenableKind.Namespace; Source = "reflection"; DurationMs = 0.0 }
    |> Expect.equal "namespace open" "open System // namespace via reflection"
    SessionContext.openLine { Name = "MyApp.Utils"; Kind = OpenableKind.Module; Source = "source-scan"; DurationMs = 0.0 }
    |> Expect.equal "module open" "open MyApp.Utils // module via source-scan"

  testCase "fileLine shows icon and path" <| fun _ ->
    SessionContext.fileLine { Path = "Domain.fs"; Readiness = Loaded; LastLoadedAt = None; IsWatched = true }
    |> Expect.equal "loaded watched" "● Domain.fs 👁"
    SessionContext.fileLine { Path = "Tests.fs"; Readiness = NotLoaded; LastLoadedAt = None; IsWatched = false }
    |> Expect.equal "not loaded unwatched" "○ Tests.fs"
]

let sampleTuiSession: SessionContext = {
  SessionId = "abc123"
  ProjectNames = ["MyApp.fsproj"]
  WorkingDir = @"C:\Code\MyProject"
  Status = "Ready"
  Warmup = {
    SourceFilesScanned = 5
    AssembliesLoaded = [
      { Name = "MyApp"; Path = "/bin/MyApp.dll"; NamespaceCount = 3; ModuleCount = 2 }
      { Name = "MyLib"; Path = "/bin/MyLib.dll"; NamespaceCount = 1; ModuleCount = 0 }
    ]
    NamespacesOpened = [
      { Name = "System"; Kind = OpenableKind.Namespace; Source = "MyApp"; DurationMs = 0.0 }
      { Name = "System.IO"; Kind = OpenableKind.Namespace; Source = "MyApp"; DurationMs = 0.0 }
      { Name = "MyApp.Domain"; Kind = OpenableKind.Module; Source = "MyApp"; DurationMs = 0.0 }
      { Name = "MyLib.Utils"; Kind = OpenableKind.Module; Source = "MyLib"; DurationMs = 0.0 }
    ]
    FailedOpens = [ { Name = "Bogus.Ns"; Kind = OpenableKind.Namespace; ErrorMessage = "Type not found"; Diagnostics = []; RetryCount = 1; DurationMs = 0.0 } ]
    PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 450L }
    StartedAt = System.DateTimeOffset.UtcNow
  }
  FileStatuses = [
    { Path = "src/Domain.fs"; Readiness = Loaded; LastLoadedAt = Some System.DateTimeOffset.UtcNow; IsWatched = true }
    { Path = "src/App.fs"; Readiness = Loaded; LastLoadedAt = Some System.DateTimeOffset.UtcNow; IsWatched = false }
    { Path = "src/Startup.fs"; Readiness = NotLoaded; LastLoadedAt = None; IsWatched = false }
    { Path = "src/Old.fs"; Readiness = Stale; LastLoadedAt = Some (System.DateTimeOffset.UtcNow.AddHours(-1)); IsWatched = true }
    { Path = "src/Broken.fs"; Readiness = LoadFailed; LastLoadedAt = None; IsWatched = false }
  ]
  Workflow = WorkflowTypes.SessionWorkflow.Interactive
}

[<Tests>]
let sessionContextTuiTests= testList "SessionContextTui" [
  testCase "summaryLine contains status, file counts, ns counts, duration" <| fun _ ->
    let line = SessionContextTui.summaryLine sampleTuiSession
    line |> Expect.stringContains "has status" "[Ready]"
    line |> Expect.stringContains "file ratio" "2/5"
    line |> Expect.stringContains "ns count" "4 ns"
    line |> Expect.stringContains "fail count" "1 fail"
    line |> Expect.stringContains "duration" "450ms"

  testCase "summaryLine empty session shows zeros" <| fun _ ->
    let empty = {
      SessionId = "x"; ProjectNames = []; WorkingDir = "."
      Status = "Starting"; Warmup = WarmupContext.empty; FileStatuses = []
      Workflow = SessionWorkflow.Interactive
    }
    let line = SessionContextTui.summaryLine empty
    line |> Expect.stringContains "zero files" "0/0"
    line |> Expect.stringContains "zero ns" "0 ns"

  testCase "detailLines has all section headers" <| fun _ ->
    let lines = SessionContextTui.detailLines sampleTuiSession
    lines |> List.exists (fun l -> l.Contains("Assemblies")) |> Expect.isTrue "assemblies header"
    lines |> List.exists (fun l -> l.Contains("Opened")) |> Expect.isTrue "opened header"
    lines |> List.exists (fun l -> l.Contains("Failed")) |> Expect.isTrue "failed header"
    lines |> List.exists (fun l -> l.Contains("Files")) |> Expect.isTrue "files header"

  testCase "detailLines includes assembly info" <| fun _ ->
    let lines = SessionContextTui.detailLines sampleTuiSession
    lines |> List.exists (fun l -> l.Contains("MyApp") && l.Contains("3 ns")) |> Expect.isTrue "MyApp assembly"
    lines |> List.exists (fun l -> l.Contains("MyLib")) |> Expect.isTrue "MyLib assembly"

  testCase "detailLines includes open statements" <| fun _ ->
    let lines = SessionContextTui.detailLines sampleTuiSession
    lines |> List.exists (fun l -> l.Contains("open System") && l.Contains("namespace")) |> Expect.isTrue "System ns"
    lines |> List.exists (fun l -> l.Contains("open MyApp.Domain") && l.Contains("module")) |> Expect.isTrue "Domain module"

  testCase "detailLines shows file readiness icons" <| fun _ ->
    let lines = SessionContextTui.detailLines sampleTuiSession
    lines |> List.exists (fun l -> l.Contains("●") && l.Contains("Domain.fs")) |> Expect.isTrue "loaded+watched"
    lines |> List.exists (fun l -> l.Contains("○") && l.Contains("Startup.fs")) |> Expect.isTrue "not loaded"
    lines |> List.exists (fun l -> l.Contains("~") && l.Contains("Old.fs")) |> Expect.isTrue "stale"
    lines |> List.exists (fun l -> l.Contains("✖") && l.Contains("Broken.fs")) |> Expect.isTrue "failed"

  testCase "detailLines shows failed opens" <| fun _ ->
    let lines = SessionContextTui.detailLines sampleTuiSession
    lines |> List.exists (fun l -> l.Contains("Bogus.Ns") && l.Contains("Type not found")) |> Expect.isTrue "failed open"

  testCase "renderContent joins summary + details with many lines" <| fun _ ->
    let content = SessionContextTui.renderContent sampleTuiSession
    let lines = content.Split('\n')
    lines.[0] |> Expect.stringContains "first line is summary" "[Ready]"
    (lines.Length, 10) |> Expect.isGreaterThan "has many lines"

  testCase "detailLines omits empty sections" <| fun _ ->
    let minimal = {
      SessionId = "m"; ProjectNames = []; WorkingDir = "."
      Status = "Ready"; Warmup = WarmupContext.empty; FileStatuses = []
      Workflow = SessionWorkflow.Interactive
    }
    let lines = SessionContextTui.detailLines minimal
    lines |> List.exists (fun l -> l.Contains("Assemblies")) |> Expect.isFalse "no assemblies section"
    lines |> List.exists (fun l -> l.Contains("Failed")) |> Expect.isFalse "no failed section"
    lines |> List.exists (fun l -> l.Contains("Files")) |> Expect.isFalse "no files section"
]

let mkLlmAsm name ns mods : LoadedAssembly =
  { Name = name; Path = sprintf "%s.dll" name; NamespaceCount = ns; ModuleCount = mods }

let mkLlmOpen name : OpenedBinding =
  { Name = name; Kind = OpenableKind.Namespace; Source = "warmup"; DurationMs = 0.0 }

let mkLlmFile path readiness : FileStatus =
  { Path = path; Readiness = readiness; LastLoadedAt = None; IsWatched = true }

[<Tests>]
let formatWarmupDetailForLlmTests = testList "formatWarmupDetailForLlm" [
  testCase "healthy session shows assemblies and opened namespaces" <| fun _ ->
    let ctx : SessionContext = {
      SessionId = "abc123"
      ProjectNames = ["MyProj"]
      WorkingDir = "/code"
      Status = "Ready"
      Warmup = {
        AssembliesLoaded = [mkLlmAsm "Asm1" 3 1; mkLlmAsm "Asm2" 2 0]
        NamespacesOpened = [mkLlmOpen "System"; mkLlmOpen "System.IO"]
        FailedOpens = []
        PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 890L }
        SourceFilesScanned = 5
        StartedAt = System.DateTimeOffset.UtcNow
      }
      FileStatuses = [mkLlmFile "a.fs" Loaded; mkLlmFile "b.fs" Loaded]
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let result = formatWarmupDetailForLlm ctx
    result |> Expect.stringContains "asm1""📦 Asm1 (3 ns, 1 modules)"
    result |> Expect.stringContains "asm2" "📦 Asm2 (2 ns, 0 modules)"
    result |> Expect.stringContains "open System" "open System // namespace"
    result |> Expect.stringContains "open System.IO" "open System.IO // namespace"

  testCase "failed opens show warning section" <| fun _ ->
    let ctx : SessionContext = {
      SessionId = "def456"
      ProjectNames = ["BadProj"]
      WorkingDir = "/code"
      Status = "Ready"
      Warmup = {
        AssembliesLoaded = [mkLlmAsm "Asm1" 3 1]
        NamespacesOpened = [mkLlmOpen "System"]
        FailedOpens = [{ Name = "Bad.Ns"; Kind = OpenableKind.Namespace; ErrorMessage = "not found"; Diagnostics = []; RetryCount = 1; DurationMs = 0.0 }]
        PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 1200L }
        SourceFilesScanned = 3
        StartedAt = System.DateTimeOffset.UtcNow
      }
      FileStatuses = [mkLlmFile "good.fs" Loaded]
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let result = formatWarmupDetailForLlm ctx
    result |> Expect.stringContains "summary ratio" "1/2 namespaces opened"
    result |> Expect.stringContains "failed section""⚠ Failed opens (1):"
    result |> Expect.stringContains "failed detail" "✖ Bad.Ns (namespace) — not found"

  testCase "failed file loads show in files section" <| fun _ ->
    let ctx : SessionContext = {
      SessionId = "ghi789"
      ProjectNames = ["Proj"]
      WorkingDir = "/code"
      Status = "Ready"
      Warmup = {
        AssembliesLoaded = [mkLlmAsm "Asm1" 2 1]
        NamespacesOpened = [mkLlmOpen "System"]
        FailedOpens = []
        PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 500L }
        SourceFilesScanned = 2
        StartedAt = System.DateTimeOffset.UtcNow
      }
      FileStatuses = [mkLlmFile "good.fs" Loaded; mkLlmFile "broken.fs" LoadFailed]
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let result = formatWarmupDetailForLlm ctx
    result |> Expect.stringContains "files header""Files (1/2 loaded):"
    result |> Expect.stringContains "loaded file" "● good.fs"
    result |> Expect.stringContains "failed file" "✖ broken.fs"

  testCase "empty session shows zero summary" <| fun _ ->
    let ctx : SessionContext = {
      SessionId = "empty"
      ProjectNames = []
      WorkingDir = ""
      Status = "Starting"
      Warmup = WarmupContext.empty
      FileStatuses = []
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let result = formatWarmupDetailForLlm ctx
    result |> Expect.stringContains "zero summary""0 assemblies, 0/0 namespaces opened, 0ms"

  testCase "modules show as module not namespace" <| fun _ ->
    let ctx : SessionContext = {
      SessionId = "mod"
      ProjectNames = ["P"]
      WorkingDir = "/code"
      Status = "Ready"
      Warmup = {
        AssembliesLoaded = [mkLlmAsm "A" 1 1]
        NamespacesOpened = [
          { Name = "MyModule"; Kind = OpenableKind.Module; Source = "warmup"; DurationMs = 0.0 }
        ]
        FailedOpens = []
        PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 100L }
        SourceFilesScanned = 1
        StartedAt = System.DateTimeOffset.UtcNow
      }
      FileStatuses = []
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
    }
    let result = formatWarmupDetailForLlm ctx
    result |> Expect.stringContains "module kind" "open MyModule // module"
]

[<Tests>]
let extractOpensTests = testList "extractOpensFromLines" [

  testCase "returns empty for empty input" <| fun _ ->
    extractOpensFromLines [||]
    |> Expect.isEmpty "empty lines should yield no opens"

  testCase "extracts single open statement" <| fun _ ->
    extractOpensFromLines [| "open System" |]
    |> Expect.equal "single open" [| "System" |]

  testCase "extracts multiple distinct opens" <| fun _ ->
    let lines = [| "open System"; "open System.IO"; "open FSharp.Collections" |]
    extractOpensFromLines lines
    |> Expect.equal "three opens" [| "System"; "System.IO"; "FSharp.Collections" |]

  testCase "deduplicates repeated opens" <| fun _ ->
    let lines = [| "open System"; "open System.IO"; "open System" |]
    extractOpensFromLines lines
    |> Expect.equal "deduped" [| "System"; "System.IO" |]

  testCase "ignores non-open lines" <| fun _ ->
    let lines = [| "module Foo"; "let x = 1"; "open System"; "type T = {}" |]
    extractOpensFromLines lines
    |> Expect.equal "only open" [| "System" |]

  testCase "ignores commented-out open lines" <| fun _ ->
    let lines = [| "// open System"; "open System.IO" |]
    extractOpensFromLines lines
    |> Expect.equal "skip comment" [| "System.IO" |]

  testCase "handles leading whitespace" <| fun _ ->
    let lines = [| "  open System.Collections.Generic" |]
    extractOpensFromLines lines
    |> Expect.equal "indented open" [| "System.Collections.Generic" |]

  testCase "handles trailing semicolons" <| fun _ ->
    let lines = [| "open System;;" |]
    extractOpensFromLines lines
    |> Expect.equal "semicolons stripped" [| "System" |]

  testCase "ignores blank lines" <| fun _ ->
    let lines = [| ""; "open System"; "   "; "open System.IO" |]
    extractOpensFromLines lines
    |> Expect.equal "blank lines skipped" [| "System"; "System.IO" |]

  testCase "parallel scan over file batches returns same set as sequential" <| fun _ ->
    let fileContents =
      [|
        [| "open System"; "open System.IO" |]
        [| "open System.IO"; "open FSharp.Collections" |]
        [| "module Foo"; "open System"; "open Expecto" |]
      |]
    let seqResult =
      fileContents
      |> Array.collect extractOpensFromLines
      |> Array.distinct
      |> Set.ofArray
    let parResult =
      fileContents
      |> Array.Parallel.map extractOpensFromLines
      |> Array.collect id
      |> Array.distinct
      |> Set.ofArray
    parResult
    |> Expect.equal "parallel matches sequential" seqResult
]
