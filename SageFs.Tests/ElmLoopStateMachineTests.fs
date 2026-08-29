module SageFs.Tests.ElmLoopStateMachineTests

open Expecto
open Expecto.Flip
open System
open System.Threading
open System.Threading.Tasks
open SageFs

type TestModel = { Count: int; Log: string list }
type TestMsg = Increment | Decrement | AddLog of string | Reset | TriggerEffect of string
type TestEffect = LogEffect of string | DelayedMsg of TestMsg
type TestRegion = CountRegion of int | LogRegion of string list

let initialModel = { Count = 0; Log = [] }

let waitForAsync (condition: unit -> bool) (timeoutMs: int) =
  task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable ok = false
    while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
      if condition () then ok <- true
      else do! Task.Delay 10
    return ok
  }

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

    testTask "initial model is preserved" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.GetModel() |> Expect.equal "GetModel returns initial model" initialModel
      cts.Cancel()
      cts.Dispose()
    }

    testTask "single dispatch updates model" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      let! updated = waitForAsync (fun () -> (rt.GetModel()).Count = 1) 5000
      updated |> Expect.isTrue "model should update after Increment"
      (rt.GetModel()).Count |> Expect.equal "Count should be 1 after Increment" 1
      cts.Cancel()
      cts.Dispose()
    }

    testTask "multiple dispatches batch" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      for _ in 1..5 do rt.Dispatch Increment
      let! updated = waitForAsync (fun () -> (rt.GetModel()).Count = 5) 5000
      updated |> Expect.isTrue "model should reach 5"
      (rt.GetModel()).Count |> Expect.equal "Count should be 5 after 5 Increments" 5
      cts.Cancel()
      cts.Dispose()
    }

    testTask "effects trigger messages" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch (TriggerEffect "hello")
      let! logged = waitForAsync (fun () -> (rt.GetModel()).Log |> List.contains "hello") 5000
      logged |> Expect.isTrue "effect should dispatch AddLog"
      (rt.GetModel()).Log
      |> List.contains "hello"
      |> Expect.isTrue "log should contain 'hello' from LogEffect"
      cts.Cancel()
      cts.Dispose()
    }

    testTask "render produces regions from model" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      let! rendered = waitForAsync (fun () ->
        rt.GetRegions() |> List.contains (CountRegion 1)) 5000
      rendered |> Expect.isTrue "regions should contain CountRegion 1"
      cts.Cancel()
      cts.Dispose()
    }

    testTask "OnModelChanged callback fires" {
      let cts = new CancellationTokenSource()
      let calls = Collections.Concurrent.ConcurrentBag<TestModel * TestRegion list>()
      let prog = makeTestProgram (fun m r -> calls.Add(m, r))
      let rt = ElmLoop.start prog initialModel cts.Token

      // Initial OnModelChanged fires during start
      let initialCallCount = calls.Count
      rt.Dispatch Increment
      let! fired = waitForAsync (fun () -> calls.Count > initialCallCount) 5000
      fired |> Expect.isTrue "callback should fire"
      let model, regions =
        calls |> Seq.find (fun (m, _) -> m.Count = 1)
      model.Count |> Expect.equal "callback received updated model" 1
      regions |> List.contains (CountRegion 1)
      |> Expect.isTrue "callback received updated regions"
      cts.Cancel()
      cts.Dispose()
    }

    testTask "dispatch after cancellation does not crash" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      cts.Cancel()
      // Give drain thread time to exit
      do! Task.Delay 50
      // Dispatching after cancellation should not throw
      rt.Dispatch Increment
      rt.Dispatch Decrement
      cts.Dispose()
    }

    testTask "loop settles after dispatch" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      let! settled = waitForAsync (fun () -> (rt.GetModel()).Count = 1) 5000
      settled |> Expect.isTrue "loop should settle within timeout"
      cts.Cancel()
      cts.Dispose()
    }

    testTask "GetRegions returns latest after multiple dispatches" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      rt.Dispatch Increment
      rt.Dispatch (AddLog "a")
      let! updated = waitForAsync (fun () ->
        let regions = rt.GetRegions()
        regions = [ CountRegion 2; LogRegion ["a"] ]) 5000
      updated |> Expect.isTrue "regions reflect final state"
      cts.Cancel()
      cts.Dispose()
    }

    testTask "effects run asynchronously" {
      let cts = new CancellationTokenSource()
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
      let! updated = waitForAsync (fun () -> (rt.GetModel()).Count = 1) 5000
      updated |> Expect.isTrue "DelayedMsg effect should have dispatched Increment"
      (rt.GetModel()).Count
      |> Expect.equal "DelayedMsg effect should have dispatched Increment" 1
      cts.Cancel()
      cts.Dispose()
    }

    testTask "Reset returns to initial state" {
      let cts = new CancellationTokenSource()
      let prog = makeTestProgram (fun _ _ -> ())
      let rt = ElmLoop.start prog initialModel cts.Token

      rt.Dispatch Increment
      rt.Dispatch Increment
      rt.Dispatch (AddLog "before-reset")
      let! counted = waitForAsync (fun () -> (rt.GetModel()).Count = 2) 5000
      counted |> Expect.isTrue "model should reach 2"

      rt.Dispatch Reset
      let! reset = waitForAsync (fun () -> (rt.GetModel()).Count = 0) 5000
      reset |> Expect.isTrue "model should reset to 0"
      let m = rt.GetModel()
      m.Count |> Expect.equal "Count should be 0 after Reset" 0
      m.Log |> Expect.equal "Log should be empty after Reset" []
      cts.Cancel()
      cts.Dispose()
    }
  ]
