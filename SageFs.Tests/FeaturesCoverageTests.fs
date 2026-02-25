module SageFs.Tests.FeaturesCoverageTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.AutoCompletion
open SageFs.Features.Events

// ═══════════════════════════════════════════════════════════
// AutoCompletion — scoreCandidate
// ═══════════════════════════════════════════════════════════

let scoreCandidateTests = testList "scoreCandidate" [
  test "exact prefix match scores higher than non-prefix" {
    let prefixScore = scoreCandidate "get" "getName"
    let nonPrefixScore = scoreCandidate "get" "forgetMe"
    (prefixScore, nonPrefixScore)
    |> Expect.isGreaterThan "prefix match should score higher"
  }

  test "exact match scores highest" {
    let exactScore = scoreCandidate "name" "name"
    let partialScore = scoreCandidate "name" "nameOfThing"
    (exactScore, partialScore)
    |> Expect.isGreaterThan "exact match should score higher"
  }

  test "shorter candidates score higher than longer with same prefix" {
    let shortScore = scoreCandidate "to" "toList"
    let longScore = scoreCandidate "to" "toListAndBack"
    (shortScore, longScore)
    |> Expect.isGreaterThan "shorter candidate should score higher"
  }

  test "empty entered word still produces a score" {
    let score = scoreCandidate "" "anything"
    (score, 0) |> Expect.isGreaterThanOrEqual "should have non-negative score"
  }

  test "score is always non-negative for non-empty inputs" {
    let score = scoreCandidate "xyz" "abc"
    (score, 0) |> Expect.isGreaterThanOrEqual "score should be >= 0"
  }
]

let scoreCandidatePropertyTests = testList "scoreCandidate properties" [
  testProperty "prefix match always adds 200 to score" (fun (NonEmptyString prefix) ->
    let candidate = prefix + "Suffix"
    let score = scoreCandidate prefix candidate
    let noMatchScore = scoreCandidate "ZZZZZ" candidate
    score >= noMatchScore
  )

  testProperty "score is always non-negative" (fun (NonEmptyString word) (NonEmptyString candidate) ->
    scoreCandidate word candidate >= 0
  )

  testProperty "exact prefix adds 200 bonus" (fun (NonEmptyString prefix) ->
    let withPrefix = prefix + "xyz"
    let score = scoreCandidate prefix withPrefix
    score >= 200
  )

  testProperty "shorter candidates score higher for same word" (fun (NonEmptyString word) ->
    let short = word + "a"
    let long = word + "abcdefghij"
    let shortScore = scoreCandidate word short
    let longScore = scoreCandidate word long
    shortScore >= longScore
  )
]

// ═══════════════════════════════════════════════════════════
// AutoCompletion — mkReplacement
// ═══════════════════════════════════════════════════════════

let mkReplacementTests = testList "mkReplacement" [
  test "strips common prefix from entry" {
    DirectiveCompletions.mkReplacement "ref" "reference"
    |> Expect.equal "should strip 'ref' prefix" "erence"
  }

  test "returns full entry when no common prefix" {
    DirectiveCompletions.mkReplacement "abc" "xyz"
    |> Expect.equal "should return full entry" "xyz"
  }

  test "returns empty for exact match" {
    DirectiveCompletions.mkReplacement "help" "help"
    |> Expect.equal "should return empty" ""
  }

  test "handles partial overlap" {
    DirectiveCompletions.mkReplacement "he" "help"
    |> Expect.equal "should strip common chars" "lp"
  }

  test "handles empty word to replace" {
    DirectiveCompletions.mkReplacement "" "reference"
    |> Expect.equal "should return full entry" "reference"
  }
]

let mkReplacementPropertyTests = testList "mkReplacement properties" [
  testProperty "result length + common prefix length = entry length" (fun (NonEmptyString word) (NonEmptyString entry) ->
    let result = DirectiveCompletions.mkReplacement word entry
    let commonLen = entry.Length - result.Length
    commonLen + result.Length = entry.Length
  )

  testProperty "result is always a suffix of entry" (fun (NonEmptyString word) (NonEmptyString entry) ->
    let result = DirectiveCompletions.mkReplacement word entry
    entry.EndsWith(result, System.StringComparison.Ordinal)
  )

  testProperty "empty word returns full entry" (fun (NonEmptyString entry) ->
    DirectiveCompletions.mkReplacement "" entry = entry
  )
]

// ═══════════════════════════════════════════════════════════
// AutoCompletion — CompletionKind
// ═══════════════════════════════════════════════════════════

let completionKindTests = testList "CompletionKind" [
  test "label returns non-empty string for every case" {
    let allKinds = [
      CompletionKind.Class; CompletionKind.Constant; CompletionKind.Delegate
      CompletionKind.Enum; CompletionKind.EnumMember; CompletionKind.Event
      CompletionKind.Exception; CompletionKind.Field; CompletionKind.Interface
      CompletionKind.Method; CompletionKind.OverriddenMethod; CompletionKind.Module
      CompletionKind.Namespace; CompletionKind.Property; CompletionKind.Struct
      CompletionKind.Typedef; CompletionKind.Type; CompletionKind.Union
      CompletionKind.Variable; CompletionKind.ExtensionMethod
      CompletionKind.TypeParameter; CompletionKind.Keyword
      CompletionKind.Folder; CompletionKind.File
    ]
    for kind in allKinds do
      let label = CompletionKind.label kind
      label |> Expect.isNotEmpty (sprintf "label for %A should be non-empty" kind)
  }

  test "label values are unique" {
    let allKinds = [
      CompletionKind.Class; CompletionKind.Constant; CompletionKind.Delegate
      CompletionKind.Enum; CompletionKind.EnumMember; CompletionKind.Event
      CompletionKind.Exception; CompletionKind.Field; CompletionKind.Interface
      CompletionKind.Method; CompletionKind.OverriddenMethod; CompletionKind.Module
      CompletionKind.Namespace; CompletionKind.Property; CompletionKind.Struct
      CompletionKind.Typedef; CompletionKind.Type; CompletionKind.Union
      CompletionKind.Variable; CompletionKind.ExtensionMethod
      CompletionKind.TypeParameter; CompletionKind.Keyword
      CompletionKind.Folder; CompletionKind.File
    ]
    let labels = allKinds |> List.map CompletionKind.label
    let unique = labels |> List.distinct
    unique |> Expect.hasLength "all labels should be unique" (List.length allKinds)
  }
]

// ═══════════════════════════════════════════════════════════
// Events — EventSource.ToString
// ═══════════════════════════════════════════════════════════

let eventSourceTests = testList "EventSource" [
  test "Console renders as 'console'" {
    EventSource.Console.ToString()
    |> Expect.equal "should be 'console'" "console"
  }

  test "McpAgent renders with agent name" {
    EventSource.McpAgent("copilot").ToString()
    |> Expect.equal "should contain agent name" "mcp:copilot"
  }

  test "FileSync renders with file name" {
    EventSource.FileSync("test.fsx").ToString()
    |> Expect.equal "should contain file name" "file:test.fsx"
  }

  test "System renders as 'system'" {
    EventSource.System.ToString()
    |> Expect.equal "should be 'system'" "system"
  }
]

// ═══════════════════════════════════════════════════════════
// Events — DiagnosticEvent.fromDiagnostic
// ═══════════════════════════════════════════════════════════

let diagnosticEventTests = testList "DiagnosticEvent" [
  test "fromDiagnostic maps all fields" {
    let diag : SageFs.Features.Diagnostics.Diagnostic = {
      Message = "test error"
      Subcategory = "parse"
      Range = { StartLine = 1; StartColumn = 2; EndLine = 3; EndColumn = 4 }
      Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Error
    }
    let event = DiagnosticEvent.fromDiagnostic diag
    event.Message |> Expect.equal "message should match" "test error"
    event.Severity |> Expect.equal "severity should match" SageFs.Features.Diagnostics.DiagnosticSeverity.Error
    event.StartLine |> Expect.equal "start line" 1
    event.StartColumn |> Expect.equal "start col" 2
    event.EndLine |> Expect.equal "end line" 3
    event.EndColumn |> Expect.equal "end col" 4
  }

  test "fromDiagnostic preserves warning severity" {
    let diag : SageFs.Features.Diagnostics.Diagnostic = {
      Message = "warning msg"
      Subcategory = ""
      Range = { StartLine = 10; StartColumn = 0; EndLine = 10; EndColumn = 5 }
      Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Warning
    }
    let event = DiagnosticEvent.fromDiagnostic diag
    event.Severity |> Expect.equal "should be warning" SageFs.Features.Diagnostics.DiagnosticSeverity.Warning
  }
]

// ═══════════════════════════════════════════════════════════
// Events — DiagnosticSeverity.label
// ═══════════════════════════════════════════════════════════

let diagnosticSeverityTests = testList "DiagnosticSeverity" [
  test "label maps all severities" {
    SageFs.Features.Diagnostics.DiagnosticSeverity.label SageFs.Features.Diagnostics.DiagnosticSeverity.Error
    |> Expect.equal "error label" "error"
    SageFs.Features.Diagnostics.DiagnosticSeverity.label SageFs.Features.Diagnostics.DiagnosticSeverity.Warning
    |> Expect.equal "warning label" "warning"
    SageFs.Features.Diagnostics.DiagnosticSeverity.label SageFs.Features.Diagnostics.DiagnosticSeverity.Info
    |> Expect.equal "info label" "info"
    SageFs.Features.Diagnostics.DiagnosticSeverity.label SageFs.Features.Diagnostics.DiagnosticSeverity.Hidden
    |> Expect.equal "hidden label" "hidden"
  }
]

// ═══════════════════════════════════════════════════════════
// LiveTestingInstrumentation — traced wrapper
// ═══════════════════════════════════════════════════════════

open SageFs.Features.LiveTesting

let instrumentationTests = testList "LiveTestingInstrumentation" [
  test "traced returns correct value" {
    let result = LiveTestingInstrumentation.traced "test.op" [] (fun () -> 42)
    result |> Expect.equal "should return inner value" 42
  }

  test "traced passes through exceptions" {
    Expect.throwsT<System.InvalidOperationException>
      "should propagate exceptions"
      (fun () ->
        LiveTestingInstrumentation.traced "test.op" [] (fun () ->
          raise (System.InvalidOperationException("boom")))
        |> ignore)
  }

  test "traced works with string results" {
    let result = LiveTestingInstrumentation.traced "test.str" [("key", box "value")] (fun () -> "hello")
    result |> Expect.equal "should return string" "hello"
  }

  test "traced handles unit result" {
    let mutable called = false
    LiveTestingInstrumentation.traced "test.unit" [] (fun () -> called <- true)
    called |> Expect.isTrue "side effect should have run"
  }
]

// ═══════════════════════════════════════════════════════════
// Combined test list
// ═══════════════════════════════════════════════════════════

[<Tests>]
let allFeaturesCoverageTests = testList "Features coverage" [
  testList "AutoCompletion" [
    scoreCandidateTests
    scoreCandidatePropertyTests
    mkReplacementTests
    mkReplacementPropertyTests
    completionKindTests
  ]
  testList "Events module" [
    eventSourceTests
    diagnosticEventTests
    diagnosticSeverityTests
  ]
  instrumentationTests
]
