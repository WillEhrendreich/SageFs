module SageFs.Tests.McpAdapterTests

open Expecto
open Expecto.Flip
open FsCheck
open System
open System.IO
open SageFs
open SageFs.AppState
open SageFs.WorkflowTypes

[<Tests>]
let tests =
  testList "MCP Adapter production code tests" [

    testCase "McpAdapter.formatEvalResult handles successful evaluation"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42"
        Metadata = Map.empty
      }

      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      result |> Expect.stringContains "Should format success with Result: prefix" "Result: val x: int = 42"
      Expect.isFalse "Should NOT echo code back to agent" (result.Contains("Code:"))

    testCase "McpAdapter.formatEvalResult handles error"
    <| fun _ ->
      let ex = Exception("test error")

      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "invalid"
        Metadata = Map.empty
      }

      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      result |> Expect.stringContains "Should format error with Error: prefix" "Error:"

    testCase "McpAdapter.formatEvalResult includes diagnostics on error"
    <| fun _ ->
      let ex = Exception("Operation could not be completed due to earlier error")
      let diag: Features.Diagnostics.Diagnostic = {
        Message = "The type 'DataProtectionProvider' is not defined in 'Microsoft.AspNetCore.DataProtection'"
        Subcategory = "typecheck"
        Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 10 }
        Severity = Features.Diagnostics.DiagnosticSeverity.Error
        ErrorNumber = 39
      }
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [| diag |]
        EvaluatedCode = "let x = DataProtectionProvider.Create(\"test\")"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      result |> Expect.stringContains "Should include diagnostic message" "DataProtectionProvider"
      result |> Expect.stringContains "Should have Diagnostics section" "Diagnostics:"
      Expect.isFalse "Should NOT start with Code:" (result.StartsWith("Code:"))

    testCase "McpAdapter.formatEvalResult does not echo code on error"
    <| fun _ ->
      let ex = Exception("some error")
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "let x = broken()"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      Expect.isFalse "Should NOT have Code section" (result.Contains("Code:"))

    testCase "McpAdapter.formatEvalResult does not echo code on success"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      Expect.isFalse "Should NOT echo code on success" (result.Contains("Code:"))

    testCase "McpAdapter.formatEvalResult includes suggestion for name errors"
    <| fun _ ->
      let ex = Exception("The value or constructor 'foo' is not defined")
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "foo"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      result |> Expect.stringContains "Should include helpful suggestion for name errors" "Tip:"

    testCase "McpAdapter.formatEvalResult includes suggestion for type errors"
    <| fun _ ->
      let ex = Exception("This expression was expected to have type 'int' but here has type 'string'")
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "let x: int = \"hello\""
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      result |> Expect.stringContains "Should include helpful suggestion for type errors" "Tip:"

    // WHY — SageFsErrorException is exactly how a SageFsError travels through
    // this exception-typed channel; formatEvalResult must recover the
    // structured case and use the algebra's own suggestedAction instead of
    // re-deriving one via fragile substring matching (ErrorMessages) over
    // free-form text, the way it does for a plain, unclassified exception.
    testCase "McpAdapter.formatEvalResult uses the SageFsError algebra for a SageFsErrorException"
    <| fun _ ->
      let ex = SageFsErrorException(SageFsError.WorkerCommunicationFailed ("abc123", "connection reset"))
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "1 + 1"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      result
      |> Expect.stringContains "describes the classified case" "Cannot reach session 'abc123': connection reset"
      result
      |> Expect.stringContains "carries the algebra's own suggestedAction verbatim, not a re-derived ErrorMessages Tip" "Run hard_reset_fsi_session"

    testCase "McpAdapter.formatEvents handles empty list"
    <| fun _ ->
      let result = McpAdapter.formatEvents []
      result |> Expect.equal "Should return empty string for empty list" ""

    testCase "McpAdapter.formatEvents formats events correctly"
    <| fun _ ->
      let timestamp = DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)

      let events = [
        (timestamp, "console", "let x = 1")
        (timestamp.AddSeconds(1.0), "mcp:agent1", "val x: int = 1")
      ]

      let result = McpAdapter.formatEvents events
      result |> Expect.stringContains "Should format first event" "console: let x = 1"
      result |> Expect.stringContains "Should format second event" "mcp:agent1: val x: int = 1"
      result |> Expect.stringContains "Should include timestamp brackets" "["

    testCase "McpAdapter.parseScriptFile reads and parses file"
    <| fun _ ->
      let tempFile = Path.GetTempFileName()

      try
        File.WriteAllText(tempFile, "let x = 1;;\nlet y = 2;;")

        let result = McpAdapter.parseScriptFile tempFile

        match result with
        | Ok statements ->
          statements.Length |> Expect.equal "Should have 2 statements" 2
          Expect.isTrue "First statement should contain x" (statements.[0].Contains("let x = 1"))
          Expect.isTrue "Second statement should contain y" (statements.[1].Contains("let y = 2"))
        | Error ex -> failtest $"Should not error: {ex.Message}"
      finally
        File.Delete(tempFile)

    testCase "McpAdapter.parseScriptFile includes comment lines in statements"
    <| fun _ ->
      let tempFile = Path.GetTempFileName()

      try
        File.WriteAllText(tempFile, "// comment\nlet x = 1;;\n// another\nlet y = 2;;")

        let result = McpAdapter.parseScriptFile tempFile

        match result with
        | Ok statements ->
          statements.Length |> Expect.equal "Should have 2 statements" 2
          statements.[0] |> Expect.stringContains "first has comment" "// comment"
          statements.[1] |> Expect.stringContains "second has comment" "// another"
        | Error ex -> failtest $"Should not error: {ex.Message}"
      finally
        File.Delete(tempFile)

    testCase "McpAdapter.parseScriptFile returns error for non-existent file"
    <| fun _ ->
      let result = McpAdapter.parseScriptFile "non-existent-file.fsx"

      match result with
      | Ok _ -> failtest "Should return error for non-existent file"
      | Error ex -> Expect.isNotNull "Should have exception" (box ex)

    testCase "McpAdapter.formatStatus includes session ID and event count"
    <| fun _ ->
      let result = McpAdapter.formatStatus "test-session-123" 42 SageFs.SessionState.Ready None

      result |> Expect.stringContains "Should include session ID" "test-session-123"
      result |> Expect.stringContains "Should include event count" "42"

    testCase "McpAdapter.formatStatus is concise"
    <| fun _ ->
      let result = McpAdapter.formatStatus "abc123" 0 SageFs.SessionState.Ready None
      Expect.isFalse "Should NOT include usage tips" (result.Contains("Usage Tips"))

    testCase "McpAdapter.formatEvalResult handles empty output string"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok ""
        Diagnostics = [||]
        EvaluatedCode = "printfn \"\""
        Metadata = Map.empty
      }

      let result = McpAdapter.formatEvalResult SessionWorkflow.Interactive response
      result |> Expect.stringContains "Should handle empty output" "Result: "
      Expect.isFalse "Should NOT echo code" (result.Contains("Code:"))

    testCase "McpAdapter.formatEvents handles newlines in event text"
    <| fun _ ->
      let timestamp = DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)
      let events = [ (timestamp, "console", "let x = \n    42") ]

      let result = McpAdapter.formatEvents events
      result |> Expect.stringContains "Should preserve newlines in text" "let x = \n    42"

    testCase "McpAdapter.parseScriptFile handles empty file"
    <| fun _ ->
      let tempFile = Path.GetTempFileName()

      try
        File.WriteAllText(tempFile, "")

        let result = McpAdapter.parseScriptFile tempFile

        match result with
        | Ok statements -> statements.Length |> Expect.equal "Empty file should return no statements" 0
        | Error ex -> failtest $"Should not error on empty file: {ex.Message}"
      finally
        File.Delete(tempFile)

    testCase "McpAdapter.parseScriptFile handles file with only comments"
    <| fun _ ->
      let tempFile = Path.GetTempFileName()

      try
        File.WriteAllText(tempFile, "// comment 1\n// comment 2\n// comment 3")

        let result = McpAdapter.parseScriptFile tempFile

        match result with
        | Ok statements ->
          statements.Length |> Expect.equal "Comments are a trailing statement" 1
          statements.[0] |> Expect.stringContains "has comments" "// comment"
        | Error ex -> failtest $"Should not error on comment-only file: {ex.Message}"
      finally
        File.Delete(tempFile)

    testCase "McpAdapter.parseScriptFile handles code without ;; separators"
    <| fun _ ->
      let tempFile = Path.GetTempFileName()

      try
        File.WriteAllText(tempFile, "let x = 1\nlet y = 2")

        let result = McpAdapter.parseScriptFile tempFile

        match result with
        | Ok statements ->
          statements.Length |> Expect.equal "Code without ;; should be one statement" 1
          statements.[0] |> Expect.stringContains "Should contain first line" "let x = 1"
          statements.[0] |> Expect.stringContains "Should contain second line" "let y = 2"
        | Error ex -> failtest $"Should not error: {ex.Message}"
      finally
        File.Delete(tempFile)

    testCase "McpAdapter.splitStatements splits on ;; and re-appends"
    <| fun _ ->
      let result = McpAdapter.splitStatements "let x = 1;; let y = 2;;"
      result.Length |> Expect.equal "Should produce 2 statements" 2
      result.[0] |> Expect.equal "First statement" "let x = 1;;"
      result.[1] |> Expect.equal "Second statement" "let y = 2;;"

    testCase "McpAdapter.splitStatements ignores empty segments"
    <| fun _ ->
      let result = McpAdapter.splitStatements "let x = 1;;   ;; ;;"
      result.Length |> Expect.equal "Should produce 1 statement" 1
      result.[0] |> Expect.equal "Only real statement" "let x = 1;;"

    testCase "McpAdapter.splitStatements ignores ;; inside regular string"
    <| fun _ ->
      let result = McpAdapter.splitStatements """let s = "hello;;world" """
      result.Length |> Expect.equal "1 stmt (;; in string)" 1

    testCase "McpAdapter.splitStatements ignores ;; inside triple-quoted string"
    <| fun _ ->
      let code = "let s = \"\"\"hello;;world\"\"\""
      let result = McpAdapter.splitStatements code
      result.Length |> Expect.equal "1 stmt (;; in triple-quoted)" 1

    testCase "McpAdapter.splitStatements ignores ;; inside verbatim string"
    <| fun _ ->
      let result = McpAdapter.splitStatements """let s = @"hello;;world" """
      result.Length |> Expect.equal "1 stmt (;; in verbatim)" 1

    testCase "McpAdapter.splitStatements ignores ;; inside line comment"
    <| fun _ ->
      let code = "// comment with ;;\nlet x = 1;;"
      let result = McpAdapter.splitStatements code
      result.Length |> Expect.equal "1 stmt (;; in comment)" 1

    testCase "McpAdapter.splitStatements ignores ;; inside block comment"
    <| fun _ ->
      let code = "(* comment ;; *)\nlet x = 1;;"
      let result = McpAdapter.splitStatements code
      result.Length |> Expect.equal "1 stmt (;; in block comment)" 1

    testCase "McpAdapter.splitStatements handles nested block comments"
    <| fun _ ->
      let code = "(* outer (* inner ;; *) *)\nlet x = 1;;"
      let result = McpAdapter.splitStatements code
      result.Length |> Expect.equal "1 stmt (;; in nested comment)" 1

    testCase "McpAdapter.echoStatement writes prompt and code to writer"
    <| fun _ ->
      let sw = new StringWriter()
      McpAdapter.echoStatement sw "let x = 42;;"
      let output = sw.ToString()
      output |> Expect.stringContains "Should have prompt" ">"
      output |> Expect.stringContains "Should have the code without ;;" "let x = 42"
      Expect.isFalse "Should strip ;;" (output.Contains(";;"))

    testCase "McpAdapter.echoStatement echoes each statement separately"
    <| fun _ ->
      let sw = new StringWriter()
      McpAdapter.echoStatement sw "let x = 1;;"
      McpAdapter.echoStatement sw "let y = 2;;"
      let output = sw.ToString()
      output |> Expect.stringContains "First echo" "let x = 1"
      output |> Expect.stringContains "Second echo" "let y = 2"

    testCase "McpAdapter.formatCompletions formats completion items"
    <| fun _ ->
      let items: SageFs.Features.AutoCompletion.CompletionItem list = [
        { DisplayText = "printfn"; ReplacementText = "printfn"; Kind = SageFs.Features.AutoCompletion.CompletionKind.Method; GetDescription = None }
        { DisplayText = "printf"; ReplacementText = "printf"; Kind = SageFs.Features.AutoCompletion.CompletionKind.Method; GetDescription = None }
      ]
      let result = McpAdapter.formatCompletions items
      result |> Expect.stringContains "Should include first completion" "printfn"
      result |> Expect.stringContains "Should include second completion" "printf"
      result |> Expect.stringContains "Should include kind" "Method"

    testCase "McpAdapter.formatCompletions handles empty list"
    <| fun _ ->
      let result = McpAdapter.formatCompletions []
      result |> Expect.stringContains "Should indicate no completions" "No completions"

    testCase "EvalStats.empty has zero counts"
    <| fun _ ->
      let stats = Affordances.EvalStats.empty
      stats.EvalCount |> Expect.equal "zero evals" 0
      stats.TotalDuration |> Expect.equal "zero duration" TimeSpan.Zero

    testCase "EvalStats.record increments count and accumulates duration"
    <| fun _ ->
      let stats =
        Affordances.EvalStats.empty
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 100.0)
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 200.0)
      stats.EvalCount |> Expect.equal "two evals" 2
      stats.TotalDuration |> Expect.equal "total 300ms" (TimeSpan.FromMilliseconds 300.0)
      stats.MinDuration |> Expect.equal "min 100ms" (TimeSpan.FromMilliseconds 100.0)
      stats.MaxDuration |> Expect.equal "max 200ms" (TimeSpan.FromMilliseconds 200.0)

    testCase "EvalStats.averageDuration computes correct average"
    <| fun _ ->
      let stats =
        Affordances.EvalStats.empty
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 100.0)
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 300.0)
      (Affordances.EvalStats.averageDuration stats) |> Expect.equal "avg 200ms" (TimeSpan.FromMilliseconds 200.0)

    testCase "EvalStats.averageDuration returns zero for empty stats"
    <| fun _ ->
      (Affordances.EvalStats.averageDuration Affordances.EvalStats.empty) |> Expect.equal "zero avg" TimeSpan.Zero

    testCase "TextWriterRecorder converts lone LF to CRLF for console"
    <| fun _ ->
      let sw = new StringWriter()
      let recorder = new TextWriterRecorder(sw)
      recorder.Write("hello\nworld\n")
      let output = sw.ToString()
      Expect.isFalse
        "should not have bare LF without CR"
        (output.Contains("\n") && not (output.Contains("\r\n")))
      output |> Expect.stringContains "should have CRLF" "\r\n"
      output |> Expect.equal "exact output" "hello\r\nworld\r\n"

    testCase "TextWriterRecorder converts lone LF char to CRLF"
    <| fun _ ->
      let sw = new StringWriter()
      let recorder = new TextWriterRecorder(sw)
      recorder.Write('h')
      recorder.Write('\n')
      recorder.Write('w')
      let output = sw.ToString()
      output |> Expect.equal "char LF should become CRLF" "h\r\nw"

    testCase "TextWriterRecorder does not double CRLF"
    <| fun _ ->
      let sw = new StringWriter()
      let recorder = new TextWriterRecorder(sw)
      recorder.Write("hello\r\nworld\r\n")
      let output = sw.ToString()
      Expect.isFalse "should not double CR" (output.Contains("\r\r"))
      output |> Expect.equal "CRLF preserved" "hello\r\nworld\r\n"

    testCase "TextWriterRecorder does not double CRLF sent char by char"
    <| fun _ ->
      let sw = new StringWriter()
      let recorder = new TextWriterRecorder(sw)
      for c in "h\r\nw" do
        recorder.Write(c)
      let output = sw.ToString()
      Expect.isFalse "should not double CR" (output.Contains("\r\r"))
      output |> Expect.equal "CRLF chars preserved" "h\r\nw"

    testCase "formatStatus includes eval count when stats provided"
    <| fun _ ->
      let stats =
        Affordances.EvalStats.empty
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 150.0)
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 250.0)
      let result = McpAdapter.formatStatus "test" 5 SessionState.Ready (Some stats)
      result |> Expect.stringContains "has eval count" "Evals: 2"
      result |> Expect.stringContains "has avg" "Avg:"
  ]

open System.Text.Json

[<Tests>]
let structuredOutputTests =
  testList "Structured output format" [

    testCase "formatEvalResultJson returns valid JSON for success"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42;;"
        Metadata = Map.empty
      }
      let json = McpAdapter.formatEvalResultJson response
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      Expect.isTrue "success should be true" (root.GetProperty("success").GetBoolean())
      root.GetProperty("result").GetString() |> Expect.equal "result value" "val x: int = 42"

    testCase "formatEvalResultJson returns valid JSON for error"
    <| fun _ ->
      let ex = Exception("The value or constructor 'foo' is not defined.")
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "foo;;"
        Metadata = Map.empty
      }
      let json = McpAdapter.formatEvalResultJson response
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      Expect.isFalse "success should be false" (root.GetProperty("success").GetBoolean())
      root.GetProperty("error").GetString() |> Expect.stringContains "error message" "foo"

    testCase "formatEvalResultJson includes diagnostics"
    <| fun _ ->
      let ex = Exception("earlier error")
      let diag: Features.Diagnostics.Diagnostic = {
        Message = "Undefined value"
        Subcategory = "typecheck"
        Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 3 }
        Severity = Features.Diagnostics.DiagnosticSeverity.Error
        ErrorNumber = 39
      }
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [| diag |]
        EvaluatedCode = "foo;;"
        Metadata = Map.empty
      }
      let json = McpAdapter.formatEvalResultJson response
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      let diagnostics = root.GetProperty("diagnostics")
      diagnostics.GetArrayLength() |> Expect.equal "one diagnostic" 1
      let d = diagnostics.[0]
      d.GetProperty("severity").GetString() |> Expect.equal "severity" "error"
      d.GetProperty("message").GetString() |> Expect.equal "message" "Undefined value"
      d.GetProperty("startLine").GetInt32() |> Expect.equal "startLine" 1
      d.GetProperty("startColumn").GetInt32() |> Expect.equal "startColumn" 0

    testCase "formatEvalResultJson includes stdout when present"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok "val it: unit = ()"
        Diagnostics = [||]
        EvaluatedCode = """printfn "hello";;"""
        Metadata = Map.ofList [ "stdout", box "hello\n" ]
      }
      let json = McpAdapter.formatEvalResultJson response
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("stdout").GetString() |> Expect.equal "stdout captured" "hello\n"

    testCase "formatEvalResultJson omits null fields"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42;;"
        Metadata = Map.empty
      }
      let json = McpAdapter.formatEvalResultJson response
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      let mutable ignored = Unchecked.defaultof<JsonElement>
      Expect.isFalse "no error field when success" (root.TryGetProperty("error", &ignored))
      Expect.isFalse "no stdout field when empty" (root.TryGetProperty("stdout", &ignored))

    testCase "formatEvalResultJson includes evaluated code"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42;;"
        Metadata = Map.empty
      }
      let json = McpAdapter.formatEvalResultJson response
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("code").GetString() |> Expect.equal "code field" "let x = 42;;"
  ]

[<Tests>]
let jsonFormatVariantTests =
  testList "JSON format variants" [

    testList "formatEventsJson" [
      testCase "empty events returns zero count"
      <| fun _ ->
        let result = McpAdapter.formatEventsJson []
        let doc = JsonDocument.Parse(result)
        doc.RootElement.GetProperty("count").GetInt32()
        |> Expect.equal "should be 0" 0

      testCase "events have timestamp, source, text"
      <| fun _ ->
        let now = DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc)
        let result = McpAdapter.formatEventsJson [(now, "system", "started")]
        let doc = JsonDocument.Parse(result)
        let first = doc.RootElement.GetProperty("events").EnumerateArray() |> Seq.head
        first.GetProperty("source").GetString()
        |> Expect.equal "source should be system" "system"
        first.GetProperty("text").GetString()
        |> Expect.equal "text should be started" "started"

      testCase "count matches number of events"
      <| fun _ ->
        let now = DateTime.UtcNow
        let events = [(now, "a", "1"); (now, "b", "2"); (now, "c", "3")]
        let result = McpAdapter.formatEventsJson events
        let doc = JsonDocument.Parse(result)
        doc.RootElement.GetProperty("count").GetInt32()
        |> Expect.equal "should be 3" 3

      testCase "special characters escaped in JSON"
      <| fun _ ->
        let now = DateTime.UtcNow
        let result = McpAdapter.formatEventsJson [(now, "eval", "line1\nline2")]
        let doc = JsonDocument.Parse(result)
        doc.RootElement.GetProperty("events").GetArrayLength()
        |> Expect.equal "should have 1 event" 1
    ]

    testList "formatStatusJson" [
      testCase "includes sessionId"
      <| fun _ ->
        let result = McpAdapter.formatStatusJson "test-123" 10 SessionState.Ready None
        let doc = JsonDocument.Parse(result)
        doc.RootElement.GetProperty("sessionId").GetString()
        |> Expect.equal "should be test-123" "test-123"

      testCase "includes state"
      <| fun _ ->
        let result = McpAdapter.formatStatusJson "x" 0 SessionState.Evaluating None
        let doc = JsonDocument.Parse(result)
        doc.RootElement.GetProperty("state").GetString()
        |> Expect.equal "should be Evaluating" "Evaluating"

      testCase "includes tools array"
      <| fun _ ->
        let result = McpAdapter.formatStatusJson "x" 0 SessionState.Ready None
        let doc = JsonDocument.Parse(result)
        let toolCount = doc.RootElement.GetProperty("tools").GetArrayLength()
        Expect.isGreaterThan "should have tools" (toolCount, 0)

      testCase "includes eval stats when present"
      <| fun _ ->
        let stats =
          Affordances.EvalStats.empty
          |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 100.0)
          |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 200.0)
          |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 300.0)
        let result = McpAdapter.formatStatusJson "x" 0 SessionState.Ready (Some stats)
        let doc = JsonDocument.Parse(result)
        doc.RootElement.GetProperty("evalStats").GetProperty("count").GetInt32()
        |> Expect.equal "should be 3" 3

      testCase "omits eval stats when None"
      <| fun _ ->
        let result = McpAdapter.formatStatusJson "x" 0 SessionState.Ready None
        let doc = JsonDocument.Parse(result)
        let mutable dummy = Unchecked.defaultof<JsonElement>
        doc.RootElement.TryGetProperty("evalStats", &dummy)
        |> Expect.isFalse "should not have evalStats"
    ]

    testList "formatEnhancedStatusJson" [
      testCase "includes projects from startup config"
      <| fun _ ->
        let cfg : StartupConfig = {
          CommandLineArgs = [||]; LoadedProjects = ["Test.fsproj"]
          WorkingDirectory = "C:\\test"
          Workflow = WorkflowTypes.SessionWorkflow.WebLive WorkflowTypes.BrowserRefreshConfig.defaults; AutoOpenNamespaces = true; AspireDetected = false
          StartupProfileLoaded = None; StartupTimestamp = DateTime.UtcNow
        }
        let result = McpAdapter.formatEnhancedStatusJson "x" 0 SessionState.Ready None (Some cfg)
        let doc = JsonDocument.Parse(result)
        let projects = doc.RootElement.GetProperty("projects")
        projects.GetArrayLength() |> Expect.equal "should have 1 project" 1
        (projects.EnumerateArray() |> Seq.head).GetString()
        |> Expect.equal "should be Test.fsproj" "Test.fsproj"

      testCase "includes startup section"
      <| fun _ ->
        let cfg : StartupConfig = {
          CommandLineArgs = [||]; LoadedProjects = []
          WorkingDirectory = "C:\\work"
          Workflow = WorkflowTypes.SessionWorkflow.Interactive; AutoOpenNamespaces = true; AspireDetected = true
          StartupProfileLoaded = None; StartupTimestamp = DateTime.UtcNow
        }
        let result = McpAdapter.formatEnhancedStatusJson "x" 0 SessionState.Ready None (Some cfg)
        let doc = JsonDocument.Parse(result)
        let startup = doc.RootElement.GetProperty("startup")
        startup.GetProperty("hotReloadEnabled").GetBoolean()
        |> Expect.isFalse "should be false"

      testCase "projects empty when no config"
      <| fun _ ->
        let result = McpAdapter.formatEnhancedStatusJson "x" 0 SessionState.Ready None None
        let doc = JsonDocument.Parse(result)
        doc.RootElement.GetProperty("projects").GetArrayLength()
        |> Expect.equal "should be 0" 0
    ]
  ]

[<Tests>]
let workerEvalJsonTests =
  testList "formatWorkerEvalResultJson" [
    testCase "success result has success=true"
    <| fun _ ->
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Ok "val x: int = 42", [], Map.empty)
      let json = McpAdapter.formatWorkerEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "should be success"

    testCase "success result includes result text"
    <| fun _ ->
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Ok "val x: int = 42", [], Map.empty)
      let json = McpAdapter.formatWorkerEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("result").GetString()
      |> Expect.equal "result text" "val x: int = 42"

    testCase "error result has success=false"
    <| fun _ ->
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Error (SageFsError.EvalFailed "type mismatch"), [], Map.empty)
      let json = McpAdapter.formatWorkerEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isFalse "should not be success"

    testCase "error result includes error message"
    <| fun _ ->
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Error (SageFsError.EvalFailed "type mismatch"), [], Map.empty)
      let json = McpAdapter.formatWorkerEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("error").GetString()
      |> Expect.stringContains "should contain error message" "type mismatch"

    testCase "diagnostics are included as array"
    <| fun _ ->
      let diag : WorkerProtocol.WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Error
          Message = "bad code"; StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 8
          ErrorNumber = 0 }
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Ok "done", [diag], Map.empty)
      let json = McpAdapter.formatWorkerEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("diagnostics").GetArrayLength()
      |> Expect.equal "one diagnostic" 1

    testCase "diagnostic has severity, message, and location"
    <| fun _ ->
      let diag : WorkerProtocol.WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Warning
          Message = "unused binding"; StartLine = 3; StartColumn = 4; EndLine = 3; EndColumn = 10
          ErrorNumber = 0 }
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Ok "done", [diag], Map.empty)
      let json = McpAdapter.formatWorkerEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      let d0 = doc.RootElement.GetProperty("diagnostics").[0]
      d0.GetProperty("severity").GetString() |> Expect.equal "severity" "warning"
      d0.GetProperty("message").GetString() |> Expect.equal "message" "unused binding"
      d0.GetProperty("startLine").GetInt32() |> Expect.equal "startLine" 3

    testCase "WorkerError response has success=false"
    <| fun _ ->
      let resp = WorkerProtocol.WorkerResponse.WorkerError (SageFsError.PipeClosed)
      let json = McpAdapter.formatWorkerEvalResultJson resp
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isFalse "should not be success"

    testCase "quotes in result are escaped"
    <| fun _ ->
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Ok """val s: string = "hello" """, [], Map.empty)
      let json = McpAdapter.formatWorkerEvalResultJson resp
      JsonDocument.Parse(json) |> ignore
      json |> Expect.stringContains "contains escaped quote" "\\\""

    testCase "formatWorkerEvalResult TEXT includes the (line,col) span for a positioned diagnostic (dogfood F4)"
    <| fun _ ->
      let diag : WorkerProtocol.WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Error
          Message = "type mismatch"; StartLine = 3; StartColumn = 7; EndLine = 3; EndColumn = 9
          ErrorNumber = 1 }
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Ok "ok", [diag], Map.empty)
      let text = McpTools.formatWorkerEvalResult WorkflowTypes.SessionWorkflow.Interactive resp
      text |> Expect.stringContains "the diagnostic text carries the span so an agent can edit surgically" "(3,7)"

    testCase "formatWorkerEvalResult TEXT omits a spurious span for an unpositioned diagnostic (dogfood F4)"
    <| fun _ ->
      let diag : WorkerProtocol.WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Error
          Message = "no position"; StartLine = 0; StartColumn = 0; EndLine = 0; EndColumn = 0
          ErrorNumber = 0 }
      let resp = WorkerProtocol.WorkerResponse.EvalResult("r1", Ok "ok", [diag], Map.empty)
      let text = McpTools.formatWorkerEvalResult WorkflowTypes.SessionWorkflow.Interactive resp
      text |> Expect.stringContains "still shows the message" "no position"
      text.Contains "(0,0)" |> Expect.isFalse "no spurious (0,0) span for an unpositioned diagnostic"

    testList "escapeJson property" [
      testProperty "escaped output roundtrips through JSON" <| fun (s: string) ->
        match isNull s with
        | true -> ()
        | false ->
          let escaped = McpAdapter.escapeJson s
          let json = sprintf """{"v":"%s"}""" escaped
          use doc = System.Text.Json.JsonDocument.Parse(json)
          let roundtripped = doc.RootElement.GetProperty("v").GetString()
          roundtripped |> Expect.equal "roundtrip should preserve value" s
    ]

    testList "file type detection" [
      testCase "isSolutionFile detects .sln" <| fun _ ->
        McpAdapter.isSolutionFile "Foo.sln" |> Expect.isTrue "should detect .sln"

      testCase "isSolutionFile detects .slnx" <| fun _ ->
        McpAdapter.isSolutionFile "Foo.slnx" |> Expect.isTrue "should detect .slnx"

      testCase "isSolutionFile rejects .fsproj" <| fun _ ->
        McpAdapter.isSolutionFile "Foo.fsproj" |> Expect.isFalse "should reject .fsproj"

      testCase "isSolutionFile is case-sensitive" <| fun _ ->
        McpAdapter.isSolutionFile "Foo.SLN" |> Expect.isFalse "should be case-sensitive"

      testCase "isProjectFile detects .fsproj" <| fun _ ->
        McpAdapter.isProjectFile "Foo.fsproj" |> Expect.isTrue "should detect .fsproj"

      testCase "isProjectFile rejects .csproj" <| fun _ ->
        McpAdapter.isProjectFile "Foo.csproj" |> Expect.isFalse "should reject .csproj"

      testCase "isProjectFile rejects .sln" <| fun _ ->
        McpAdapter.isProjectFile "Foo.sln" |> Expect.isFalse "should reject .sln"
    ]

    testList "splitStatements properties" [
      testProperty "no output statement is empty" <| fun (s: string) ->
        match isNull s with
        | true -> ()
        | false ->
          let stmts = McpAdapter.splitStatements s
          for stmt in stmts do
            (stmt.Trim().Length > 0) |> Expect.isTrue "no statement should be empty/whitespace"

      testCase "escaped backslash before quote in string" <| fun _ ->
        let stmts = McpAdapter.splitStatements """let s = "a\\";; let y = 1;;"""
        stmts |> Expect.hasLength "should have 2 stmts" 2

      testCase "empty string between double semicolons" <| fun _ ->
        let stmts = McpAdapter.splitStatements ";;;;"
        stmts |> Expect.equal "should be empty" []

      testCase "multiple newlines between statements" <| fun _ ->
        let stmts = McpAdapter.splitStatements "let x = 1;;\n\n\n\nlet y = 2;;"
        stmts |> Expect.hasLength "should have 2 stmts" 2

      testCase "statement with only whitespace before ;;" <| fun _ ->
        let stmts = McpAdapter.splitStatements "   ;;"
        stmts |> Expect.equal "whitespace-only segment is dropped" []

      testCase "hash directive preserved" <| fun _ ->
        let stmts = McpAdapter.splitStatements "#r \"nuget: FSharp.Data\";;\nlet x = 1;;"
        stmts |> Expect.hasLength "should have 2 stmts" 2
        stmts.[0] |> Expect.stringContains "first should be directive" "#r"
    ]
  ]

/// roast-7 §2/§16 item 2: `send_fsharp_code` used to flatten every result to
/// a `sprintf "Result: %s"` / `"Error: %s"` string even though structured
/// data (diagnostics with spans, a classified SageFsError) already existed.
/// These test the two new pure builders directly — the same style as
/// `formatWorkerEvalResultJson`'s tests above — without needing a live FSI
/// session; `McpToolExecutionTests.fs` covers the actual tool wiring.
[<Tests>]
let evalStructuredContentTests =
  testList "send_fsharp_code structured content (roast-7 §2)" [

    testCase "success payload has success=true and the result text"
    <| fun _ ->
      let json = McpAdapter.formatEvalStructuredSuccess "val x: int = 42" []
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("success").GetBoolean() |> Expect.isTrue "should be success"
      doc.RootElement.GetProperty("result").GetString() |> Expect.equal "result text" "val x: int = 42"

    testCase "success payload carries diagnostics with spans, not flattened text"
    <| fun _ ->
      let diag : WorkerProtocol.WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Warning
          Message = "unused binding"; StartLine = 3; StartColumn = 4; EndLine = 3; EndColumn = 10
          ErrorNumber = 0 }
      let json = McpAdapter.formatEvalStructuredSuccess "val x: int = 42" [diag]
      let doc = JsonDocument.Parse(json)
      let d0 = doc.RootElement.GetProperty("diagnostics").[0]
      d0.GetProperty("startLine").GetInt32() |> Expect.equal "startLine span" 3
      d0.GetProperty("startColumn").GetInt32() |> Expect.equal "startColumn span" 4
      d0.GetProperty("endLine").GetInt32() |> Expect.equal "endLine span" 3
      d0.GetProperty("endColumn").GetInt32() |> Expect.equal "endColumn span" 10

    testCase "failure payload is the full SageFsError algebra, not a flattened message"
    <| fun _ ->
      let err = SageFsError.EvalFailed "type mismatch"
      let json = McpAdapter.formatEvalStructuredError err
      let doc = JsonDocument.Parse(json)
      // Same shape as McpServer.structuredToolErrorResult / SageFsError.toJson:
      // case / message / suggestedAction — never just an "error" string.
      doc.RootElement.GetProperty("case").GetString() |> Expect.equal "case token" "EvalFailed"
      doc.RootElement.GetProperty("message").GetString() |> Expect.stringContains "message" "type mismatch"
      doc.RootElement.TryGetProperty("suggestedAction") |> fst |> Expect.isTrue "must carry a suggestedAction"
  ]

/// roast-7 §13/§16 item 13: the only enforced size limit before this was
/// INBOUND (4 MiB on the request body) — outbound tool results had no
/// ceiling before landing in an agent's context window.
[<Tests>]
let outboundCapTests =
  testList "truncateForOutbound (roast-7 §13)" [

    testCase "text under the cap is returned unchanged"
    <| fun _ ->
      let text = "val x: int = 42"
      McpAdapter.truncateForOutbound 1000 text |> Expect.equal "unchanged" text

    testCase "text over the cap is truncated with a marker"
    <| fun _ ->
      let text = String.replicate 100 "x"
      let result = McpAdapter.truncateForOutbound 20 text
      (System.Text.Encoding.UTF8.GetByteCount(result) <= 20 + 40)
      |> Expect.isTrue "result should be close to the byte budget (text + short marker)"
      result |> Expect.stringContains "should carry an explicit truncation marker" "truncated"
      (result.Length < text.Length) |> Expect.isTrue "should actually be shorter than the input"

    testCase "truncated result never exceeds the byte cap"
    <| fun _ ->
      let text = String.replicate 5000 "abcdefghij"
      let cap = 500
      let result = McpAdapter.truncateForOutbound cap text
      (System.Text.Encoding.UTF8.GetByteCount(result) <= cap)
      |> Expect.isTrue "must never exceed the requested byte cap"

    testProperty "truncated output never exceeds the byte cap by more than a marker's worth (property)"
    <| fun (s: NonNull<string>) (PositiveInt capSeed) ->
      let cap = (capSeed % 4096) + 1
      let result = McpAdapter.truncateForOutbound cap s.Get
      // A generous slack (128 bytes) above the requested cap: the marker
      // itself is appended after the budget is computed, and its own size
      // varies with how many bytes were cut, so the hard guarantee is
      // "close to the cap", not "never one byte over" for a pathological
      // marker/cap combination — this still catches any truncation that is
      // unboundedly wrong (e.g. returning the untruncated input).
      System.Text.Encoding.UTF8.GetByteCount(result) <= cap + 128
  ]
