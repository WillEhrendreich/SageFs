module SageFs.Tests.DiagnosticsStoreTests

open Expecto
open Expecto.Flip
open SageFs.Features.Diagnostics
open SageFs.Features

// DiagnosticsStore is a pure module for accumulating diagnostics keyed by code hash.
// It's the foundation for POST/GET /diagnostics endpoints and accumulated state in AppState.

let mkDiag message severity line =
  {
    Message = message
    Subcategory = "typecheck"
    Range = { StartLine = line; StartColumn = 0; EndLine = line; EndColumn = 10 }
    Severity = severity
  }

[<Tests>]
let diagnosticsStoreTests =
  testList "DiagnosticsStore" [

    testCase "empty store has no diagnostics"
    <| fun _ ->
      DiagnosticsStore.empty
      |> DiagnosticsStore.all
      |> Expect.isEmpty "empty store should have no diagnostics"

    testCase "add diagnostics for a code snippet stores them keyed by hash"
    <| fun _ ->
      let diags = [| mkDiag "type error" DiagnosticSeverity.Error 1 |]
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "let x: int = \"hello\"" diags
      |> DiagnosticsStore.all
      |> Expect.hasLength "should have one entry" 1

    testCase "add diagnostics for same code replaces previous"
    <| fun _ ->
      let diags1 = [| mkDiag "first error" DiagnosticSeverity.Error 1 |]
      let diags2 = [| mkDiag "second error" DiagnosticSeverity.Error 2 |]
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "let x = 1" diags1
      |> DiagnosticsStore.add "let x = 1" diags2
      |> DiagnosticsStore.forCode "let x = 1"
      |> Expect.hasLength "should have replaced, not appended" 1

    testCase "add diagnostics for different code accumulates"
    <| fun _ ->
      let diags1 = [| mkDiag "error one" DiagnosticSeverity.Error 1 |]
      let diags2 = [| mkDiag "error two" DiagnosticSeverity.Error 2 |]
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "let x = 1" diags1
      |> DiagnosticsStore.add "let y = 2" diags2
      |> DiagnosticsStore.all
      |> Expect.hasLength "should have two entries" 2

    testCase "forCode returns diagnostics for specific code"
    <| fun _ ->
      let diags = [| mkDiag "specific error" DiagnosticSeverity.Error 3 |]
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "let z = true" diags
      |> DiagnosticsStore.forCode "let z = true"
      |> List.head
      |> fun d -> d.Message
      |> Expect.equal "should return the specific diagnostic" "specific error"

    testCase "forCode returns empty for unknown code"
    <| fun _ ->
      DiagnosticsStore.empty
      |> DiagnosticsStore.forCode "never seen this"
      |> Expect.isEmpty "unknown code should return empty"

    testCase "clear removes all diagnostics"
    <| fun _ ->
      let diags = [| mkDiag "will be cleared" DiagnosticSeverity.Error 1 |]
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "let x = 1" diags
      |> DiagnosticsStore.clear
      |> DiagnosticsStore.all
      |> Expect.isEmpty "cleared store should be empty"

    testCase "add with empty diagnostics array removes entry"
    <| fun _ ->
      let diags = [| mkDiag "will be removed" DiagnosticSeverity.Error 1 |]
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "let x = 1" diags
      |> DiagnosticsStore.add "let x = 1" [||]
      |> DiagnosticsStore.all
      |> Expect.isEmpty "empty array should remove entry"

    testCase "allFlat returns all diagnostics in a flat list"
    <| fun _ ->
      let diags1 = [| mkDiag "err1" DiagnosticSeverity.Error 1; mkDiag "err2" DiagnosticSeverity.Error 2 |]
      let diags2 = [| mkDiag "err3" DiagnosticSeverity.Warning 3 |]
      DiagnosticsStore.empty
      |> DiagnosticsStore.add "code1" diags1
      |> DiagnosticsStore.add "code2" diags2
      |> DiagnosticsStore.allFlat
      |> Expect.hasLength "should have 3 diagnostics total" 3
  ]
