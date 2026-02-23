module SageFs.Tests.McpAdapterTests

open Expecto
open Expecto.Flip
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

      let result = McpAdapter.formatEvalResult response
      result |> Expect.stringContains "Should format error with Error: prefix" "Error:"

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
      let result = McpAdapter.formatEvalResult response
      Expect.isFalse "Should NOT have Code section" (result.Contains("Code:"))

    testCase "McpAdapter.formatEvalResult does not echo code on success"
    <| fun _ ->
      let response: EvalResponse = {
        EvaluationResult = Ok "val x: int = 42"
        Diagnostics = [||]
        EvaluatedCode = "let x = 42"
        Metadata = Map.empty
      }
      let result = McpAdapter.formatEvalResult response
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
      let result = McpAdapter.formatEvalResult response
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
      let result = McpAdapter.formatEvalResult response
      result |> Expect.stringContains "Should include helpful suggestion for type errors" "Tip:"

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

      let result = McpAdapter.formatEvalResult response
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
