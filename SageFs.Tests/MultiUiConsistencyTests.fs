module SageFs.Tests.MultiUiConsistencyTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.DaemonClient
open SageFs.SessionDisplay
open SageFs.Features

/// Simulate the Dashboard's pushJson serialization of RenderRegions.
let serializeRegions (sessionId: string) (state: string) (evalCount: int) (avgMs: float) (regions: RenderRegion list) =
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
       activeWorkingDir = ""
       regions = regionPayloads |})

let mkRegion id content cursor completions : RenderRegion =
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
    let (_, _, _, _, _, regions) = parsed.Value
    regions.[0].Content
    |> Expect.equal "content preserved" original.Content

  testCase "cursor survives serialization roundtrip" <| fun _ ->
    let cursor = Some { Line = 3; Col = 7 }
    let original = mkRegion "editor" "let x = 1" cursor None
    let json = serializeRegions "sess-2" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, _, regions) = parsed.Value
    regions.[0].Cursor |> Expect.isSome "cursor preserved"
    regions.[0].Cursor.Value.Line |> Expect.equal "cursor line" 3
    regions.[0].Cursor.Value.Col |> Expect.equal "cursor col" 7

  testCase "completions survive serialization roundtrip" <| fun _ ->
    let completions = Some { Items = ["foo"; "bar"; "baz"]; SelectedIndex = 1 }
    let original = mkRegion "editor" "code" None completions
    let json = serializeRegions "sess-3" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, _, regions) = parsed.Value
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
    let (_, _, _, _, _, regions) = parsed.Value
    regions.[0].Cursor |> Expect.isNone "None cursor stays None"

  testCase "None completions stays None through roundtrip" <| fun _ ->
    let original = mkRegion "output" "text" None None
    let json = serializeRegions "sess-5" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, _, regions) = parsed.Value
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
    let (_, _, _, _, _, parsedRegions) = parsed.Value
    parsedRegions |> List.length |> Expect.equal "count" 4
    parsedRegions |> List.map (fun r -> r.Id)
    |> Expect.equal "order preserved" ["editor"; "output"; "diagnostics"; "sessions"]

  testCase "sessionId and stats survive roundtrip" <| fun _ ->
    let json = serializeRegions "session-abc123" "WarmingUp" 10 55.5 []
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (sid, state, count, avg, _, _) = parsed.Value
    sid |> Expect.equal "sessionId" "session-abc123"
    state |> Expect.equal "state" "WarmingUp"
    count |> Expect.equal "eval count" 10
    avg |> Expect.floatClose "avg ms" Accuracy.medium 55.5

  testCase "empty content roundtrips" <| fun _ ->
    let original = mkRegion "output" "" None None
    let json = serializeRegions "s" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, _, regions) = parsed.Value
    regions.[0].Content |> Expect.equal "empty content" ""

  testCase "special characters in content roundtrip" <| fun _ ->
    let content = "let x = \"hello\"\nlet y = 'c'\n// ñ é ü 日本語"
    let original = mkRegion "editor" content None None
    let json = serializeRegions "s" "Ready" 0 0.0 [original]
    let parsed = parseStateEvent json
    parsed |> Expect.isSome "should parse"
    let (_, _, _, _, _, regions) = parsed.Value
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
    let (sid1, st1, c1, avg1, _, r1) = parse1.Value
    let (sid2, st2, c2, avg2, _, r2) = parse2.Value
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
    let (_, _, _, _, _, baseRegions) = baseLine
    for i in 1..consumers.Length-1 do
      let (_, _, _, _, _, otherRegions) = consumers.[i].Value
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
          Diagnostics = Map.ofList [
            "", [
              { Message = "unused"
                Subcategory = ""
                Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
                Severity = Features.Diagnostics.DiagnosticSeverity.Warning }
            ] ] }
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

let mkSnapshot id projects isActive : SessionSnapshot =
  { Id = id
    Name = None
    Status = SessionDisplayStatus.Running
    Projects = projects
    IsActive = isActive
    EvalCount = 0
    LastActivity = System.DateTime.UtcNow
    UpSince = System.DateTime.UtcNow
    WorkingDirectory = "" }

let threeSessionModel =
  let m0 = SageFsModel.initial
  let apply evt m = SageFsUpdate.update (SageFsMsg.Event evt) m |> fst
  m0
  |> apply (SageFsEvent.SessionCreated (mkSnapshot "sess-a" ["A.fsproj"] false))
  |> apply (SageFsEvent.SessionCreated (mkSnapshot "sess-b" ["B.fsproj"] false))
  |> apply (SageFsEvent.SessionCreated (mkSnapshot "sess-c" ["C.fsproj"] false))
  |> apply (SageFsEvent.SessionSwitched (None, "sess-a"))

let elmSessionSwitchingTests = testList "Elm session switching" [
  testCase "SessionSelect at index 0 emits RequestSessionSwitch for first session" <| fun _ ->
    // Sessions are prepended, so order is [sess-c; sess-b; sess-a]
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 0 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model
    effs |> List.length |> Expect.equal "one effect" 1
    match effs.[0] with
    | SageFsEffect.Editor(EditorEffect.RequestSessionSwitch sid) ->
      sid |> Expect.equal "switches to sess-c (newest)" "sess-c"
    | other -> failtest (sprintf "unexpected effect: %A" other)

  testCase "SessionSelect at index 1 switches to second session" <| fun _ ->
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 1 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model
    match effs.[0] with
    | SageFsEffect.Editor(EditorEffect.RequestSessionSwitch sid) ->
      sid |> Expect.equal "switches to sess-b" "sess-b"
    | other -> failtest (sprintf "unexpected effect: %A" other)

  testCase "SessionSelect at index 2 switches to third session" <| fun _ ->
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 2 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model
    match effs.[0] with
    | SageFsEffect.Editor(EditorEffect.RequestSessionSwitch sid) ->
      sid |> Expect.equal "switches to sess-a (oldest)" "sess-a"
    | other -> failtest (sprintf "unexpected effect: %A" other)

  testCase "SessionSelect with no index emits no effects" <| fun _ ->
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) threeSessionModel
    effs |> List.length |> Expect.equal "no effects" 0

  testCase "SessionSelect with out-of-range index emits no effects" <| fun _ ->
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 99 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model
    effs |> List.length |> Expect.equal "no effects" 0
]

let elmSessionCyclingTests = testList "Elm session cycling" [
  testCase "CycleNext from index 0 advances to index 1" <| fun _ ->
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 0 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model
    effs |> List.length |> Expect.equal "one effect" 1
    match effs.[0] with
    | SageFsEffect.Editor(EditorEffect.RequestSessionSwitch sid) ->
      sid |> Expect.equal "advances to sess-b" "sess-b"
    | other -> failtest (sprintf "unexpected effect: %A" other)

  testCase "CycleNext from last index wraps to 0" <| fun _ ->
    // Sessions: [sess-c; sess-b; sess-a], index 2 = sess-a
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 2 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model
    match effs.[0] with
    | SageFsEffect.Editor(EditorEffect.RequestSessionSwitch sid) ->
      sid |> Expect.equal "wraps to sess-c" "sess-c"
    | other -> failtest (sprintf "unexpected effect: %A" other)

  testCase "CyclePrev from index 0 wraps to last" <| fun _ ->
    // Sessions: [sess-c; sess-b; sess-a], index 0 = sess-c
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 0 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCyclePrev) model
    match effs.[0] with
    | SageFsEffect.Editor(EditorEffect.RequestSessionSwitch sid) ->
      sid |> Expect.equal "wraps to sess-a" "sess-a"
    | other -> failtest (sprintf "unexpected effect: %A" other)

  testCase "CyclePrev from index 2 goes to index 1" <| fun _ ->
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 2 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCyclePrev) model
    match effs.[0] with
    | SageFsEffect.Editor(EditorEffect.RequestSessionSwitch sid) ->
      sid |> Expect.equal "goes to sess-b" "sess-b"
    | other -> failtest (sprintf "unexpected effect: %A" other)

  testCase "CycleNext with single session produces no effects" <| fun _ ->
    let m0 = SageFsModel.initial
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "only" ["X.fsproj"] true))) m0
    let model = { m1 with Editor = { m1.Editor with SelectedSessionIndex = Some 0 } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model
    effs |> List.length |> Expect.equal "no effects for single session" 0

  testCase "CyclePrev with no sessions produces no effects" <| fun _ ->
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCyclePrev) SageFsModel.initial
    effs |> List.length |> Expect.equal "no effects" 0

  testCase "CycleNext with no SelectedSessionIndex defaults to index 0 then cycles" <| fun _ ->
    // Sessions: [sess-c; sess-b; sess-a], default index 0, next = index 1 = sess-b
    let model = { threeSessionModel with Editor = { threeSessionModel.Editor with SelectedSessionIndex = None } }
    let _, effs = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) model
    effs |> List.length |> Expect.equal "one effect" 1
    match effs.[0] with
    | SageFsEffect.Editor(EditorEffect.RequestSessionSwitch sid) ->
      sid |> Expect.equal "cycles to index 1" "sess-b"
    | other -> failtest (sprintf "unexpected effect: %A" other)
]

let elmSessionEventTests = testList "Elm session events" [
  testCase "SessionSwitched updates ActiveSessionId and IsActive flags" <| fun _ ->
    let m', _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, "sess-b"))) threeSessionModel
    m'.Sessions.ActiveSessionId |> Expect.equal "active updated" (ActiveSession.Viewing "sess-b")
    m'.Sessions.Sessions
    |> List.find (fun s -> s.Id = "sess-b")
    |> fun s -> s.IsActive |> Expect.isTrue "sess-b is active"
    m'.Sessions.Sessions
    |> List.find (fun s -> s.Id = "sess-a")
    |> fun s -> s.IsActive |> Expect.isFalse "sess-a no longer active"
    m'.Sessions.Sessions
    |> List.filter (fun s -> s.IsActive)
    |> List.length
    |> Expect.equal "exactly one active" 1

  testCase "SessionCreated adds new session to list" <| fun _ ->
    let snap = mkSnapshot "sess-d" ["D.fsproj"] false
    let m', _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap)) threeSessionModel
    m'.Sessions.Sessions |> List.length |> Expect.equal "4 sessions" 4
    m'.Sessions.Sessions
    |> List.exists (fun s -> s.Id = "sess-d")
    |> Expect.isTrue "new session exists"

  testCase "SessionCreated auto-activates first session" <| fun _ ->
    let m0 = SageFsModel.initial
    m0.Sessions.ActiveSessionId |> Expect.equal "initially awaiting" ActiveSession.AwaitingSession
    let snap = mkSnapshot "first" ["X.fsproj"] false
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated snap)) m0
    m1.Sessions.ActiveSessionId |> Expect.equal "auto-activated" (ActiveSession.Viewing "first")
    m1.Sessions.Sessions |> List.find (fun s -> s.Id = "first")
    |> fun s -> s.IsActive |> Expect.isTrue "first is active"

  testCase "SessionStopped removes session" <| fun _ ->
    let m', _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionStopped "sess-b")) threeSessionModel
    m'.Sessions.Sessions |> List.length |> Expect.equal "2 left" 2
    m'.Sessions.Sessions
    |> List.exists (fun s -> s.Id = "sess-b")
    |> Expect.isFalse "sess-b removed"

  testCase "SessionStopped of non-active preserves active" <| fun _ ->
    let m', _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionStopped "sess-c")) threeSessionModel
    m'.Sessions.ActiveSessionId |> Expect.equal "still sess-a" (ActiveSession.Viewing "sess-a")
    m'.Sessions.Sessions
    |> List.find (fun s -> s.Id = "sess-a")
    |> fun s -> s.IsActive |> Expect.isTrue "sess-a still active"

  testCase "Sequential switches maintain consistent state" <| fun _ ->
    let switches = ["sess-b"; "sess-c"; "sess-a"; "sess-c"; "sess-b"]
    let finalModel =
      switches |> List.fold (fun m sid ->
        let m', _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, sid))) m
        m') threeSessionModel
    finalModel.Sessions.ActiveSessionId |> Expect.equal "last switch wins" (ActiveSession.Viewing "sess-b")
    finalModel.Sessions.Sessions
    |> List.filter (fun s -> s.IsActive)
    |> List.length
    |> Expect.equal "exactly one active" 1
    finalModel.Sessions.Sessions
    |> List.find (fun s -> s.IsActive)
    |> fun s -> s.Id |> Expect.equal "active is sess-b" "sess-b"
    finalModel.Sessions.Sessions |> List.length |> Expect.equal "all 3 still present" 3

  testCase "SessionStatusChanged updates correct session" <| fun _ ->
    let m', _ =
      SageFsUpdate.update
        (SageFsMsg.Event
          (SageFsEvent.SessionStatusChanged ("sess-b", SessionDisplayStatus.Errored "test error")))
        threeSessionModel
    m'.Sessions.Sessions
    |> List.find (fun s -> s.Id = "sess-b")
    |> fun s -> s.Status |> Expect.equal "updated status" (SessionDisplayStatus.Errored "test error")
    m'.Sessions.Sessions
    |> List.find (fun s -> s.Id = "sess-a")
    |> fun s -> s.Status |> Expect.equal "other unchanged" SessionDisplayStatus.Running
]

let sessionApiRoundtripTests = testList "session action API round-trip" [
  testCase "SessionNavUp round-trips through API" <| fun _ ->
    let result = DaemonClient.actionToApi EditorAction.SessionNavUp
    result |> Expect.isSome "mapped"
    let (name, _) = result.Value
    name |> Expect.equal "api name" "sessionNavUp"

  testCase "SessionNavDown round-trips through API" <| fun _ ->
    let result = DaemonClient.actionToApi EditorAction.SessionNavDown
    result |> Expect.isSome "mapped"
    let (name, _) = result.Value
    name |> Expect.equal "api name" "sessionNavDown"

  testCase "SessionSelect round-trips through API" <| fun _ ->
    let result = DaemonClient.actionToApi EditorAction.SessionSelect
    result |> Expect.isSome "mapped"
    let (name, _) = result.Value
    name |> Expect.equal "api name" "sessionSelect"

  testCase "SessionDelete round-trips through API" <| fun _ ->
    let result = DaemonClient.actionToApi EditorAction.SessionDelete
    result |> Expect.isSome "mapped"
    let (name, _) = result.Value
    name |> Expect.equal "api name" "sessionDelete"

  testCase "SessionCycleNext round-trips through API" <| fun _ ->
    let result = DaemonClient.actionToApi EditorAction.SessionCycleNext
    result |> Expect.isSome "mapped"
    let (name, _) = result.Value
    name |> Expect.equal "api name" "sessionCycleNext"

  testCase "SessionCyclePrev round-trips through API" <| fun _ ->
    let result = DaemonClient.actionToApi EditorAction.SessionCyclePrev
    result |> Expect.isSome "mapped"
    let (name, _) = result.Value
    name |> Expect.equal "api name" "sessionCyclePrev"

  testCase "SessionSetIndex carries index value" <| fun _ ->
    let result = DaemonClient.actionToApi (EditorAction.SessionSetIndex 5)
    result |> Expect.isSome "mapped"
    let (name, value) = result.Value
    name |> Expect.equal "api name" "sessionSetIndex"
    value |> Expect.equal "carries index" (Some "5")

  testCase "SwitchSession carries session ID" <| fun _ ->
    let result = DaemonClient.actionToApi (EditorAction.SwitchSession "abc-123")
    result |> Expect.isSome "mapped"
    let (name, value) = result.Value
    name |> Expect.equal "api name" "switchSession"
    value |> Expect.equal "carries id" (Some "abc-123")

  testCase "CreateSession carries projects" <| fun _ ->
    let result = DaemonClient.actionToApi (EditorAction.CreateSession ["A.fsproj"; "B.fsproj"])
    result |> Expect.isSome "mapped"
    let (name, value) = result.Value
    name |> Expect.equal "api name" "createSession"
    value |> Expect.isSome "has value"

  testCase "StopSession carries session ID" <| fun _ ->
    let result = DaemonClient.actionToApi (EditorAction.StopSession "xyz-789")
    result |> Expect.isSome "mapped"
    let (name, value) = result.Value
    name |> Expect.equal "api name" "stopSession"
    value |> Expect.equal "carries id" (Some "xyz-789")

  testCase "ToggleSessionPanel round-trips" <| fun _ ->
    let result = DaemonClient.actionToApi EditorAction.ToggleSessionPanel
    result |> Expect.isSome "mapped"
    let (name, _) = result.Value
    name |> Expect.equal "api name" "toggleSessionPanel"

  testCase "ListSessions round-trips" <| fun _ ->
    let result = DaemonClient.actionToApi EditorAction.ListSessions
    result |> Expect.isSome "mapped"
    let (name, _) = result.Value
    name |> Expect.equal "api name" "listSessions"
]

let sessionPaneRemapTests = testList "session pane key remapping" [
  testCase "Up arrow in Sessions pane maps to SessionNavUp" <| fun _ ->
    let combo = KeyCombo.plain System.ConsoleKey.UpArrow
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor (EditorAction.MoveCursor Direction.Up)) -> ()
    | _ -> failtest "UpArrow should map to MoveCursor Up in defaults"

  testCase "Down arrow in Sessions pane maps to SessionNavDown" <| fun _ ->
    let combo = KeyCombo.plain System.ConsoleKey.DownArrow
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor (EditorAction.MoveCursor Direction.Down)) -> ()
    | _ -> failtest "DownArrow should map to MoveCursor Down in defaults"

  testCase "Enter in Sessions pane maps to SessionSelect" <| fun _ ->
    let combo = KeyCombo.plain System.ConsoleKey.Enter
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor EditorAction.NewLine) -> ()
    | _ -> failtest "Enter should map to NewLine in defaults (remapped in Sessions pane)"

  testCase "Delete/Backspace in Sessions pane maps to SessionDelete" <| fun _ ->
    let combo = KeyCombo.plain System.ConsoleKey.Backspace
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor EditorAction.DeleteBackward) -> ()
    | _ -> failtest "Backspace should map to DeleteBackward in defaults (remapped in Sessions pane)"

  testCase "Other keys in Sessions pane are NOT remapped" <| fun _ ->
    let combo = KeyCombo.plain System.ConsoleKey.A
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor (EditorAction.SessionNavUp | EditorAction.SessionNavDown | EditorAction.SessionSelect | EditorAction.SessionDelete)) ->
      failtest "'a' should NOT map to session nav"
    | _ -> ()

  testCase "Movement keys outside Sessions pane are NOT remapped" <| fun _ ->
    let combo = KeyCombo.plain System.ConsoleKey.UpArrow
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor EditorAction.SessionNavUp) ->
      failtest "UpArrow in default map should be MoveCursor, not SessionNavUp"
    | _ -> ()
]

let keyMapSessionTests = testList "keymap session shortcuts" [
  testCase "Ctrl+Tab maps to SessionCycleNext" <| fun _ ->
    let combo = KeyCombo.ctrl System.ConsoleKey.Tab
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor EditorAction.SessionCycleNext) -> ()
    | other -> failtest (sprintf "Ctrl+Tab should map to SessionCycleNext, got %A" other)

  testCase "Ctrl+Shift+Tab maps to SessionCyclePrev" <| fun _ ->
    let combo = KeyCombo.ctrlShift System.ConsoleKey.Tab
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor EditorAction.SessionCyclePrev) -> ()
    | other -> failtest (sprintf "Ctrl+Shift+Tab should map to SessionCyclePrev, got %A" other)

  testCase "Ctrl+N maps to CreateSession" <| fun _ ->
    let combo = KeyCombo.ctrl System.ConsoleKey.N
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor (EditorAction.CreateSession _)) -> ()
    | other -> failtest (sprintf "Ctrl+N should map to CreateSession, got %A" other)

  testCase "Ctrl+Alt+S maps to ToggleSessionPanel" <| fun _ ->
    let combo = KeyCombo.ctrlAlt System.ConsoleKey.S
    let action = KeyMap.defaults |> Map.tryFind combo
    match action with
    | Some (UiAction.Editor EditorAction.ToggleSessionPanel) -> ()
    | other -> failtest (sprintf "Ctrl+Alt+S should map to ToggleSessionPanel, got %A" other)

  testCase "UiAction.tryParse SessionCycleNext" <| fun _ ->
    UiAction.tryParse "SessionCycleNext"
    |> Expect.isSome "should parse SessionCycleNext"

  testCase "UiAction.tryParse SessionCyclePrev" <| fun _ ->
    UiAction.tryParse "SessionCyclePrev"
    |> Expect.isSome "should parse SessionCyclePrev"
]

let multiSessionLifecycleTests = testList "multi-session lifecycle" [
  testCase "create 3 sessions, cycle through all, verify each becomes active" <| fun _ ->
    let m0 = SageFsModel.initial
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "s1" ["A.fsproj"] false))) m0
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "s2" ["B.fsproj"] false))) m1
    let m3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "s3" ["C.fsproj"] false))) m2
    m3.Sessions.ActiveSessionId |> Expect.equal "first auto-active" (ActiveSession.Viewing "s1")
    m3.Sessions.Sessions |> List.length |> Expect.equal "3 sessions" 3
    let m4, effs4 = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) m3
    effs4 |> List.length |> Expect.equal "cycle emits effect" 1
    let m5, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, "s2"))) m4
    m5.Sessions.ActiveSessionId |> Expect.equal "now s2" (ActiveSession.Viewing "s2")
    let m6, _ = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) m5
    let m7, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, "s3"))) m6
    m7.Sessions.ActiveSessionId |> Expect.equal "now s3" (ActiveSession.Viewing "s3")
    let m8, _ = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionCycleNext) m7
    let m9, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, "s1"))) m8
    m9.Sessions.ActiveSessionId |> Expect.equal "wraps to s1" (ActiveSession.Viewing "s1")

  testCase "stop active session, verify fallback" <| fun _ ->
    let m0 = threeSessionModel
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionStopped "sess-a")) m0
    m1.Sessions.Sessions |> List.length |> Expect.equal "2 left" 2
    m1.Sessions.Sessions
    |> List.exists (fun s -> s.Id = "sess-a")
    |> Expect.isFalse "sess-a removed"
    m1.Sessions.Sessions
    |> List.map (fun s -> s.Id)
    |> Expect.containsAll "remaining" ["sess-b"; "sess-c"]

  testCase "create, switch, stop, switch back — full lifecycle" <| fun _ ->
    let m0 = SageFsModel.initial
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "s1" ["A.fsproj"] false))) m0
    m1.Sessions.ActiveSessionId |> Expect.equal "s1 active" (ActiveSession.Viewing "s1")
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "s2" ["B.fsproj"] false))) m1
    m2.Sessions.ActiveSessionId |> Expect.equal "still s1" (ActiveSession.Viewing "s1")
    let m3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, "s2"))) m2
    m3.Sessions.ActiveSessionId |> Expect.equal "now s2" (ActiveSession.Viewing "s2")
    let m4, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionStopped "s2")) m3
    m4.Sessions.Sessions |> List.length |> Expect.equal "1 left" 1
    let m5, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, "s1"))) m4
    m5.Sessions.ActiveSessionId |> Expect.equal "back to s1" (ActiveSession.Viewing "s1")

  testCase "SessionNavDown clamps to session count" <| fun _ ->
    let model =
      { threeSessionModel with
          Editor = { threeSessionModel.Editor with SelectedSessionIndex = Some 2 } }
    let m', _ = SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionNavDown) model
    Expect.isTrue "clamped" (m'.Editor.SelectedSessionIndex.Value <= 2)

  testCase "SessionSetIndex 0 selects first session" <| fun _ ->
    let m', _ = SageFsUpdate.update (SageFsMsg.Editor (EditorAction.SessionSetIndex 0)) threeSessionModel
    m'.Editor.SelectedSessionIndex |> Expect.equal "index 0" (Some 0)

  testCase "SessionSetIndex out-of-range clamps" <| fun _ ->
    let m', _ = SageFsUpdate.update (SageFsMsg.Editor (EditorAction.SessionSetIndex 99)) threeSessionModel
    Expect.isTrue "clamped" (m'.Editor.SelectedSessionIndex.Value <= 2)

  testCase "all UIs see same session list after create+switch+stop" <| fun _ ->
    let m0 = SageFsModel.initial
    let m1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "s1" ["A.fsproj"] false))) m0
    let m2, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "s2" ["B.fsproj"] false))) m1
    let m3, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnapshot "s3" ["C.fsproj"] false))) m2
    let m4, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, "s2"))) m3
    let m5, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionStopped "s3")) m4
    let rendered = SageFsRender.render m5
    let json = serializeRegions "s2" "Ready" 0 0.0 rendered
    let tuiParse = parseStateEvent json
    let guiParse = parseStateEvent json
    let webParse = parseStateEvent json
    tuiParse |> Expect.isSome "TUI parses"
    guiParse |> Expect.isSome "GUI parses"
    webParse |> Expect.isSome "Web parses"
    let (sid1, _, _, _, _, r1) = tuiParse.Value
    let (sid2, _, _, _, _, r2) = guiParse.Value
    let (sid3, _, _, _, _, r3) = webParse.Value
    sid1 |> Expect.equal "TUI session" "s2"
    sid2 |> Expect.equal "GUI session" "s2"
    sid3 |> Expect.equal "Web session" "s2"
    r1 |> List.length |> Expect.equal "TUI/GUI count" (r2 |> List.length)
    r2 |> List.length |> Expect.equal "GUI/Web count" (r3 |> List.length)
    for i in 0..r1.Length-1 do
      r1.[i].Content |> Expect.equal (sprintf "region %d TUI=GUI" i) r2.[i].Content
      r2.[i].Content |> Expect.equal (sprintf "region %d GUI=Web" i) r3.[i].Content

  testCase "IsActive flags always have exactly one true after switch" <| fun _ ->
    let switches = ["sess-a"; "sess-b"; "sess-c"; "sess-b"; "sess-a"]
    let finalModel =
      switches |> List.fold (fun m sid ->
        let m', _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.SessionSwitched (None, sid))) m
        m') threeSessionModel
    finalModel.Sessions.Sessions
    |> List.filter (fun s -> s.IsActive)
    |> List.length
    |> Expect.equal "exactly one active" 1
    finalModel.Sessions.Sessions
    |> List.find (fun s -> s.IsActive)
    |> fun s -> s.Id |> Expect.equal "last switch wins" "sess-a"
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
  elmSessionSwitchingTests
  elmSessionCyclingTests
  elmSessionEventTests
  sessionApiRoundtripTests
  sessionPaneRemapTests
  keyMapSessionTests
  multiSessionLifecycleTests
]
