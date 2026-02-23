module SageFs.Tests.PluginContractTests

/// Tests that verify the JSON/text contracts editors consume from the SageFs API.
/// Every test proven in SageFs REPL first, then persisted here.

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Affordances
open SageFs.AppState
open SageFs.Features.Diagnostics
open SageFs.Features.AutoCompletion

// ─── Helpers ───────────────────────────────────────────────────────

let mkTestWarmup (nFiles: int) (fails: (string * string) list) : WarmupContext =
  { SourceFilesScanned = nFiles
    AssembliesLoaded = [
      { Name = "FSharp.Core"; Path = "/lib/FSharp.Core.dll"; NamespaceCount = 10; ModuleCount = 5 }
      { Name = "Expecto"; Path = "/lib/Expecto.dll"; NamespaceCount = 2; ModuleCount = 3 }
    ]
    NamespacesOpened = [
      { Name = "System"; IsModule = false; Source = "auto" }
      { Name = "Expecto"; IsModule = true; Source = "auto" }
    ]
    FailedOpens = fails
    WarmupDurationMs = 150L
    StartedAt = DateTimeOffset.UtcNow }

let mkTestSessionCtx id projects warmup files : SessionContext =
  { SessionId = id
    ProjectNames = projects
    WorkingDir = "/code/myapp"
    Status = "Ready"
    Warmup = warmup
    FileStatuses = files }

let mkDiag msg sev line col : Diagnostic =
  { Message = msg
    Subcategory = ""
    Range = { StartLine = line; StartColumn = col; EndLine = line; EndColumn = col + 5 }
    Severity = sev }

// ─── SessionContext Rendering ──────────────────────────────────────

[<Tests>]
let sessionCtxRenderTests =
  testList "SessionContext rendering" [
    testCase "renderContent shows session ID" <| fun _ ->
      let ctx = mkTestSessionCtx "abc123" ["MyApp.Tests"] (mkTestWarmup 5 []) []
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "has session id" "abc123"

    testCase "renderContent shows directory" <| fun _ ->
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 3 []) []
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "has dir" "/code/myapp"

    testCase "renderContent shows assembly count" <| fun _ ->
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 3 []) []
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "shows assembly section" "Assemblies (2)"

    testCase "renderContent shows opened namespaces" <| fun _ ->
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 3 []) []
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "shows opened section" "Opened (2)"

    testCase "renderContent shows failed opens count" <| fun _ ->
      let ctx = mkTestSessionCtx "s1" ["T"] (mkTestWarmup 3 [("BadNs", "not found"); ("Also.Bad", "missing")]) []
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "shows 2 fails" "2 fail"

    testCase "renderContent shows warmup duration" <| fun _ ->
      let ctx = mkTestSessionCtx "s1" ["T"] (mkTestWarmup 3 []) []
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "shows 150ms" "150ms"

    testCase "renderContent shows status" <| fun _ ->
      let ctx = mkTestSessionCtx "s1" ["T"] (mkTestWarmup 3 []) []
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "shows Ready" "[Ready]"

    testCase "renderContent includes namespace source" <| fun _ ->
      let ctx = mkTestSessionCtx "s1" ["T"] (mkTestWarmup 3 []) []
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "shows source" "via auto"
  ]

// ─── File Status Rendering ─────────────────────────────────────────

[<Tests>]
let fileStatusRenderTests =
  testList "SessionContext with file statuses" [
    testCase "shows Files section" <| fun _ ->
      let files = [
        { Path = "src/Domain.fs"; Readiness = FileReadiness.Loaded; LastLoadedAt = Some DateTimeOffset.UtcNow; IsWatched = true }
        { Path = "src/App.fs"; Readiness = FileReadiness.NotLoaded; LastLoadedAt = None; IsWatched = false }
      ]
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 5 []) files
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "shows Files section" "Files (2)"

    testCase "loaded count in summary line" <| fun _ ->
      let files = [
        { Path = "a.fs"; Readiness = FileReadiness.Loaded; LastLoadedAt = Some DateTimeOffset.UtcNow; IsWatched = true }
        { Path = "b.fs"; Readiness = FileReadiness.Loaded; LastLoadedAt = Some DateTimeOffset.UtcNow; IsWatched = true }
        { Path = "c.fs"; Readiness = FileReadiness.Stale; LastLoadedAt = None; IsWatched = true }
      ]
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 5 []) files
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "2 of 3 loaded" "2/3 files"

    testCase "stale file marked with tilde" <| fun _ ->
      let files = [
        { Path = "stale.fs"; Readiness = FileReadiness.Stale; LastLoadedAt = Some DateTimeOffset.UtcNow; IsWatched = true }
      ]
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 5 []) files
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "tilde for stale" "~ stale.fs"

    testCase "loaded file marked with bullet" <| fun _ ->
      let files = [
        { Path = "good.fs"; Readiness = FileReadiness.Loaded; LastLoadedAt = Some DateTimeOffset.UtcNow; IsWatched = true }
      ]
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 5 []) files
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "bullet for loaded" "● good.fs"

    testCase "watched file has eye icon" <| fun _ ->
      let files = [
        { Path = "w.fs"; Readiness = FileReadiness.Loaded; LastLoadedAt = Some DateTimeOffset.UtcNow; IsWatched = true }
      ]
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 5 []) files
      SessionContextTui.renderContent ctx
      |> Expect.stringContains "eye for watched" "👁"

    testCase "unwatched file has no eye icon on its line" <| fun _ ->
      let files = [
        { Path = "uw.fs"; Readiness = FileReadiness.NotLoaded; LastLoadedAt = None; IsWatched = false }
      ]
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 5 []) files
      let rendered = SessionContextTui.renderContent ctx
      let fileLine = rendered.Split('\n') |> Array.find (fun l -> l.Contains("uw.fs"))
      fileLine.Contains("👁")
      |> Expect.isFalse "no eye for unwatched"

    testCase "no files section when no file statuses" <| fun _ ->
      let ctx = mkTestSessionCtx "s1" ["Test"] (mkTestWarmup 5 []) []
      SessionContextTui.renderContent ctx
      |> fun r -> r.Contains("Files")
      |> Expect.isFalse "no Files section with empty list"
  ]

// ─── formatEvalResultJson ──────────────────────────────────────────

[<Tests>]
let evalJsonTests =
  testList "formatEvalResultJson" [
    testCase "success result has success=true" <| fun _ ->
      let resp: EvalResponse = { EvaluationResult = Ok "val it: int = 42"; Diagnostics = [||]; EvaluatedCode = "21 * 2;;"; Metadata = Map.empty }
      let json = McpAdapter.formatEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "success is true"

    testCase "success result includes result text" <| fun _ ->
      let resp: EvalResponse = { EvaluationResult = Ok "val it: int = 42"; Diagnostics = [||]; EvaluatedCode = "21 * 2;;"; Metadata = Map.empty }
      let json = McpAdapter.formatEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("result").GetString()
      |> Expect.equal "result text" "val it: int = 42"

    testCase "error result has success=false" <| fun _ ->
      let resp: EvalResponse =
        { EvaluationResult = Error (exn "type mismatch")
          Diagnostics = [| mkDiag "expected int" DiagnosticSeverity.Error 1 5 |]
          EvaluatedCode = """let x: int = "no";;"""
          Metadata = Map.empty }
      let json = McpAdapter.formatEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isFalse "success is false on error"

    testCase "error result includes diagnostics array" <| fun _ ->
      let resp: EvalResponse =
        { EvaluationResult = Error (exn "type mismatch")
          Diagnostics = [| mkDiag "expected int" DiagnosticSeverity.Error 1 5 |]
          EvaluatedCode = "bad code"
          Metadata = Map.empty }
      let json = McpAdapter.formatEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("diagnostics").GetArrayLength()
      |> Expect.equal "1 diagnostic" 1

    testCase "code field preserved in output" <| fun _ ->
      let resp: EvalResponse = { EvaluationResult = Ok "done"; Diagnostics = [||]; EvaluatedCode = "let x = 1;;"; Metadata = Map.empty }
      let json = McpAdapter.formatEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("code").GetString()
      |> Expect.equal "code preserved" "let x = 1;;"
  ]

// ─── formatStatus ──────────────────────────────────────────────────

[<Tests>]
let formatStatusTests =
  testList "formatStatus" [
    testCase "includes session ID" <| fun _ ->
      McpAdapter.formatStatus "sess-42" 5 SessionState.Ready None
      |> Expect.stringContains "has session id" "sess-42"

    testCase "includes event count" <| fun _ ->
      McpAdapter.formatStatus "s1" 17 SessionState.Ready None
      |> Expect.stringContains "has event count" "17"

    testCase "Ready state shown" <| fun _ ->
      McpAdapter.formatStatus "s1" 0 SessionState.Ready None
      |> Expect.stringContains "shows Ready" "Ready"

    testCase "Evaluating state shown" <| fun _ ->
      McpAdapter.formatStatus "s1" 0 SessionState.Evaluating None
      |> Expect.stringContains "shows Evaluating" "Evaluating"

    testCase "with eval stats shows eval count" <| fun _ ->
      let stats: EvalStats = {
        EvalCount = 10
        TotalDuration = TimeSpan.FromMilliseconds(500.0)
        MinDuration = TimeSpan.FromMilliseconds(20.0)
        MaxDuration = TimeSpan.FromMilliseconds(100.0)
      }
      McpAdapter.formatStatus "s1" 3 SessionState.Ready (Some stats)
      |> Expect.stringContains "shows eval count" "10"
  ]

// ─── formatEnhancedStatus ──────────────────────────────────────────

[<Tests>]
let enhancedStatusTests =
  testList "formatEnhancedStatus" [
    testCase "includes session ID" <| fun _ ->
      McpAdapter.formatEnhancedStatus "test-sess" 5 SessionState.Ready None None
      |> Expect.stringContains "has session id" "test-sess"

    testCase "includes event count" <| fun _ ->
      McpAdapter.formatEnhancedStatus "s1" 42 SessionState.Ready None None
      |> Expect.stringContains "has event count" "42"

    testCase "includes state" <| fun _ ->
      McpAdapter.formatEnhancedStatus "s1" 5 SessionState.Evaluating None None
      |> Expect.stringContains "has state" "Evaluating"

    testCase "with eval stats shows Evals line" <| fun _ ->
      let stats: EvalStats = {
        EvalCount = 77
        TotalDuration = TimeSpan.FromMilliseconds(350.0)
        MinDuration = TimeSpan.FromMilliseconds(30.0)
        MaxDuration = TimeSpan.FromMilliseconds(80.0)
      }
      McpAdapter.formatEnhancedStatus "s2" 3 SessionState.Ready (Some stats) None
      |> Expect.stringContains "has eval count" "Evals: 77"
  ]

// ─── formatStartupInfoJson ─────────────────────────────────────────

[<Tests>]
let startupInfoTests =
  testList "formatStartupInfoJson" [
    testCase "returns valid JSON with projects" <| fun _ ->
      let config: AppState.StartupConfig = {
        CommandLineArgs = [| "--proj"; "Test.fsproj" |]
        LoadedProjects = ["Test.fsproj"]
        WorkingDirectory = "/code"
        McpPort = 37749
        HotReloadEnabled = true
        AspireDetected = false
        StartupTimestamp = DateTime.UtcNow
        StartupProfileLoaded = None
      }
      let json = McpAdapter.formatStartupInfoJson config
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("workingDirectory").GetString()
      |> Expect.equal "working dir" "/code"
      root.GetProperty("mcpPort").GetInt32()
      |> Expect.equal "port" 37749
      root.GetProperty("hotReloadEnabled").GetBoolean()
      |> Expect.isTrue "hot reload on"

    testCase "includes loaded projects array" <| fun _ ->
      let config: AppState.StartupConfig = {
        CommandLineArgs = [||]
        LoadedProjects = ["A.fsproj"; "B.fsproj"]
        WorkingDirectory = "/"
        McpPort = 9999
        HotReloadEnabled = false
        AspireDetected = true
        StartupTimestamp = DateTime.UtcNow
        StartupProfileLoaded = None
      }
      let json = McpAdapter.formatStartupInfoJson config
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("loadedProjects").GetArrayLength()
      |> Expect.equal "2 projects" 2
  ]

// ─── splitStatements editor scenarios ──────────────────────────────

[<Tests>]
let editorSplitTests =
  testList "splitStatements editor scenarios" [
    testCase "editor sends single expression" <| fun _ ->
      McpAdapter.splitStatements "1 + 1;;"
      |> Expect.equal "single expr" ["1 + 1;;"]

    testCase "editor sends multi-line with blank lines" <| fun _ ->
      McpAdapter.splitStatements "let x = 1;;\n\nlet y = 2;;"
      |> Expect.equal "two stmts" ["let x = 1;;"; "let y = 2;;"]

    testCase "editor sends code with comments containing ;;" <| fun _ ->
      McpAdapter.splitStatements "// this is not ;; a split\nlet a = 1;;"
      |> Expect.equal "comment ;; ignored" ["// this is not ;; a split\nlet a = 1;;"]

    testCase "editor sends string literal with ;;" <| fun _ ->
      let code = sprintf "let s = %ctest ;; value%c;;" '"' '"'
      McpAdapter.splitStatements code
      |> List.length
      |> Expect.equal "only 1 statement" 1

    testCase "editor sends triple-quoted string with ;;" <| fun _ ->
      let code = "let s = \"\"\"hello ;; world\"\"\";;"
      McpAdapter.splitStatements code
      |> List.length
      |> Expect.equal "only 1 statement" 1

    testCase "editor sends empty string" <| fun _ ->
      McpAdapter.splitStatements ""
      |> Expect.equal "empty gives empty" []

    testCase "editor sends whitespace only" <| fun _ ->
      McpAdapter.splitStatements "   \n  \n  "
      |> Expect.equal "whitespace gives empty" []

    testCase "editor sends block comment with ;;" <| fun _ ->
      McpAdapter.splitStatements "(* block ;; comment *) let x = 1;;"
      |> List.length
      |> Expect.equal "only 1 statement" 1

    testCase "editor sends nested block comments" <| fun _ ->
      McpAdapter.splitStatements "(* outer (* inner ;; *) *) 42;;"
      |> List.length
      |> Expect.equal "only 1 statement" 1
  ]

// ─── formatCompletions ─────────────────────────────────────────────

[<Tests>]
let completionTests =
  testList "formatCompletions" [
    testCase "empty completions says no completions" <| fun _ ->
      McpAdapter.formatCompletions []
      |> Expect.equal "no completions message" "No completions found."

    testCase "formats items as DisplayText (Kind)" <| fun _ ->
      let items = [
        { DisplayText = "ToString"; ReplacementText = "ToString()"; Kind = CompletionKind.Method; GetDescription = None }
        { DisplayText = "Length"; ReplacementText = "Length"; Kind = CompletionKind.Property; GetDescription = None }
      ]
      let result = McpAdapter.formatCompletions items
      result |> Expect.stringContains "first item" "ToString (Method)"
      result |> Expect.stringContains "second item" "Length (Property)"
  ]

// ─── formatCompletionsJson ────────────────────────────────────────────

[<Tests>]
let completionJsonTests =
  testList "formatCompletionsJson" [
    testCase "returns valid JSON with completions array" <| fun _ ->
      let items = [
        { DisplayText = "ToString"; ReplacementText = "ToString"; Kind = CompletionKind.Method; GetDescription = None }
        { DisplayText = "Length"; ReplacementText = "Length"; Kind = CompletionKind.Property; GetDescription = None }
      ]
      let result = McpAdapter.formatCompletionsJson items
      use doc = System.Text.Json.JsonDocument.Parse(result)
      doc.RootElement.GetProperty("completions").GetArrayLength()
      |> Expect.equal "2 completions" 2
      doc.RootElement.GetProperty("count").GetInt32()
      |> Expect.equal "count is 2" 2

    testCase "empty list returns empty completions" <| fun _ ->
      let result = McpAdapter.formatCompletionsJson []
      use doc = System.Text.Json.JsonDocument.Parse(result)
      doc.RootElement.GetProperty("completions").GetArrayLength()
      |> Expect.equal "0 completions" 0

    testCase "completion items have label, kind, insertText" <| fun _ ->
      let items = [
        { DisplayText = "Foo"; ReplacementText = "FooBar"; Kind = CompletionKind.Property; GetDescription = None }
      ]
      let result = McpAdapter.formatCompletionsJson items
      use doc = System.Text.Json.JsonDocument.Parse(result)
      let first = doc.RootElement.GetProperty("completions").EnumerateArray() |> Seq.head
      first.GetProperty("label").GetString()
      |> Expect.equal "label is Foo" "Foo"
      first.GetProperty("kind").GetString()
      |> Expect.equal "kind is Property" "Property"
      first.GetProperty("insertText").GetString()
      |> Expect.equal "insertText is FooBar" "FooBar"
  ]

// ─── formatExplorationResultJson ──────────────────────────────────────

[<Tests>]
let explorationJsonTests =
  testList "formatExplorationResultJson" [
    testCase "empty result returns zero count" <| fun _ ->
      let result = McpAdapter.formatExplorationResultJson "System.IO" []
      use doc = System.Text.Json.JsonDocument.Parse(result)
      doc.RootElement.GetProperty("name").GetString()
      |> Expect.equal "name is System.IO" "System.IO"
      doc.RootElement.GetProperty("totalCount").GetInt32()
      |> Expect.equal "count is 0" 0

    testCase "groups items by kind" <| fun _ ->
      let items = [
        { DisplayText = "ToString"; ReplacementText = "ToString"; Kind = CompletionKind.Method; GetDescription = None }
        { DisplayText = "GetHashCode"; ReplacementText = "GetHashCode"; Kind = CompletionKind.Method; GetDescription = None }
        { DisplayText = "Length"; ReplacementText = "Length"; Kind = CompletionKind.Property; GetDescription = None }
      ]
      let result = McpAdapter.formatExplorationResultJson "System.String" items
      use doc = System.Text.Json.JsonDocument.Parse(result)
      doc.RootElement.GetProperty("totalCount").GetInt32()
      |> Expect.equal "total is 3" 3
      doc.RootElement.GetProperty("groups").GetArrayLength()
      |> Expect.equal "2 groups" 2
  ]
