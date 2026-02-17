module SageFs.Tests.MultiUiConsistencyTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.DaemonClient

/// Simulate the Dashboard's pushJson serialization of RenderRegions.
let private serializeRegions (sessionId: string) (state: string) (evalCount: int) (avgMs: float) (regions: RenderRegion list) =
  let regionPayloads =
    regions |> List.map (fun region ->
      {| id = region.Id
         content = region.Content
         cursor = region.Cursor |> Option.map (fun c -> {| line = c.Line; col = c.Col |})
         completions = region.Completions |> Option.map (fun co ->
           {| items = co.Items; selectedIndex = co.SelectedIndex |}) |})
  System.Text.Json.JsonSerializer.Serialize(
    {| sessionId = sessionId
       sessionState = state
       evalCount = evalCount
       avgMs = avgMs
       regions = regionPayloads |})

let private mkRegion id content cursor completions : RenderRegion =
  { Id = id
    Flags = RegionFlags.None
    Content = content
    Affordances = []
    Cursor = cursor
    Completions = completions }

let roundtripTests = testList "SSE roundtrip" [
  testCase "content survives serialization roundtrip" <| fun _ ->
    let original = mkRegion "output" "hello\nworld" None None
    let json = serializeRegions "sess-1" "Ready" 5 42.5 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, regions) = parsed.Value
    regions.[0].Content
    |> Expect.equal "content preserved" original.Content

  testCase "cursor survives serialization roundtrip" <| fun _ ->
    let cursor = Some { Line = 3; Col = 7 }
    let original = mkRegion "editor" "let x = 1" cursor None
    let json = serializeRegions "sess-2" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, regions) = parsed.Value
    regions.[0].Cursor |> Expect.isSome "cursor preserved"
    regions.[0].Cursor.Value.Line |> Expect.equal "cursor line" 3
    regions.[0].Cursor.Value.Col |> Expect.equal "cursor col" 7

  testCase "completions survive serialization roundtrip" <| fun _ ->
    let completions = Some { Items = ["foo"; "bar"; "baz"]; SelectedIndex = 1 }
    let original = mkRegion "editor" "code" None completions
    let json = serializeRegions "sess-3" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, regions) = parsed.Value
    regions.[0].Completions |> Expect.isSome "completions preserved"
    regions.[0].Completions.Value.Items
    |> Expect.equal "items preserved" ["foo"; "bar"; "baz"]
    regions.[0].Completions.Value.SelectedIndex
    |> Expect.equal "selected index preserved" 1

  testCase "None cursor stays None through roundtrip" <| fun _ ->
    let original = mkRegion "output" "text" None None
    let json = serializeRegions "sess-4" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, regions) = parsed.Value
    regions.[0].Cursor |> Expect.isNone "None cursor stays None"

  testCase "None completions stays None through roundtrip" <| fun _ ->
    let original = mkRegion "output" "text" None None
    let json = serializeRegions "sess-5" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, regions) = parsed.Value
    regions.[0].Completions |> Expect.isNone "None completions stays None"

  testCase "multiple regions preserve order through roundtrip" <| fun _ ->
    let regions = [
      mkRegion "editor" "code" (Some { Line = 0; Col = 0 }) None
      mkRegion "output" "result" None None
      mkRegion "diagnostics" "warn" None None
      mkRegion "sessions" "sess-1" None None
    ]
    let json = serializeRegions "sess-6" "Ready" 3 100.0 regions
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, parsedRegions) = parsed.Value
    parsedRegions |> List.length |> Expect.equal "count" 4
    parsedRegions |> List.map (fun r -> r.Id)
    |> Expect.equal "order preserved" ["editor"; "output"; "diagnostics"; "sessions"]

  testCase "sessionId and stats survive roundtrip" <| fun _ ->
    let json = serializeRegions "session-abc123" "WarmingUp" 10 55.5 []
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (sid, state, count, avg, _) = parsed.Value
    sid |> Expect.equal "sessionId" "session-abc123"
    state |> Expect.equal "state" "WarmingUp"
    count |> Expect.equal "eval count" 10
    avg |> Expect.floatClose "avg ms" Accuracy.medium 55.5

  testCase "empty content roundtrips" <| fun _ ->
    let original = mkRegion "output" "" None None
    let json = serializeRegions "s" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, regions) = parsed.Value
    regions.[0].Content |> Expect.equal "empty content" ""

  testCase "special characters in content roundtrip" <| fun _ ->
    let content = "let x = \"hello\"\nlet y = 'c'\n// ñ é ü 日本語"
    let original = mkRegion "editor" content None None
    let json = serializeRegions "s" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, regions) = parsed.Value
    regions.[0].Content |> Expect.equal "special chars" content
]

let multiConsumerTests = testList "multi-consumer consistency" [
  testCase "two independent parses of same payload produce identical results" <| fun _ ->
    let regions = [
      mkRegion "editor" "let x = 1" (Some { Line = 0; Col = 9 }) None
      mkRegion "output" "val x: int = 1" None None
    ]
    let json = serializeRegions "sess-mc1" "Ready" 5 42.5 regions
    let parse1 = parseStateEvent json
    let parse2 = parseStateEvent json
    parse1 |> Expect.isSome "parse1"
    parse2 |> Expect.isSome "parse2"
    let (sid1, st1, c1, avg1, r1) = parse1.Value
    let (sid2, st2, c2, avg2, r2) = parse2.Value
    sid1 |> Expect.equal "sessionId match" sid2
    st1 |> Expect.equal "state match" st2
    c1 |> Expect.equal "count match" c2
    avg1 |> Expect.floatClose "avg match" Accuracy.medium avg2
    r1 |> List.length |> Expect.equal "region count match" (r2 |> List.length)
    for i in 0..r1.Length-1 do
      r1.[i].Id |> Expect.equal (sprintf "region %d id" i) r2.[i].Id
      r1.[i].Content |> Expect.equal (sprintf "region %d content" i) r2.[i].Content

  testCase "toRenderRegion produces consistent regions across consumers" <| fun _ ->
    let data : DaemonRegionData = {
      Id = "editor"
      Content = "let x = 1"
      Cursor = Some { Line = 0; Col = 9 }
      Completions = Some { Items = ["int"; "int64"]; SelectedIndex = 0 }
    }
    let r1 = DaemonRegionData.toRenderRegion data
    let r2 = DaemonRegionData.toRenderRegion data
    r1.Id |> Expect.equal "id" r2.Id
    r1.Content |> Expect.equal "content" r2.Content
    r1.Cursor |> Expect.equal "cursor" r2.Cursor
    r1.Completions |> Expect.equal "completions" r2.Completions
    r1.Flags |> Expect.equal "flags" r2.Flags

  testCase "render → serialize → parse produces same content for all consumers" <| fun _ ->
    let model =
      { SageFsModel.initial with
          RecentOutput = [
            { Kind = OutputKind.Result; Text = "val x: int = 1"; Timestamp = System.DateTime.UtcNow; SessionId = "" }
            { Kind = OutputKind.Result; Text = """val y: string = "hello" """; Timestamp = System.DateTime.UtcNow; SessionId = "" }
          ] }
    let rendered = SageFsRender.render model
    let json = serializeRegions "sess-pipe" "Ready" 2 50.0 rendered
    let consumers = [1..3] |> List.map (fun _ -> parseStateEvent json)
    for c in consumers do
      c |> Expect.isSome "should parse"
    let baseLine = consumers.[0].Value
    let (_, _, _, _, baseRegions) = baseLine
    for i in 1..consumers.Length-1 do
      let (_, _, _, _, otherRegions) = consumers.[i].Value
      baseRegions |> List.length
      |> Expect.equal "same region count" (otherRegions |> List.length)
      for j in 0..baseRegions.Length-1 do
        baseRegions.[j].Content
        |> Expect.equal (sprintf "consumer %d region %d content" i j) otherRegions.[j].Content

  testCase "Elm model render is deterministic across calls" <| fun _ ->
    let model =
      { SageFsModel.initial with
          RecentOutput = [
            { Kind = OutputKind.Result; Text = "output line"; Timestamp = System.DateTime.UtcNow; SessionId = "" }
          ]
          Diagnostics = [
            { Message = "unused"
              Subcategory = ""
              Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
              Severity = Features.Diagnostics.DiagnosticSeverity.Warning }
          ] }
    let r1 = SageFsRender.render model
    let r2 = SageFsRender.render model
    r1 |> List.length |> Expect.equal "same count" (r2 |> List.length)
    for i in 0..r1.Length-1 do
      r1.[i].Id |> Expect.equal (sprintf "region %d id" i) r2.[i].Id
      r1.[i].Content |> Expect.equal (sprintf "region %d content" i) r2.[i].Content
      r1.[i].Cursor |> Expect.equal (sprintf "region %d cursor" i) r2.[i].Cursor
]

let actionDispatchTests = testList "action dispatch consistency" [
  testCase "actionToApi maps all session actions" <| fun _ ->
    let sessionActions = [
      EditorAction.SessionNavUp
      EditorAction.SessionNavDown
      EditorAction.SessionSelect
      EditorAction.SessionDelete
      EditorAction.SessionCycleNext
      EditorAction.SessionCyclePrev
      EditorAction.CreateSession []
    ]
    for action in sessionActions do
      let result = DaemonClient.actionToApi action
      result |> Expect.isSome (sprintf "%A should map to API" action)

  testCase "all API names from actionToApi use consistent camelCase" <| fun _ ->
    let actions = [
      EditorAction.SessionNavUp
      EditorAction.SessionNavDown
      EditorAction.SessionSelect
      EditorAction.SessionDelete
      EditorAction.SessionCycleNext
      EditorAction.SessionCyclePrev
      EditorAction.ClearOutput
      EditorAction.ResetSession
      EditorAction.HardResetSession
      EditorAction.CreateSession []
    ]
    for action in actions do
      match DaemonClient.actionToApi action with
      | Some (apiName, _) ->
        // camelCase: first char lowercase, no spaces/dashes
        apiName.[0] |> System.Char.IsLower
        |> Expect.isTrue (sprintf "'%s' should be camelCase" apiName)
        apiName.Contains(" ") |> Expect.isFalse (sprintf "'%s' no spaces" apiName)
      | None ->
        failtest (sprintf "%A has no API mapping" action)

  testCase "UiAction.tryParse handles all PascalCase action names" <| fun _ ->
    // UiAction.tryParse uses PascalCase names
    let knownPascalActions = [
      "SessionNavUp"; "SessionNavDown"; "SessionSelect"; "SessionDelete"
      "SessionCycleNext"; "SessionCyclePrev"
      "ClearOutput"; "ResetSession"; "HardResetSession"; "CreateSession"
    ]
    for name in knownPascalActions do
      UiAction.tryParse name
      |> Expect.isSome (sprintf "UiAction should parse '%s'" name)
]

let resizeTests = testList "pane resize" [
  testCase "UiAction.tryParse ResizeHGrow → ResizeH 1" <| fun _ ->
    UiAction.tryParse "ResizeHGrow"
    |> Expect.equal "should parse" (Some (UiAction.ResizeH 1))

  testCase "UiAction.tryParse ResizeHShrink → ResizeH -1" <| fun _ ->
    UiAction.tryParse "ResizeHShrink"
    |> Expect.equal "should parse" (Some (UiAction.ResizeH -1))

  testCase "UiAction.tryParse ResizeVGrow → ResizeV 1" <| fun _ ->
    UiAction.tryParse "ResizeVGrow"
    |> Expect.equal "should parse" (Some (UiAction.ResizeV 1))

  testCase "UiAction.tryParse ResizeRShrink → ResizeR -1" <| fun _ ->
    UiAction.tryParse "ResizeRShrink"
    |> Expect.equal "should parse" (Some (UiAction.ResizeR -1))

  testCase "LayoutConfig.resizeH clamps to bounds" <| fun _ ->
    let cfg = LayoutConfig.defaults
    let grown = LayoutConfig.resizeH 20 cfg
    let shrunk = LayoutConfig.resizeH -20 cfg
    Expecto.Flip.Expect.floatClose "max 0.9" Accuracy.high 0.9 grown.LeftRightSplit
    Expecto.Flip.Expect.floatClose "min 0.2" Accuracy.high 0.2 shrunk.LeftRightSplit

  testCase "LayoutConfig.resizeV clamps min to 2" <| fun _ ->
    let cfg = LayoutConfig.defaults
    let shrunk = LayoutConfig.resizeV -10 cfg
    shrunk.OutputEditorSplit |> Expect.equal "min 2" 2

  testCase "LayoutConfig.resizeR clamps to bounds" <| fun _ ->
    let cfg = LayoutConfig.defaults
    let grown = LayoutConfig.resizeR 20 cfg
    let shrunk = LayoutConfig.resizeR -20 cfg
    Expecto.Flip.Expect.floatClose "max 0.9" Accuracy.high 0.9 grown.SessionsDiagSplit
    Expecto.Flip.Expect.floatClose "min 0.1" Accuracy.high 0.1 shrunk.SessionsDiagSplit

  testCase "LayoutConfig.resizeH step is 0.05" <| fun _ ->
    let cfg = LayoutConfig.defaults
    let grown = LayoutConfig.resizeH 1 cfg
    Expecto.Flip.Expect.floatClose "default + 0.05" Accuracy.high 0.7 grown.LeftRightSplit
]

[<Tests>]
let allMultiUiTests = testList "Multi-UI Consistency" [
  roundtripTests
  multiConsumerTests
  actionDispatchTests
  resizeTests
]
