module SageFs.Tests.SageFsAppTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.Diagnostics

[<Tests>]
let sageFsUpdateTests = testList "SageFsUpdate" [
  testCase "editor action routes through EditorUpdate" <| fun _ ->
    let model = SageFsModel.initial
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor (EditorAction.InsertChar 'x')) model
    ValidatedBuffer.text newModel.Editor.Buffer
    |> Expect.equal "should have inserted x" "x"
    effects |> Expect.isEmpty "no effects for insert"

  testCase "submit produces eval effect" <| fun _ ->
    let model = {
      SageFsModel.initial with
        Editor = {
          EditorState.initial with
            Buffer = ValidatedBuffer.insertChar 'a' ValidatedBuffer.empty } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.Submit) model
    effects |> Expect.hasLength "should have one effect" 1
    match effects.[0] with
    | SageFsEffect.Editor (EditorEffect.RequestEval code) ->
      code |> Expect.equal "should eval buffer content" "a"
    | _ -> failtest "expected RequestEval effect"

  testCase "EvalCompleted adds output line" <| fun _ ->
    let event = SageFsEvent.EvalCompleted ("s1", "val x = 42", [])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.RecentOutput |> Expect.hasLength "should have one output" 1
    newModel.RecentOutput.[0].Kind
    |> Expect.equal "should be Result" OutputKind.Result
    newModel.RecentOutput.[0].Text
    |> Expect.equal "should have output text" "val x = 42"

  testCase "EvalFailed adds error output" <| fun _ ->
    let event = SageFsEvent.EvalFailed ("s1", "type mismatch")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.RecentOutput.[0].Kind
    |> Expect.equal "should be Error" OutputKind.Error

  testCase "EvalCancelled adds info line" <| fun _ ->
    let event = SageFsEvent.EvalCancelled "s1"
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.RecentOutput.[0].Kind
    |> Expect.equal "should be Info" OutputKind.Info

  testCase "CompletionReady sets completion menu" <| fun _ ->
    let items = [
      { Label = "toString"; Kind = "method"; Detail = Some "string -> string" }
    ]
    let event = SageFsEvent.CompletionReady items
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.Editor.CompletionMenu |> Expect.isSome "should have menu"
    newModel.Editor.CompletionMenu.Value.Items
    |> Expect.hasLength "should have 1 item" 1

  testCase "SessionCreated adds to session list" <| fun _ ->
    let snap = {
      Id = "s1"; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let event = SageFsEvent.SessionCreated snap
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.Sessions.Sessions
    |> Expect.hasLength "should have 1 session" 1

  testCase "SessionSwitched updates active session" <| fun _ ->
    let snap1 = {
      Id = "s1"; Projects = []; Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let snap2 = {
      Id = "s2"; Projects = []; Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = false; WorkingDirectory = "" }
    let model = {
      SageFsModel.initial with
        Sessions = {
          SageFsModel.initial.Sessions with
            Sessions = [snap1; snap2] }
    }
    let event = SageFsEvent.SessionSwitched (Some "s1", "s2")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.ActiveSessionId
    |> Expect.equal "should be s2" (Some "s2")
    newModel.Sessions.Sessions
    |> List.find (fun s -> s.Id = "s2")
    |> fun s -> s.IsActive
    |> Expect.isTrue "s2 should be active"

  testCase "SessionStopped removes session" <| fun _ ->
    let snap = {
      Id = "s1"; Projects = []; Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let model = {
      SageFsModel.initial with
        Sessions = {
          SageFsModel.initial.Sessions with
            Sessions = [snap] }
    }
    let event = SageFsEvent.SessionStopped "s1"
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.Sessions
    |> Expect.isEmpty "session should be removed"

  testCase "DiagnosticsUpdated replaces diagnostics" <| fun _ ->
    let diag = {
      Message = "type error"
      Subcategory = "typecheck"
      Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
      Severity = DiagnosticSeverity.Error
    }
    let event = SageFsEvent.DiagnosticsUpdated ("s1", [diag])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.Diagnostics
    |> Expect.hasLength "should have 1 diagnostic" 1

  testCase "WarmupCompleted with no failures adds info" <| fun _ ->
    let event = SageFsEvent.WarmupCompleted (TimeSpan.FromSeconds 2.0, [])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.RecentOutput.[0].Text
    |> Expect.equal "should say complete" "Warmup complete"

  testCase "WarmupCompleted with failures adds error lines" <| fun _ ->
    let event =
      SageFsEvent.WarmupCompleted (TimeSpan.FromSeconds 2.0, ["ns1"; "ns2"])
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.RecentOutput
    |> Expect.hasLength "should have 2 error lines" 2

  testCase "FileReloaded success adds info line" <| fun _ ->
    let event =
      SageFsEvent.FileReloaded ("test.fs", TimeSpan.FromMilliseconds 50.0, Ok "loaded")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.RecentOutput.[0].Kind
    |> Expect.equal "should be Info" OutputKind.Info

  testCase "FileReloaded failure adds error line" <| fun _ ->
    let event =
      SageFsEvent.FileReloaded ("test.fs", TimeSpan.FromMilliseconds 50.0, Error "parse error")
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel.RecentOutput.[0].Kind
    |> Expect.equal "should be Error" OutputKind.Error

  testCase "EvalStarted is a no-op" <| fun _ ->
    let event = SageFsEvent.EvalStarted ("s1", "let x = 1")
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Event event) SageFsModel.initial
    newModel |> Expect.equal "model unchanged" SageFsModel.initial
    effects |> Expect.isEmpty "no effects"

  testCase "SessionStale marks session as stale" <| fun _ ->
    let snap = {
      Id = "s1"; Projects = []; Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let model = {
      SageFsModel.initial with
        Sessions = {
          SageFsModel.initial.Sessions with
            Sessions = [snap] }
    }
    let event = SageFsEvent.SessionStale ("s1", TimeSpan.FromMinutes 15.0)
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Event event) model
    newModel.Sessions.Sessions.[0].Status
    |> Expect.equal "should be Stale" SessionDisplayStatus.Stale
]

[<Tests>]
let sageFsRenderTests = testList "SageFsRender" [
  testCase "renders 4 regions from initial model" <| fun _ ->
    let regions = SageFsRender.render SageFsModel.initial
    regions |> Expect.hasLength "should have 4 regions" 4

  testCase "editor region is focusable" <| fun _ ->
    let regions = SageFsRender.render SageFsModel.initial
    regions |> List.find (fun r -> r.Id = "editor")
    |> fun r -> r.Flags.HasFlag RegionFlags.Focusable
    |> Expect.isTrue "editor should be focusable"

  testCase "output region shows recent output" <| fun _ ->
    let model = {
      SageFsModel.initial with
        RecentOutput = [
          { Kind = OutputKind.Result
            Text = "val x = 42"
            Timestamp = DateTime.UtcNow
            SessionId = "" }
        ]
    }
    let regions = SageFsRender.render model
    let outputRegion = regions |> List.find (fun r -> r.Id = "output")
    outputRegion.Content
    |> Expect.stringContains "should contain output" "val x = 42"

  testCase "diagnostics region shows diagnostics" <| fun _ ->
    let model = {
      SageFsModel.initial with
        Diagnostics = [{
          Message = "type error"
          Subcategory = "typecheck"
          Range = { StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5 }
          Severity = DiagnosticSeverity.Error
        }]
    }
    let regions = SageFsRender.render model
    let diagRegion = regions |> List.find (fun r -> r.Id = "diagnostics")
    diagRegion.Content
    |> Expect.stringContains "should contain error" "type error"

  testCase "sessions region shows active session" <| fun _ ->
    let snap = {
      Id = "s1"; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = DateTime.UtcNow; EvalCount = 0
      UpSince = DateTime.UtcNow; IsActive = true; WorkingDirectory = "" }
    let model = {
      SageFsModel.initial with
        Sessions = {
          SageFsModel.initial.Sessions with
            Sessions = [snap] }
    }
    let regions = SageFsRender.render model
    let sessionsRegion = regions |> List.find (fun r -> r.Id = "sessions")
    sessionsRegion.Content
    |> Expect.stringContains "should show session id" "s1"
    sessionsRegion.Content
    |> Expect.stringContains "should show active marker" "*"

  testCase "output region tags each line with correct [kind]" <| fun _ ->
    let now = DateTime.UtcNow
    let model =
      { SageFsModel.initial with
          RecentOutput = [
            { Kind = OutputKind.Result; Text = "val x = 1"; Timestamp = now; SessionId = "" }
            { Kind = OutputKind.Error; Text = "oops"; Timestamp = now; SessionId = "" }
            { Kind = OutputKind.Info; Text = "loaded"; Timestamp = now; SessionId = "" }
            { Kind = OutputKind.System; Text = "sys"; Timestamp = now; SessionId = "" }
          ] }
    let output =
      SageFsRender.render model
      |> List.find (fun r -> r.Id = "output")
    let lines = output.Content.Split('\n')
    // RecentOutput is newest-first; render reverses to oldest-first (FSI style)
    lines |> Array.length |> Expect.equal "4 output lines" 4
    lines.[0] |> Expect.stringContains "first tagged system (oldest)" "[system]"
    lines.[1] |> Expect.stringContains "second tagged info" "[info]"
    lines.[2] |> Expect.stringContains "third tagged error" "[error]"
    lines.[3] |> Expect.stringContains "fourth tagged result (newest)" "[result]"

  testCase "inactive session has no * marker" <| fun _ ->
    let now = DateTime.UtcNow
    let model =
      { SageFsModel.initial with
          Sessions =
            { SageFsModel.initial.Sessions with
                Sessions = [
                  { Id = "s1"; Projects = []; Status = SessionDisplayStatus.Running
                    LastActivity = now; EvalCount = 0; UpSince = now; IsActive = true; WorkingDirectory = "" }
                  { Id = "s2"; Projects = []; Status = SessionDisplayStatus.Starting
                    LastActivity = now; EvalCount = 0; UpSince = now; IsActive = false; WorkingDirectory = "" }
                ] } }
    let sessions =
      SageFsRender.render model
      |> List.find (fun r -> r.Id = "sessions")
    let lines = sessions.Content.Split('\n')
    lines |> Array.length |> Expect.equal "2 session lines" 2
    lines.[1].Contains("*")
    |> Expect.isFalse "inactive session has no *"

  testCase "empty model produces empty output and diagnostics" <| fun _ ->
    let regions = SageFsRender.render SageFsModel.initial
    let output = regions |> List.find (fun r -> r.Id = "output")
    let diag = regions |> List.find (fun r -> r.Id = "diagnostics")
    output.Content |> Expect.equal "empty output" ""
    diag.Content |> Expect.equal "empty diagnostics" ""

  testCase "region ids are correct" <| fun _ ->
    SageFsRender.render SageFsModel.initial
    |> List.map (fun r -> r.Id)
    |> Expect.equal "region ids in order" ["editor"; "output"; "diagnostics"; "sessions"]

  testProperty "sessions render contains id and status for every session"
    <| fun (sessionCount: byte) ->
      let count = int sessionCount % 10
      let now = DateTime.UtcNow
      let statuses = [|
        SessionDisplayStatus.Running
        SessionDisplayStatus.Starting
        SessionDisplayStatus.Suspended
        SessionDisplayStatus.Stale
        SessionDisplayStatus.Restarting |]
      let sessions =
        [ for i in 0..count-1 do
            { SessionSnapshot.Id = sprintf "session-%d" i
              Projects = []; Status = statuses.[i % statuses.Length]
              LastActivity = now; EvalCount = i; UpSince = now
              IsActive = (i = 0); WorkingDirectory = "" } ]
      let model =
        { SageFsModel.initial with
            Sessions = { SageFsModel.initial.Sessions with Sessions = sessions } }
      let sessRegion =
        SageFsRender.render model
        |> List.find (fun r -> r.Id = "sessions")
      if count = 0 then
        sessRegion.Content |> Expect.equal "empty" ""
      else
        let lines = sessRegion.Content.Split('\n')
        lines |> Array.length |> Expect.equal "one line per session" count
        for i in 0..count-1 do
          lines.[i]
          |> Expect.stringContains "contains session id" (sprintf "session-%d" i)
          lines.[i]
          |> Expect.stringContains "contains status brackets" "["
]

[<Tests>]
let elmIntegrationTests = testList "ElmLoop integration" [
  testCase "SageFs program wires update+render correctly" <| fun _ ->
    let mutable lastRegions : RenderRegion list = []
    let mutable lastModel : SageFsModel option = None
    let program : ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = fun _ _ -> async { () }
      OnModelChanged = fun model regions ->
        lastModel <- Some model
        lastRegions <- regions
    }
    let dispatch = (ElmLoop.start program SageFsModel.initial).Dispatch
    lastRegions |> Expect.hasLength "initial render should have 4 regions" 4

    dispatch (SageFsMsg.Editor (EditorAction.InsertChar 'h'))
    lastModel.Value.Editor.Buffer
    |> ValidatedBuffer.text
    |> Expect.equal "should have h" "h"

    dispatch (SageFsMsg.Event (SageFsEvent.EvalCompleted ("s1", "val x = 42", [])))
    lastModel.Value.RecentOutput
    |> Expect.hasLength "should have output" 1
    let outputRegion = lastRegions |> List.find (fun r -> r.Id = "output")
    outputRegion.Content
    |> Expect.stringContains "should show in render" "val x = 42"

  testCase "effects are dispatched asynchronously" <| fun _ ->
    let mutable effectExecuted = false
    let mutable resultReceived = false
    let program : ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> = {
      Update = SageFsUpdate.update
      Render = SageFsRender.render
      ExecuteEffect = fun dispatch effect ->
        async {
          effectExecuted <- true
          match effect with
          | SageFsEffect.Editor (EditorEffect.RequestEval code) ->
            dispatch (
              SageFsMsg.Event (
                SageFsEvent.EvalCompleted (
                  "s1", sprintf "val it = %s" code, [])))
          | _ -> ()
        }
      OnModelChanged = fun model _ ->
        if model.RecentOutput.Length > 0 then resultReceived <- true
    }
    let dispatch = (ElmLoop.start program SageFsModel.initial).Dispatch
    dispatch (SageFsMsg.Editor (EditorAction.InsertChar '1'))
    dispatch (SageFsMsg.Editor EditorAction.Submit)
    System.Threading.Thread.Sleep 200
    effectExecuted |> Expect.isTrue "effect should have been executed"
    resultReceived |> Expect.isTrue "result should have been received"
]

[<Tests>]
let sessionNavAppTests = testList "SageFsUpdate session navigation" [
  let mkSnap id isActive = {
    Id = id; Projects = []; Status = SessionDisplayStatus.Running
    LastActivity = System.DateTime.UtcNow; EvalCount = 0
    UpSince = System.DateTime.UtcNow; IsActive = isActive; WorkingDirectory = "" }

  let withSessions (snaps: SessionSnapshot list) (model: SageFsModel) : SageFsModel =
    { model with
        Sessions = {
          model.Sessions with Sessions = snaps } }

  testCase "SessionNavDown clamps to session count" <| fun _ ->
    let model =
      SageFsModel.initial
      |> withSessions [mkSnap "s1" true; mkSnap "s2" false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 1 } }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionNavDown) model'
    newModel.Editor.SelectedSessionIndex
    |> Expect.equal "should clamp to last index" (Some 1)

  testCase "SessionSelect emits RequestSessionSwitch with correct id" <| fun _ ->
    let model =
      SageFsModel.initial
      |> withSessions [mkSnap "s1" true; mkSnap "s2" false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 1 } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model'
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)] ->
      sid |> Expect.equal "should switch to s2" "s2"
    | _ -> failtest (sprintf "expected RequestSessionSwitch, got %A" effects)

  testCase "SessionDelete emits RequestSessionStop with correct id" <| fun _ ->
    let model =
      SageFsModel.initial
      |> withSessions [mkSnap "s1" true; mkSnap "s2" false]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 0 } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionDelete) model'
    match effects with
    | [SageFsEffect.Editor (EditorEffect.RequestSessionStop sid)] ->
      sid |> Expect.equal "should stop s1" "s1"
    | _ -> failtest (sprintf "expected RequestSessionStop, got %A" effects)

  testCase "SessionSelect with no selection does nothing" <| fun _ ->
    let model = SageFsModel.initial |> withSessions [mkSnap "s1" true]
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model
    effects |> Expect.isEmpty "no effects when no selection"

  testCase "SessionSelect with out-of-range index does nothing" <| fun _ ->
    let model =
      SageFsModel.initial
      |> withSessions [mkSnap "s1" true]
    let model' = { model with Editor = { model.Editor with SelectedSessionIndex = Some 5 } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.SessionSelect) model'
    effects |> Expect.isEmpty "no effects for OOB index"

  testCase "ClearOutput clears recent output" <| fun _ ->
    let model = {
      SageFsModel.initial with
        RecentOutput = [
          { Kind = OutputKind.Result; Text = "hello"
            Timestamp = System.DateTime.UtcNow; SessionId = "" }
        ] }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.ClearOutput) model
    newModel.RecentOutput |> Expect.isEmpty "output should be cleared"

  testCase "InsertChar remapped to PromptChar when prompt active" <| fun _ ->
    let model = {
      SageFsModel.initial with
        Editor = { EditorState.initial with
                     Prompt = Some { Label = "Dir"; Input = "ab"; Purpose = PromptPurpose.CreateSessionDir } } }
    let newModel, _ =
      SageFsUpdate.update (SageFsMsg.Editor (EditorAction.InsertChar 'c')) model
    newModel.Editor.Prompt.Value.Input |> Expect.equal "should append via remap" "abc"

  testCase "NewLine remapped to PromptConfirm when prompt active" <| fun _ ->
    let model = {
      SageFsModel.initial with
        Editor = { EditorState.initial with
                     Prompt = Some { Label = "Dir"; Input = "C:\\Code"; Purpose = PromptPurpose.CreateSessionDir } } }
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.NewLine) model
    effects |> List.exists (fun e ->
      match e with
      | SageFsEffect.Editor (EditorEffect.RequestSessionCreate _) -> true
      | _ -> false)
    |> Expect.isTrue "should produce session create effect"

  testCase "Cancel remapped to PromptCancel when prompt active" <| fun _ ->
    let model = {
      SageFsModel.initial with
        Editor = { EditorState.initial with
                     Prompt = Some { Label = "Dir"; Input = "test"; Purpose = PromptPurpose.CreateSessionDir } } }
    let newModel, effects =
      SageFsUpdate.update (SageFsMsg.Editor EditorAction.Cancel) model
    newModel.Editor.Prompt |> Expect.isNone "prompt should close"
    effects |> Expect.isEmpty "no effects on cancel"
]
