module SageFs.Tests.ElmLoopStateMachineTests

open Expecto
open Expecto.Flip
open System
open System.Threading
open SageFs

type TestModel = { Count: int; Log: string list }
type TestMsg = Increment | Decrement | AddLog of string | Reset | TriggerEffect of string
type TestEffect = LogEffect of string | DelayedMsg of TestMsg
type TestRegion = CountRegion of int | LogRegion of string list

let initialModel = { Count = 0; Log = [] }

let waitFor (condition: unit -> bool) (timeoutMs: int) =
  let sw = Diagnostics.Stopwatch.StartNew()
  while not (condition ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
    Thread.Sleep 10
  condition ()

let makeTestProgram (onModelChanged: TestModel -> TestRegion list -> unit) =
  { Update = fun msg model ->
      match msg with
      | Increment -> { model with Count = model.Count + 1 }, []
      | Decrement -> { model with Count = model.Count - 1 }, []
      | AddLog s -> { model with Log = s :: model.Log }, []
      | Reset -> { Count = 0; Log = [] }, []
      | TriggerEffect s -> model, [LogEffect s]
    Render = fun model ->
      [ CountRegion model.Count; LogRegion model.Log ]
    ExecuteEffect = fun dispatch effect -> async {
      match effect with
      | LogEffect s -> dispatch (AddLog s)
      | DelayedMsg msg ->
        do! Async.Sleep 10
        dispatch msg
    }
    OnModelChanged = onModelChanged
    OnSystemAlarm = fun _ _ -> () }

[<Tests>]
let elmLoopStateMachineTests =
  testList "ElmLoop state machine" [

    testCase "initial model is preserved" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.GetModel() |> Expect.equal "GetModel returns initial model" initialModel
      cts.Cancel()

    testCase "single dispatch updates model" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      waitFor (fun () -> (rt.GetModel()).Count = 1) 5000 |> ignore
      (rt.GetModel()).Count |> Expect.equal "Count should be 1 after Increment" 1
      cts.Cancel()

    testCase "multiple dispatches batch" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      for _ in 1..5 do rt.Dispatch Increment
      waitFor (fun () -> (rt.GetModel()).Count = 5) 5000 |> ignore
      (rt.GetModel()).Count |> Expect.equal "Count should be 5 after 5 Increments" 5
      cts.Cancel()

    testCase "effects trigger messages" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch (TriggerEffect "hello")
      waitFor (fun () -> (rt.GetModel()).Log |> List.contains "hello") 5000 |> ignore
      (rt.GetModel()).Log
      |> List.contains "hello"
      |> Expect.isTrue "log should contain 'hello' from LogEffect"
      cts.Cancel()

    testCase "render produces regions from model" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      waitFor (fun () ->
        rt.GetRegions() |> List.contains (CountRegion 1)) 5000
      |> Expect.isTrue "regions should contain CountRegion 1"
      cts.Cancel()

    testCase "OnModelChanged callback fires" <| fun _ ->
      use cts = new CancellationTokenSource()
      let calls = Collections.Concurrent.ConcurrentBag<TestModel * TestRegion list>()
      let prog = makeTestProgram (fun m r -> calls.Add(m, r))
      let rt = ElmLoop.start prog initialModel cts.Token

      // Initial OnModelChanged fires during start
      let initialCallCount = calls.Count
      rt.Dispatch Increment
      waitFor (fun () -> calls.Count > initialCallCount) 5000 |> ignore
      let model, regions =
        calls |> Seq.find (fun (m, _) -> m.Count = 1)
      model.Count |> Expect.equal "callback received updated model" 1
      regions |> List.contains (CountRegion 1)
      |> Expect.isTrue "callback received updated regions"
      cts.Cancel()

    testCase "dispatch after cancellation does not crash" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      cts.Cancel()
      // Give drain thread time to exit
      Thread.Sleep 50
      // Dispatching after cancellation should not throw
      rt.Dispatch Increment
      rt.Dispatch Decrement

    testCase "loop settles after dispatch" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      let settled = waitFor (fun () -> (rt.GetModel()).Count = 1) 5000
      settled |> Expect.isTrue "loop should settle within timeout"
      cts.Cancel()

    testCase "GetRegions returns latest after multiple dispatches" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      rt.Dispatch Increment
      rt.Dispatch (AddLog "a")
      waitFor (fun () ->
        let regions = rt.GetRegions()
        regions = [ CountRegion 2; LogRegion ["a"] ]) 5000
      |> Expect.isTrue "regions reflect final state"
      cts.Cancel()

    testCase "effects run asynchronously" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog =
        { Update = fun msg model ->
            match msg with
            | Increment -> { model with Count = model.Count + 1 }, []
            | AddLog s -> { model with Log = s :: model.Log }, []
            | TriggerEffect s -> model, [DelayedMsg Increment]
            | _ -> model, []
          Render = fun model ->
            [ CountRegion model.Count; LogRegion model.Log ]
          ExecuteEffect = fun dispatch effect -> async {
            match effect with
            | LogEffect s -> dispatch (AddLog s)
            | DelayedMsg msg ->
              do! Async.Sleep 10
              dispatch msg
          }
          OnModelChanged = fun _ _ -> ()
          OnSystemAlarm = fun _ _ -> () }
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch (TriggerEffect "async")
      waitFor (fun () -> (rt.GetModel()).Count = 1) 5000 |> ignore
      (rt.GetModel()).Count
      |> Expect.equal "DelayedMsg effect should have dispatched Increment" 1
      cts.Cancel()

    testCase "Reset returns to initial state" <| fun _ ->
      use cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      rt.Dispatch Increment
      rt.Dispatch (AddLog "before-reset")
      waitFor (fun () -> (rt.GetModel()).Count = 2) 5000 |> ignore

      rt.Dispatch Reset
      waitFor (fun () -> (rt.GetModel()).Count = 0) 5000 |> ignore
      let m = rt.GetModel()
      m.Count |> Expect.equal "Count should be 0 after Reset" 0
      m.Log |> Expect.equal "Log should be empty after Reset" []
      cts.Cancel()
  ]
