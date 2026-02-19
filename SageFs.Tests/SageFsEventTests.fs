module SageFs.Tests.SageFsEventTests

open System
open Expecto
open Expecto.Flip
open SageFs

let now = DateTime(2026, 2, 14, 12, 0, 0)

[<Tests>]
let outputLineTests = testList "OutputLine" [
  testCase "can create Result output" <| fun _ ->
    let line = { Kind = OutputKind.Result; Text = "val x: int = 42"; Timestamp = now; SessionId = "" }
    line.Kind |> Expect.equal "kind" OutputKind.Result
    line.Text |> Expect.equal "text" "val x: int = 42"

  testCase "can create Error output" <| fun _ ->
    let line = { Kind = OutputKind.Error; Text = "FS0001: type mismatch"; Timestamp = now; SessionId = "" }
    line.Kind |> Expect.equal "kind" OutputKind.Error

  testCase "all OutputKind cases are distinct" <| fun _ ->
    let kinds = [OutputKind.Result; OutputKind.Error; OutputKind.Info; OutputKind.System]
    kinds |> List.distinct |> List.length
    |> Expect.equal "4 distinct kinds" 4
]

[<Tests>]
let SageFsEventTests = testList "SageFsEvent" [
  testCase "EvalStarted carries session and code" <| fun _ ->
    let evt = SageFsEvent.EvalStarted("s1", "let x = 42")
    match evt with
    | SageFsEvent.EvalStarted(sid, code) ->
      sid |> Expect.equal "session" "s1"
      code |> Expect.equal "code" "let x = 42"
    | _ -> failwith "wrong case"

  testCase "EvalCompleted carries output and diagnostics" <| fun _ ->
    let evt = SageFsEvent.EvalCompleted("s1", "val x: int = 42", [])
    match evt with
    | SageFsEvent.EvalCompleted(sid, output, diags) ->
      sid |> Expect.equal "session" "s1"
      output |> Expect.equal "output" "val x: int = 42"
      diags |> Expect.equal "no diags" []
    | _ -> failwith "wrong case"

  testCase "SessionCreated carries snapshot" <| fun _ ->
    let snap = {
      Id = "s1"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = now; EvalCount = 0
      UpSince = now; IsActive = true; WorkingDirectory = "" }
    let evt = SageFsEvent.SessionCreated snap
    match evt with
    | SageFsEvent.SessionCreated s -> s.Id |> Expect.equal "id" "s1"
    | _ -> failwith "wrong case"

  testCase "FileChanged carries path and action" <| fun _ ->
    let evt = SageFsEvent.FileChanged("src/Main.fs", FileWatchAction.Changed)
    match evt with
    | SageFsEvent.FileChanged(p, a) ->
      p |> Expect.equal "path" "src/Main.fs"
      a |> Expect.equal "action" FileWatchAction.Changed
    | _ -> failwith "wrong case"

  testCase "WarmupProgress carries step info" <| fun _ ->
    let evt = SageFsEvent.WarmupProgress(3, 10, "FSharp.Core")
    match evt with
    | SageFsEvent.WarmupProgress(s, t, name) ->
      s |> Expect.equal "step" 3
      t |> Expect.equal "total" 10
      name |> Expect.equal "asm" "FSharp.Core"
    | _ -> failwith "wrong case"

  testCase "SessionStale carries duration" <| fun _ ->
    let dur = TimeSpan.FromMinutes(15.0)
    let evt = SageFsEvent.SessionStale("s1", dur)
    match evt with
    | SageFsEvent.SessionStale(sid, d) ->
      sid |> Expect.equal "session" "s1"
      d |> Expect.equal "duration" dur
    | _ -> failwith "wrong case"
]

[<Tests>]
let SageFsViewTests = testList "SageFsView" [
  testCase "can create a minimal view" <| fun _ ->
    let view = {
      Buffer = ValidatedBuffer.empty
      CompletionMenu = None
      ActiveSession = {
        Id = "s1"; Name = None; Projects = ["Test.fsproj"]
        Status = SessionDisplayStatus.Running
        LastActivity = now; EvalCount = 0
        UpSince = now; IsActive = true; WorkingDirectory = "" }
      RecentOutput = []
      Diagnostics = []
      WatchStatus = None
    }
    view.Buffer |> Expect.equal "empty buffer" ValidatedBuffer.empty
    view.RecentOutput |> Expect.isEmpty "no output"
    view.Diagnostics |> Expect.isEmpty "no diagnostics"

  testCase "view with output lines" <| fun _ ->
    let lines = [
      { Kind = OutputKind.Result; Text = "val x = 42"; Timestamp = now; SessionId = "" }
      { Kind = OutputKind.Error; Text = "error FS0001"; Timestamp = now; SessionId = "" }
    ]
    let view = {
      Buffer = ValidatedBuffer.empty
      CompletionMenu = None
      ActiveSession = {
        Id = "s1"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
        LastActivity = now; EvalCount = 1; UpSince = now; IsActive = true; WorkingDirectory = "" }
      RecentOutput = lines
      Diagnostics = []
      WatchStatus = None
    }
    view.RecentOutput.Length |> Expect.equal "2 lines" 2

  testCase "view with watch status" <| fun _ ->
    let ws = WatchStatus.Active 5
    let view = {
      Buffer = ValidatedBuffer.empty
      CompletionMenu = None
      ActiveSession = {
        Id = "s1"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
        LastActivity = now; EvalCount = 0; UpSince = now; IsActive = true; WorkingDirectory = "" }
      RecentOutput = []
      Diagnostics = []
      WatchStatus = Some ws
    }
    view.WatchStatus |> Expect.isSome "has watch"
    match view.WatchStatus.Value with
    | WatchStatus.Active n -> n |> Expect.equal "5 files" 5
    | _ -> failwith "expected Active"
]

[<Tests>]
let fileWatchActionTests = testList "FileWatchAction" [
  testCase "all cases are distinct" <| fun _ ->
    let actions = [FileWatchAction.Changed; FileWatchAction.Created; FileWatchAction.Deleted; FileWatchAction.Renamed]
    actions |> List.distinct |> List.length
    |> Expect.equal "4 distinct actions" 4
]
