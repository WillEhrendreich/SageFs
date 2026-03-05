module SageFs.Tests.FsiRegressionTests

open Expecto
open Expecto.Flip
open SageFs

/// FSI regression tests pin expected FSI behavior to catch .NET preview breakage.
/// These test the parsing/formatting functions that depend on FSI output format.
[<Tests>]
let fsiRegressionTests = testList "FSI regression" [

  testList "val binding format" [
    test "parses simple val binding" {
      SseWriter.parseBindingsFromOutput "val x : int = 42"
      |> Expect.equal "should parse x:int" [| ("x", "int") |]
    }

    test "parses string val binding" {
      SseWriter.parseBindingsFromOutput "val greeting : string = \"hello\""
      |> Expect.equal "should parse greeting:string" [| ("greeting", "string") |]
    }

    test "parses list val binding" {
      SseWriter.parseBindingsFromOutput "val xs : int list = [1; 2; 3]"
      |> Expect.equal "should parse xs:int list" [| ("xs", "int list") |]
    }

    test "parses function val binding" {
      SseWriter.parseBindingsFromOutput "val f : int -> string"
      |> Expect.equal "should parse f:int -> string" [| ("f", "int -> string") |]
    }

    test "parses mutable val binding" {
      SseWriter.parseBindingsFromOutput "val mutable counter : int = 0"
      |> Expect.equal "should parse mutable" [| ("counter", "int") |]
    }

    test "skips val it (expression result)" {
      SseWriter.parseBindingsFromOutput "val it : int = 42"
      |> Expect.isEmpty "should skip val it"
    }

    test "skips tuple patterns" {
      SseWriter.parseBindingsFromOutput "val (a, b) : int * string = (1, \"x\")"
      |> Expect.isEmpty "should skip tuple patterns"
    }

    test "parses multiple bindings from multiline output" {
      let output = "val x : int = 1\nval y : string = \"hi\"\nval it : bool = true"
      SseWriter.parseBindingsFromOutput output
      |> Expect.equal "should parse x and y, skip it"
        [| ("x", "int"); ("y", "string") |]
    }

    test "handles generic type val binding" {
      SseWriter.parseBindingsFromOutput "val m : Map<string, int>"
      |> Expect.equal "should parse generic" [| ("m", "Map<string, int>") |]
    }

    testProperty "non-val lines produce no bindings" <| fun (line: string) ->
      match line with
      | null -> true
      | l when l.StartsWith("val ") -> true // skip val lines
      | l ->
        SseWriter.parseBindingsFromOutput l |> Array.isEmpty
  ]

  testList "ANSI stripping" [
    test "strips color codes" {
      AppState.stripAnsi "\x1b[31mred text\x1b[0m"
      |> Expect.equal "should strip color" "red text"
    }

    test "strips bold codes" {
      AppState.stripAnsi "\x1b[1mbold\x1b[22m"
      |> Expect.equal "should strip bold" "bold"
    }

    test "converts cursor reset to newline" {
      AppState.stripAnsi "line1\x1b[5Dline2"
      |> Expect.equal "should convert cursor reset" "line1\nline2"
    }

    test "strips cursor visibility codes" {
      AppState.stripAnsi "\x1b[?25lhidden\x1b[?25h"
      |> Expect.equal "should strip visibility" "hidden"
    }

    test "handles plain text unchanged" {
      AppState.stripAnsi "plain text"
      |> Expect.equal "should be unchanged" "plain text"
    }

    testProperty "stripAnsi removes standard ANSI color/cursor codes" <| fun (s: string) ->
      match s with
      | null -> true
      | s ->
        // Insert known ANSI codes and verify they're stripped
        let withAnsi = sprintf "\x1b[31m%s\x1b[0m" s
        let cleaned = AppState.stripAnsi withAnsi
        not (cleaned.Contains("\x1b[31m")) && not (cleaned.Contains("\x1b[0m"))
  ]

  testList "Expecto summary reformatting" [
    test "reformats standard Expecto summary" {
      let input = "EXPECTO! 42 tests run in 00:00:01.234 for MyTests – 40 passed, 1 ignored, 1 failed, 0 errored. Success!"
      let result = AppState.reformatExpectoSummary input
      result |> Expect.stringContains "should contain test suite name" "MyTests"
      result |> Expect.stringContains "should contain test count" "42"
      result |> Expect.stringContains "should contain passed" "40"
    }

    test "passes through non-Expecto lines unchanged" {
      let input = "some random output line"
      AppState.reformatExpectoSummary input
      |> Expect.equal "should be unchanged" "some random output line"
    }
  ]

  testList "cleanStdout" [
    test "strips progress bars" {
      let input = "1/5 | building...\n2/5 | testing...\nactual output"
      AppState.cleanStdout input
      |> Expect.equal "should strip progress" "actual output"
    }

    test "strips Expecto timestamps" {
      let input = "[14:30:05 INF] test passed"
      AppState.cleanStdout input
      |> Expect.equal "should strip timestamp" "test passed"
    }

    test "strips <Expecto> suffix" {
      let input = "test output <Expecto>"
      AppState.cleanStdout input
      |> Expect.equal "should strip suffix" "test output"
    }

    test "strips 'Expecto Running' lines" {
      let input = "Expecto Running tests\nactual result"
      AppState.cleanStdout input
      |> Expect.equal "should strip running" "actual result"
    }
  ]

  testList "error classification" [
    test "identifies type error" {
      let err = ErrorMessages.parseError "This expression was expected to have type 'int'"
      err.IsTypeError |> Expect.isTrue "should be type error"
    }

    test "identifies syntax error" {
      let err = ErrorMessages.parseError "unexpected symbol 'in' in expression"
      err.IsSyntaxError |> Expect.isTrue "should be syntax error"
    }

    test "identifies name error" {
      let err = ErrorMessages.parseError "The value or constructor 'foo' is not defined"
      err.IsNameError |> Expect.isTrue "should be name error"
    }

    test "identifies 'not found' name error" {
      let err = ErrorMessages.parseError "The type 'Bar' was not found"
      err.IsNameError |> Expect.isTrue "should be name error for not found"
    }

    test "earlier error gets correct suggestion" {
      let suggestion =
        ErrorMessages.parseError "Operation could not be completed due to earlier error"
        |> ErrorMessages.getSuggestion
      suggestion |> Expect.stringContains "should warn about earlier error" "earlier error"
      suggestion |> Expect.stringContains "should say don't reset" "Do NOT reset"
    }

    test "name error gets hint about namespace" {
      let suggestion =
        ErrorMessages.parseError "The value 'x' is not defined"
        |> ErrorMessages.getSuggestion
      suggestion |> Expect.stringContains "should mention namespace" "namespace"
    }

    testProperty "parseError always returns non-null Message" <| fun (text: string) ->
      match text with
      | null -> true
      | t ->
        let err = ErrorMessages.parseError t
        err.Message = t
  ]

  testList "SageFsError describe" [
    test "describe covers all error variants" {
      let errors : SageFsError list = [
        SageFsError.NoActiveSessions
        SageFsError.SessionNotFound "test-session"
        SageFsError.EvalFailed "compile error"
        SageFsError.WorkerSpawnFailed "OOM"
        SageFsError.DaemonNotRunning
        SageFsError.SseConnectionError "timeout"
      ]
      errors
      |> List.map SageFsError.describe
      |> List.length
      |> Expect.equal "all should describe" errors.Length
    }

    test "NoActiveSessions suggests create_session" {
      SageFsError.describe SageFsError.NoActiveSessions
      |> Expect.stringContains "should mention create" "create_session"
    }

    test "SessionNotFound includes session id" {
      SageFsError.describe (SageFsError.SessionNotFound "my-session")
      |> Expect.stringContains "should include id" "my-session"
    }

    testProperty "describe returns non-empty for EvalFailed" <| fun (msg: string) ->
      let safeMsg = match msg with null -> "test" | m -> m
      let desc = SageFsError.describe (SageFsError.EvalFailed safeMsg)
      desc.Length > 0
  ]
]
