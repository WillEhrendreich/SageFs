module SageFs.Tests.SsePropertyTests

open System
open System.Text.Json
open System.Text.Json.Serialization
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.SseWriter
open SageFs.Features.LiveTesting
open SageFs.Features.EvalDiff
open SageFs.Features.CellDependencyGraph
open SageFs.Features.BindingExplorer
open SageFs.Features.EvalTimeline
open SageFs.Features.DomainModelViz

// ── JSON options matching daemon configuration ──

let private jsonOpts =
  let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  opts.Converters.Add(JsonFSharpConverter())
  opts

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 100 }

// ── Helpers ──

let private extractDataPayload (sse: string) =
  sse.Split('\n')
  |> Array.choose (fun line ->
    match line.StartsWith("data: ") with
    | true -> Some (line.Substring(6))
    | false -> None)
  |> String.concat "\n"

let private countDataLines (sse: string) =
  sse.Split('\n')
  |> Array.filter (fun l -> l.StartsWith("data: "))
  |> Array.length

let private isValidJson (s: string) =
  try
    use _ = JsonDocument.Parse(s)
    true
  with _ -> false

let private startsWithEventPrefix (sse: string) =
  sse.StartsWith("event: ")

let private endsWithDoubleNewline (sse: string) =
  sse.EndsWith("\n\n")

let private hasNonEmptyEventType (sse: string) =
  match sse.Split('\n') with
  | [||] -> false
  | lines ->
    match lines.[0].StartsWith("event: ") with
    | true -> lines.[0].Substring(7).Trim().Length > 0
    | false -> false

let private safeStr (s: string) =
  match s with
  | null -> ""
  | v -> v

// ── Builders ──

let private mkTestSummary total passed failed =
  { Total = abs total
    Passed = abs passed
    Failed = abs failed
    Stale = 0
    Running = 0
    Disabled = 0
    Enabled = true }

let private mkDiffSummary addedTexts removedTexts =
  let added = addedTexts |> List.map (safeStr >> Added)
  let removed = removedTexts |> List.map (safeStr >> Removed)
  { Lines = added @ removed
    AddedCount = List.length added
    RemovedCount = List.length removed
    ModifiedCount = 0
    UnchangedCount = 0 }

let private mkCellGraph n =
  let count = max 1 (min (abs n) 10)
  let cells =
    [ for i in 0 .. count - 1 do
        i,
        { CellInfo.Id = i
          Source = sprintf "cell_%d" i
          Produces = [ sprintf "val_%d" i ]
          Consumes = [] } ]
    |> Map.ofList
  let edges =
    match count > 1 with
    | true -> [ for i in 0 .. count - 2 do (i, i + 1) ]
    | false -> []
  { Cells = cells; Edges = edges }

let private mkBindingScope n =
  let count = max 1 (min (abs n) 10)
  let bindings =
    [ for i in 0 .. count - 1 do
        { BindingInfo.Name = sprintf "b_%d" i
          TypeSig = "int"
          Value = Some (sprintf "%d" i)
          CellIndex = i
          ShadowedBy = []
          ReferencedIn = [] } ]
  { Bindings = bindings
    ActiveBindings = bindings |> List.map (fun b -> b.Name, b) |> Map.ofList
    ShadowedBindings = [] }

let private mkTimeline n =
  { Count = abs n + 1
    P50Ms = Some 42.0
    P95Ms = Some 180.0
    P99Ms = Some 450.0
    MeanMs = Some 55.0
    Sparkline = "▁▂▃▅▇" }

let private mkNarratives n =
  let count = max 0 (min (abs n) 5)
  [ for i in 0 .. count - 1 do
      TestId.TestId (sprintf "test_%d" i),
      { LastPassedAt = None
        TimeSinceLastPass = Some (TimeSpan.FromMinutes(float (i + 1)))
        CausalChanges = [ CausalChange.SymbolChanged (sprintf "sym_%d" i) ]
        PropertyViolation = None
        Summary = sprintf "Test %d failed" i } ]
  |> Map.ofList

let private mkSourceLocations n =
  let count = max 0 (min (abs n) 10)
  [ for i in 0 .. count - 1 do
      { CellId = i
        TestName = sprintf "test_%d" i
        FilePath = sprintf "Mod%d.fs" i
        StartLine = i * 10
        EndLine = i * 10 + 5 } ]

let private mkTransitions n =
  let count = max 0 (min (abs n) 10)
  [ for i in 0 .. count - 1 do
      { FromState = sprintf "S%d" i
        ToState = sprintf "S%d" (i + 1)
        FunctionName = Some (sprintf "fn_%d" i)
        IsErrorBranch = i % 2 = 0
        Health =
          match i % 5 with
          | 0 -> TransitionHealth.Passing
          | 1 -> TransitionHealth.Failing
          | 2 -> TransitionHealth.Stale
          | 3 -> TransitionHealth.Untested
          | _ -> TransitionHealth.NotImplemented } ]

// ── Tests ──

[<Tests>]
let ssePropertyTests =
  testList "SSE property tests" [

    // ── Group 1: SSE framing properties ──

    testList "SSE framing properties" [

      testPropertyWithConfig propConfig
        "every typed event starts with 'event: ' and has non-empty type name" <|
        fun (n: PositiveInt) ->
          let events =
            [ formatWarmupProgressEvent jsonOpts None 1 n.Get "msg"
              formatEvalStartedEvent jsonOpts None "x.fsx" n.Get
              formatEvalResultEvent jsonOpts None "x.fsx" n.Get "ok" true 1.0
              formatTestSummaryEvent jsonOpts None (mkTestSummary n.Get 0 0) None
              formatEvalDiffEvent jsonOpts None (mkDiffSummary [ "a" ] [ "b" ])
              formatCellDependenciesEvent jsonOpts None (mkCellGraph n.Get)
              formatBindingScopeMapEvent jsonOpts None (mkBindingScope n.Get)
              formatEvalTimelineEvent jsonOpts None (mkTimeline n.Get)
              formatFailureNarrativesEvent jsonOpts None (mkNarratives n.Get)
              formatTestSourceLocationsEvent jsonOpts None (mkSourceLocations n.Get)
              formatDomainModelEvent jsonOpts None (mkTransitions n.Get) ]
          events |> List.forall (fun e ->
            startsWithEventPrefix e && hasNonEmptyEventType e)

      testPropertyWithConfig propConfig
        "every typed event contains exactly one data line" <|
        fun (n: PositiveInt) ->
          let events =
            [ formatWarmupProgressEvent jsonOpts None 1 n.Get "msg"
              formatEvalStartedEvent jsonOpts None "x.fsx" n.Get
              formatEvalResultEvent jsonOpts None "x.fsx" n.Get "ok" true 2.0
              formatTestSummaryEvent jsonOpts None (mkTestSummary n.Get 0 0) None
              formatEvalDiffEvent jsonOpts None (mkDiffSummary [ "a" ] [ "b" ])
              formatCellDependenciesEvent jsonOpts None (mkCellGraph n.Get)
              formatBindingScopeMapEvent jsonOpts None (mkBindingScope n.Get)
              formatEvalTimelineEvent jsonOpts None (mkTimeline n.Get)
              formatDomainModelEvent jsonOpts None (mkTransitions n.Get) ]
          events |> List.forall (fun e -> countDataLines e = 1)

      testPropertyWithConfig propConfig
        "every typed event ends with double newline" <|
        fun (n: PositiveInt) ->
          let events =
            [ formatWarmupProgressEvent jsonOpts None 1 n.Get "msg"
              formatEvalStartedEvent jsonOpts None "x.fsx" n.Get
              formatEvalResultEvent jsonOpts None "x.fsx" n.Get "ok" true 2.0
              formatTestSummaryEvent jsonOpts None (mkTestSummary n.Get 0 0) None
              formatEvalDiffEvent jsonOpts None (mkDiffSummary [ "a" ] [])
              formatCellDependenciesEvent jsonOpts None (mkCellGraph n.Get)
              formatBindingScopeMapEvent jsonOpts None (mkBindingScope n.Get)
              formatEvalTimelineEvent jsonOpts None (mkTimeline n.Get)
              formatFailureNarrativesEvent jsonOpts None (mkNarratives n.Get)
              formatTestSourceLocationsEvent jsonOpts None (mkSourceLocations n.Get)
              formatDomainModelEvent jsonOpts None (mkTransitions n.Get) ]
          events |> List.forall endsWithDoubleNewline

      testPropertyWithConfig propConfig
        "every typed event data payload is valid JSON" <|
        fun (n: PositiveInt) ->
          let events =
            [ formatWarmupProgressEvent jsonOpts None 1 n.Get "msg"
              formatEvalStartedEvent jsonOpts None "x.fsx" n.Get
              formatEvalResultEvent jsonOpts None "x.fsx" n.Get "ok" true 2.0
              formatTestSummaryEvent jsonOpts None (mkTestSummary n.Get 0 0) None
              formatEvalDiffEvent jsonOpts None (mkDiffSummary [ "a" ] [])
              formatCellDependenciesEvent jsonOpts None (mkCellGraph n.Get)
              formatBindingScopeMapEvent jsonOpts None (mkBindingScope n.Get)
              formatEvalTimelineEvent jsonOpts None (mkTimeline n.Get)
              formatFailureNarrativesEvent jsonOpts None (mkNarratives n.Get)
              formatTestSourceLocationsEvent jsonOpts None (mkSourceLocations n.Get)
              formatDomainModelEvent jsonOpts None (mkTransitions n.Get) ]
          events |> List.forall (fun e ->
            extractDataPayload e |> isValidJson)
    ]

    // ── Group 2: sessionId injection ──

    testList "sessionId injection" [

      testPropertyWithConfig propConfig
        "Some sessionId injects SessionId field into JSON" <|
        fun (n: PositiveInt) ->
          let sid = sprintf "session_%d" n.Get
          let result = injectSessionId (Some sid) """{"key":"value"}"""
          result.Contains("\"SessionId\"") && result.Contains(sid)

      testPropertyWithConfig propConfig
        "None sessionId preserves JSON unchanged" <|
        fun (n: PositiveInt) ->
          let json = sprintf """{"v":%d}""" n.Get
          let result = injectSessionId None json
          result = json

      testPropertyWithConfig propConfig
        "Some sessionId preserves valid JSON structure" <|
        fun (n: PositiveInt) ->
          let sid = sprintf "sid_%d" n.Get
          let json = sprintf """{"v":%d}""" n.Get
          injectSessionId (Some sid) json
          |> isValidJson

      testPropertyWithConfig propConfig
        "typed formatters with Some sessionId include it in data" <|
        fun (n: PositiveInt) ->
          let sid = sprintf "s_%d" n.Get
          let events =
            [ formatWarmupProgressEvent jsonOpts (Some sid) 1 10 "x"
              formatEvalStartedEvent jsonOpts (Some sid) "f.fsx" 1
              formatEvalResultEvent jsonOpts (Some sid) "f.fsx" 1 "ok" true 1.0
              formatTestSummaryEvent jsonOpts (Some sid) (mkTestSummary 10 8 2) None
              formatEvalTimelineEvent jsonOpts (Some sid) (mkTimeline n.Get) ]
          events |> List.forall (fun e ->
            (extractDataPayload e).Contains(sid))

      testPropertyWithConfig propConfig
        "typed formatters with None sessionId omit SessionId" <|
        fun (n: PositiveInt) ->
          let events =
            [ formatWarmupProgressEvent jsonOpts None n.Get n.Get "x"
              formatEvalStartedEvent jsonOpts None "f.fsx" n.Get
              formatEvalResultEvent jsonOpts None "f.fsx" n.Get "ok" true 1.0 ]
          events |> List.forall (fun e ->
            (extractDataPayload e).Contains("SessionId") |> not)
    ]

    // ── Group 3: serialization roundtrip ──

    testList "serialization roundtrip" [

      testPropertyWithConfig propConfig
        "formatWarmupProgressEvent with random inputs produces valid SSE" <|
        fun (step: PositiveInt) (total: PositiveInt) (msg: NonNull<string>) ->
          let sse =
            formatWarmupProgressEvent jsonOpts None step.Get total.Get (safeStr msg.Get)
          startsWithEventPrefix sse
          && endsWithDoubleNewline sse
          && countDataLines sse = 1
          && (extractDataPayload sse |> isValidJson)

      testPropertyWithConfig propConfig
        "formatEvalResultEvent with random inputs produces valid SSE" <|
        fun (line: PositiveInt) (success: bool) (dur: NormalFloat) ->
          let sse =
            formatEvalResultEvent jsonOpts None "test.fsx" line.Get "output" success (abs dur.Get)
          startsWithEventPrefix sse
          && endsWithDoubleNewline sse
          && countDataLines sse = 1
          && (extractDataPayload sse |> isValidJson)

      testPropertyWithConfig propConfig
        "formatEvalStartedEvent with random inputs produces valid SSE" <|
        fun (line: PositiveInt) ->
          let sse = formatEvalStartedEvent jsonOpts None "module.fsx" line.Get
          startsWithEventPrefix sse
          && endsWithDoubleNewline sse
          && countDataLines sse = 1
          && (extractDataPayload sse |> isValidJson)
    ]

    // ── Group 4: compositional properties ──

    testList "compositional properties" [

      testPropertyWithConfig propConfig
        "concatenated events are splittable on double newline" <|
        fun (n: PositiveInt) ->
          let count = min n.Get 20
          let events =
            [ for i in 1 .. count do
                formatSseEvent "test_event" (sprintf """{"i":%d}""" i) ]
          let concatenated = events |> String.concat ""
          let parts =
            concatenated.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
          Array.length parts = count

      testPropertyWithConfig propConfig
        "typed event count matches format call count" <|
        fun (n: PositiveInt) ->
          let count = min n.Get 20
          let events =
            [ for i in 1 .. count do
                formatWarmupProgressEvent jsonOpts None i count "step" ]
          let concatenated = events |> String.concat ""
          let parts =
            concatenated.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
          Array.length parts = count
    ]
  ]
