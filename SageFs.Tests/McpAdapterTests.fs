module SageFs.Tests.McpAdapterTests

open Expecto
open System
open System.IO
open SageFs
open SageFs.AppState

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

      let result = McpAdapter.formatEvalResult response
      Expect.stringContains result "Result: val x: int = 42" "Should format success with Result: prefix"
      Expect.isFalse (result.Contains("Code:")) "Should NOT echo code back to agent"

    testCase "McpAdapter.formatEvalResult handles error"
    <| fun _ ->
      let ex = Exception("test error")

      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "invalid"
        Metadata = Map.empty
      }

      let result = McpAdapter.formatEvalResult response
      Expect.stringContains result "Error:" "Should format error with Error: prefix"

    testCase "McpAdapter.formatEvalResult includes diagnostics on error"
    <| fun _ ->
      let ex = Exception("Operation could not be completed due to earlier error")
      let diag: Features.Diagnostics.Diagnostic = {
        Message = "The type 'DataProtectionProvider' is not defined in 'Microsoft.AspNetCore.DataProtection'"
        Subcategory = "typecheck"
        Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 10 }
        Severity = Features.Diagnostics.DiagnosticSeverity.Error
      }
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [| diag |]
        EvaluatedCode = "let x = DataProtectionProvider.Create(\"test\")"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult response
      Expect.stringContains result "DataProtectionProvider" "Should include diagnostic message"
      Expect.stringContains result "Diagnostics:" "Should have Diagnostics section"
      Expect.isFalse (result.StartsWith("Code:")) "Should NOT start with Code:"

    testCase "McpAdapter.formatEvalResult does not echo code on error"
    <| fun _ ->
      let ex = Exception("some error")
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "let x = broken()"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult response
      Expect.isFalse (result.Contains("Code:")) "Should NOT have Code section"

    testCase "McpAdapter.formatEvalResult does not echo code on success"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult response
      Expect.isFalse (result.Contains("Code:")) "Should NOT echo code on success"

    testCase "McpAdapter.formatEvalResult includes suggestion for name errors"
    <| fun _ ->
      let ex = Exception("The value or constructor 'foo' is not defined")
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "foo"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult response
      Expect.stringContains result "Tip:" "Should include helpful suggestion for name errors"

    testCase "McpAdapter.formatEvalResult includes suggestion for type errors"
    <| fun _ ->
      let ex = Exception("This expression was expected to have type 'int' but here has type 'string'")
      let response: EvalResponse = {
        EvaluationResult = Error ex
        Diagnostics = [||]
        EvaluatedCode = "let x: int = \"hello\""
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult response
      Expect.stringContains result "Tip:" "Should include helpful suggestion for type errors"

    testCase "McpAdapter.formatEvents handles empty list"
    <| fun _ ->
      let result = McpAdapter.formatEvents []
      Expect.equal result "" "Should return empty string for empty list"

    testCase "McpAdapter.formatEvents formats events correctly"
    <| fun _ ->
      let timestamp = DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)

      let events = [
        (timestamp, "console", "let x = 1")
        (timestamp.AddSeconds(1.0), "mcp:agent1", "val x: int = 1")
      ]

      let result = McpAdapter.formatEvents events
      Expect.stringContains result "console: let x = 1" "Should format first event"
      Expect.stringContains result "mcp:agent1: val x: int = 1" "Should format second event"
      Expect.stringContains result "[" "Should include timestamp brackets"

    testCase "McpAdapter.parseScriptFile reads and parses file"
    <| fun _ ->
      let tempFile = Path.GetTempFileName()

      try
        File.WriteAllText(tempFile, "let x = 1;;\nlet y = 2;;")

        let result = McpAdapter.parseScriptFile tempFile

        match result with
        | Ok statements ->
          Expect.equal statements.Length 2 "Should have 2 statements"
          Expect.isTrue (statements.[0].Contains("let x = 1")) "First statement should contain x"
          Expect.isTrue (statements.[1].Contains("let y = 2")) "Second statement should contain y"
        | Error ex -> failtest $"Should not error: {ex.Message}"
      finally
        File.Delete(tempFile)

    testCase "McpAdapter.parseScriptFile filters comment lines"
    <| fun _ ->
      let tempFile = Path.GetTempFileName()

      try
        File.WriteAllText(tempFile, "// comment\nlet x = 1;;\n// another\nlet y = 2;;")

        let result = McpAdapter.parseScriptFile tempFile

        match result with
        | Ok statements ->
          Expect.equal statements.Length 2 "Should have 2 non-comment statements"

          statements
          |> List.iter (fun stmt -> Expect.isFalse (stmt.Contains("//")) "Statements should not contain comment lines")
        | Error ex -> failtest $"Should not error: {ex.Message}"
      finally
        File.Delete(tempFile)

    testCase "McpAdapter.parseScriptFile returns error for non-existent file"
    <| fun _ ->
      let result = McpAdapter.parseScriptFile "non-existent-file.fsx"

      match result with
      | Ok _ -> failtest "Should return error for non-existent file"
      | Error ex -> Expect.isNotNull (box ex) "Should have exception"

    testCase "McpAdapter.formatStatus includes session ID and event count"
    <| fun _ ->
      let result = McpAdapter.formatStatus "test-session-123" 42 SageFs.SessionState.Ready None

      Expect.stringContains result "test-session-123" "Should include session ID"
      Expect.stringContains result "42" "Should include event count"

    testCase "McpAdapter.formatStatus is concise"
    <| fun _ ->
      let result = McpAdapter.formatStatus "abc123" 0 SageFs.SessionState.Ready None
      Expect.isFalse (result.Contains("Usage Tips")) "Should NOT include usage tips"

    testCase "McpAdapter.formatEvalResult handles empty output string"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok ""
        Diagnostics = [||]
        EvaluatedCode = "printfn \"\""
        Metadata = Map.empty
      }

      let result = McpAdapter.formatEvalResult response
      Expect.stringContains result "Result: " "Should handle empty output"
      Expect.isFalse (result.Contains("Code:")) "Should NOT echo code"

    testCase "McpAdapter.formatEvents handles newlines in event text"
    <| fun _ ->
      let timestamp = DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)
      let events = [ (timestamp, "console", "let x = \n    42") ]

      let result = McpAdapter.formatEvents events
      Expect.stringContains result "let x = \n    42" "Should preserve newlines in text"

    testCase "McpAdapter.parseScriptFile handles empty file"
    <| fun _ ->
      let tempFile = Path.GetTempFileName()

      try
        File.WriteAllText(tempFile, "")

        let result = McpAdapter.parseScriptFile tempFile

        match result with
        | Ok statements -> Expect.equal statements.Length 0 "Empty file should return no statements"
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
        | Ok statements -> Expect.equal statements.Length 0 "File with only comments should return no statements"
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
          Expect.equal statements.Length 1 "Code without ;; should be one statement"
          Expect.stringContains statements.[0] "let x = 1" "Should contain first line"
          Expect.stringContains statements.[0] "let y = 2" "Should contain second line"
        | Error ex -> failtest $"Should not error: {ex.Message}"
      finally
        File.Delete(tempFile)

    testCase "McpAdapter.splitStatements splits on ;; and re-appends"
    <| fun _ ->
      let result = McpAdapter.splitStatements "let x = 1;; let y = 2;;"
      Expect.equal result.Length 2 "Should produce 2 statements"
      Expect.equal result.[0] "let x = 1;;" "First statement"
      Expect.equal result.[1] "let y = 2;;" "Second statement"

    testCase "McpAdapter.splitStatements ignores empty segments"
    <| fun _ ->
      let result = McpAdapter.splitStatements "let x = 1;;   ;; ;;"
      Expect.equal result.Length 1 "Should produce 1 statement"
      Expect.equal result.[0] "let x = 1;;" "Only real statement"

    testCase "McpAdapter.echoStatement writes prompt and code to writer"
    <| fun _ ->
      let sw = new StringWriter()
      McpAdapter.echoStatement sw "let x = 42;;"
      let output = sw.ToString()
      Expect.stringContains output ">" "Should have prompt"
      Expect.stringContains output "let x = 42" "Should have the code without ;;"
      Expect.isFalse (output.Contains(";;")) "Should strip ;;"

    testCase "McpAdapter.echoStatement echoes each statement separately"
    <| fun _ ->
      let sw = new StringWriter()
      McpAdapter.echoStatement sw "let x = 1;;"
      McpAdapter.echoStatement sw "let y = 2;;"
      let output = sw.ToString()
      Expect.stringContains output "let x = 1" "First echo"
      Expect.stringContains output "let y = 2" "Second echo"

    testCase "McpAdapter.formatCompletions formats completion items"
    <| fun _ ->
      let items: SageFs.Features.AutoCompletion.CompletionItem list = [
        { DisplayText = "printfn"; ReplacementText = "printfn"; Kind = SageFs.Features.AutoCompletion.CompletionKind.Method; GetDescription = None }
        { DisplayText = "printf"; ReplacementText = "printf"; Kind = SageFs.Features.AutoCompletion.CompletionKind.Method; GetDescription = None }
      ]
      let result = McpAdapter.formatCompletions items
      Expect.stringContains result "printfn" "Should include first completion"
      Expect.stringContains result "printf" "Should include second completion"
      Expect.stringContains result "Method" "Should include kind"

    testCase "McpAdapter.formatCompletions handles empty list"
    <| fun _ ->
      let result = McpAdapter.formatCompletions []
      Expect.stringContains result "No completions" "Should indicate no completions"

    testCase "EvalStats.empty has zero counts"
    <| fun _ ->
      let stats = Affordances.EvalStats.empty
      Expect.equal stats.EvalCount 0 "zero evals"
      Expect.equal stats.TotalDuration TimeSpan.Zero "zero duration"

    testCase "EvalStats.record increments count and accumulates duration"
    <| fun _ ->
      let stats =
        Affordances.EvalStats.empty
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 100.0)
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 200.0)
      Expect.equal stats.EvalCount 2 "two evals"
      Expect.equal stats.TotalDuration (TimeSpan.FromMilliseconds 300.0) "total 300ms"
      Expect.equal stats.MinDuration (TimeSpan.FromMilliseconds 100.0) "min 100ms"
      Expect.equal stats.MaxDuration (TimeSpan.FromMilliseconds 200.0) "max 200ms"

    testCase "EvalStats.averageDuration computes correct average"
    <| fun _ ->
      let stats =
        Affordances.EvalStats.empty
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 100.0)
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 300.0)
      Expect.equal (Affordances.EvalStats.averageDuration stats) (TimeSpan.FromMilliseconds 200.0) "avg 200ms"

    testCase "EvalStats.averageDuration returns zero for empty stats"
    <| fun _ ->
      Expect.equal (Affordances.EvalStats.averageDuration Affordances.EvalStats.empty) TimeSpan.Zero "zero avg"

    testCase "TextWriterRecorder converts lone LF to CRLF for console"
    <| fun _ ->
      let sw = new StringWriter()
      let recorder = new TextWriterRecorder(sw)
      recorder.Write("hello\nworld\n")
      let output = sw.ToString()
      Expect.isFalse
        (output.Contains("\n") && not (output.Contains("\r\n")))
        "should not have bare LF without CR"
      Expect.stringContains output "\r\n" "should have CRLF"
      Expect.equal output "hello\r\nworld\r\n" "exact output"

    testCase "TextWriterRecorder converts lone LF char to CRLF"
    <| fun _ ->
      let sw = new StringWriter()
      let recorder = new TextWriterRecorder(sw)
      recorder.Write('h')
      recorder.Write('\n')
      recorder.Write('w')
      let output = sw.ToString()
      Expect.equal output "h\r\nw" "char LF should become CRLF"

    testCase "TextWriterRecorder does not double CRLF"
    <| fun _ ->
      let sw = new StringWriter()
      let recorder = new TextWriterRecorder(sw)
      recorder.Write("hello\r\nworld\r\n")
      let output = sw.ToString()
      Expect.isFalse (output.Contains("\r\r")) "should not double CR"
      Expect.equal output "hello\r\nworld\r\n" "CRLF preserved"

    testCase "TextWriterRecorder does not double CRLF sent char by char"
    <| fun _ ->
      let sw = new StringWriter()
      let recorder = new TextWriterRecorder(sw)
      for c in "h\r\nw" do
        recorder.Write(c)
      let output = sw.ToString()
      Expect.isFalse (output.Contains("\r\r")) "should not double CR"
      Expect.equal output "h\r\nw" "CRLF chars preserved"

    testCase "formatStatus includes eval count when stats provided"
    <| fun _ ->
      let stats =
        Affordances.EvalStats.empty
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 150.0)
        |> Affordances.EvalStats.record (TimeSpan.FromMilliseconds 250.0)
      let result = McpAdapter.formatStatus "test" 5 SessionState.Ready (Some stats)
      Expect.stringContains result "Evals: 2" "has eval count"
      Expect.stringContains result "Avg:" "has avg"
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
      Expect.isTrue (root.GetProperty("success").GetBoolean()) "success should be true"
      Expect.equal (root.GetProperty("result").GetString()) "val x: int = 42" "result value"

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
      Expect.isFalse (root.GetProperty("success").GetBoolean()) "success should be false"
      Expect.stringContains (root.GetProperty("error").GetString()) "foo" "error message"

    testCase "formatEvalResultJson includes diagnostics"
    <| fun _ ->
      let ex = Exception("earlier error")
      let diag: Features.Diagnostics.Diagnostic = {
        Message = "Undefined value"
        Subcategory = "typecheck"
        Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 3 }
        Severity = Features.Diagnostics.DiagnosticSeverity.Error
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
      Expect.equal (diagnostics.GetArrayLength()) 1 "one diagnostic"
      let d = diagnostics.[0]
      Expect.equal (d.GetProperty("severity").GetString()) "error" "severity"
      Expect.equal (d.GetProperty("message").GetString()) "Undefined value" "message"
      Expect.equal (d.GetProperty("startLine").GetInt32()) 1 "startLine"
      Expect.equal (d.GetProperty("startColumn").GetInt32()) 0 "startColumn"

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
      Expect.equal (root.GetProperty("stdout").GetString()) "hello\n" "stdout captured"

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
      Expect.isFalse (root.TryGetProperty("error", &ignored)) "no error field when success"
      Expect.isFalse (root.TryGetProperty("stdout", &ignored)) "no stdout field when empty"

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
      Expect.equal (root.GetProperty("code").GetString()) "let x = 42;;" "code field"
  ]
