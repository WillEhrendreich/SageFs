module SageFs.Tests.SageFsEventTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Tests.SharedGenerators

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
    let s1 = testSessionId "aa000001"
    let snap = {
      Id = s1; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = now; EvalCount = 0
      UpSince = now; IsActive = true; WorkingDirectory = "" }
    let evt = SageFsEvent.SessionCreated snap
    match evt with
    | SageFsEvent.SessionCreated s -> s.Id |> Expect.equal "id" s1
    | _ -> failwith "wrong case"

  testCase "OutputEmitted carries output line" <| fun _ ->
    let line = { Kind = OutputKind.System; Text = "Disabled warmup auto-open"; Timestamp = now; SessionId = "" }
    let evt = SageFsEvent.OutputEmitted line
    match evt with
    | SageFsEvent.OutputEmitted actual ->
      actual |> Expect.equal "line" line
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
        Id = testSessionId "aa000001"; Name = None; Projects = ["Test.fsproj"]
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
        Id = testSessionId "aa000001"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
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
        Id = testSessionId "aa000001"; Name = None; Projects = []; Status = SessionDisplayStatus.Running
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

let private mkLine text = {
  Kind = OutputKind.Result; Text = text
  Timestamp = DateTime(2026, 3, 3, 12, 0, 0); SessionId = "s1" }

[<Tests>]
let outputRingBufferTests = testList "OutputRingBuffer" [
  testCase "fresh buffer has version 0" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Version |> Expect.equal "starts at 0" 0

  testCase "each Add increments version by 1" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "a")
    buf.Version |> Expect.equal "after 1 add" 1
    buf.Add(mkLine "b")
    buf.Version |> Expect.equal "after 2 adds" 2

  testCase "Clear increments version" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "a")
    buf.Add(mkLine "b")
    let v = buf.Version
    buf.Clear()
    buf.Version |> Expect.equal "incremented after clear" (v + 1)

  testProperty "version equals total Add + Clear count" <| fun (adds: NonNegativeInt) (clears: NonNegativeInt) ->
    let buf = OutputRingBuffer(100)
    for i in 1..adds.Get do buf.Add(mkLine (sprintf "line%d" i))
    for _ in 1..clears.Get do buf.Clear()
    buf.Version = adds.Get + clears.Get

  testProperty "version never decreases across any mutation sequence" <| fun (ops: bool list) ->
    let buf = OutputRingBuffer(50)
    let mutable prev = buf.Version
    let mutable ok = true
    for isAdd in ops do
      match isAdd with
      | true -> buf.Add(mkLine "x")
      | false -> buf.Clear()
      match buf.Version >= prev with
      | true -> prev <- buf.Version
      | false -> ok <- false
    ok

  testCase "RenderAllCached returns same content on repeated calls" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "hello")
    let r1 = buf.RenderAllCached()
    let r2 = buf.RenderAllCached()
    r1 |> Expect.equal "should be identical" r2

  testCase "RenderAllCached invalidates after Add" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "first")
    let r1 = buf.RenderAllCached()
    buf.Add(mkLine "second")
    let r2 = buf.RenderAllCached()
    r2 |> Expect.stringContains "should include second" "second"
    (r1 = r2) |> Expect.isFalse "should differ after mutation"

  testCase "RenderAllCached invalidates after Clear" <| fun _ ->
    let buf = OutputRingBuffer(10)
    buf.Add(mkLine "stuff")
    let r1 = buf.RenderAllCached()
    buf.Clear()
    let r2 = buf.RenderAllCached()
    r2 |> Expect.equal "empty after clear" ""
    (r1 = r2) |> Expect.isFalse "should differ after clear"
]
