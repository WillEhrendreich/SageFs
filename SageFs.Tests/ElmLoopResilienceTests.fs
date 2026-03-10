module SageFs.Tests.ElmLoopResilienceTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Utils
open System.Collections.Concurrent
open System.Collections.Generic

let waitFor (condition: unit -> bool) (timeoutMs: int) =
  let sw = System.Diagnostics.Stopwatch.StartNew()
  while not (condition ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
    System.Threading.Thread.Sleep 10
  condition ()

let wait () =
  System.Threading.Thread.Sleep 50

[<Tests>]
let elmLoopResilienceTests =
  // Decision: Elm loop catches exceptions in Update/Render/OnModelChanged/Effect.
  //   A single bad message must not kill the dispatch loop — all frontends depend on it.
  // Assumes (2026-01): Every frontend shares one ElmProgram per instance.
  // See ElmLoop.fs header for the full contract.
  testList "ElmLoop resilience" [

    testCase "Update throws: model stays, no effect, loop recovers" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model ->
          if msg = 2 then failwith "Update boom!"
          model + 1, [1]
        Render = fun model -> [model * 10]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None

      rt.Dispatch 1  // model=1, effect fires
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      effCount.Value |> Expect.equal "effect fired on d1" 1
      rt.GetModel() |> Expect.equal "model is 1" 1

      rt.Dispatch 2  // Update throws, model stays 1, no effect
      wait ()
      effCount.Value |> Expect.equal "no new effect on d2" 1
      rt.GetModel() |> Expect.equal "model still 1" 1

      rt.Dispatch 3  // recovers, model=2
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      effCount.Value |> Expect.equal "effect fires on d3 — loop survived throw (see ElmLoop.fs header)" 2
      rt.GetModel() |> Expect.equal "model is 2" 2

    testCase "Render throws: previous regions preserved, effects fire" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, [1]
        Render = fun model ->
          if model = 2 then failwith "Render boom!"
          [model * 10]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None

      rt.Dispatch 1  // model=1, regions=[10]
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      rt.GetRegions() |> Expect.equal "regions from d1" [10]

      rt.Dispatch 2  // model=2, Render throws, regions stay [10]
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      effCount.Value |> Expect.equal "effect still fires" 2
      rt.GetRegions() |> Expect.equal "regions preserved" [10]

      rt.Dispatch 3  // model=3, Render succeeds with [30]
      waitFor (fun () -> effCount.Value >= 3) 2000 |> ignore
      rt.GetRegions() |> Expect.equal "regions recover" [30]

    testCase "OnModelChanged throws: effects still fire" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, [1]
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun model _ ->
          if model = 2 then failwith "OnModelChanged boom!"
        OnSystemAlarm = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None

      rt.Dispatch 1
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      effCount.Value |> Expect.equal "effect on d1" 1

      rt.Dispatch 2  // OnModelChanged throws, but effect should still fire
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      effCount.Value |> Expect.equal "effect on d2 despite throw — resilience contract (see ElmLoop.fs header)" 2
      rt.GetModel() |> Expect.equal "model updated" 2

      rt.Dispatch 3  // recovers
      waitFor (fun () -> effCount.Value >= 3) 2000 |> ignore
      effCount.Value |> Expect.equal "effect on d3" 3

    testCase "Effect throws: loop still works for subsequent dispatches" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, [msg]
        Render = fun model -> [model]
        ExecuteEffect = fun _ eff ->
          async {
            if eff = 2 then failwith "Effect boom!"
            System.Threading.Interlocked.Increment effCount |> ignore
          }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None

      rt.Dispatch 1  // effect=1, succeeds
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      effCount.Value |> Expect.equal "effect 1 ran" 1

      rt.Dispatch 2  // effect=2, throws
      wait ()
      effCount.Value |> Expect.equal "effect 2 failed" 1

      rt.Dispatch 3  // effect=3, succeeds
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      effCount.Value |> Expect.equal "effect 3 ran — loop survived bad effect (see ElmLoop.fs header)" 2

    testCase "Initial Render throws: starts with empty regions" <| fun _ ->
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, []
        Render = fun model ->
          if model = 0 then failwith "Initial Render boom!"
          [model * 10]
        ExecuteEffect = fun _ _ -> async { () }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None

      rt.GetRegions()|> Expect.equal "empty regions on failed init" []
      rt.GetModel() |> Expect.equal "model still 0" 0

      rt.Dispatch 1  // Render succeeds now
      wait ()
      rt.GetRegions() |> Expect.equal "regions recover" [10]

    testCase "Initial OnModelChanged throws: loop still works" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + 1, [1]
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun model _ ->
          if model = 0 then failwith "Initial OnModelChanged boom!"
        OnSystemAlarm = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None

      rt.GetModel() |> Expect.equal "model is 0" 0
      rt.GetRegions() |> Expect.equal "regions rendered despite throw" [0]

      rt.Dispatch 1
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      effCount.Value |> Expect.equal "effect fires" 1
      rt.GetModel() |> Expect.equal "model updated" 1

    testCase "Multiple failures in sequence: loop survives all" <| fun _ ->
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model ->
          if msg = 3 then failwith "Update boom on 3!"
          model + 1, [msg]
        Render = fun model ->
          if model = 2 then failwith "Render boom on 2!"
          [model]
        ExecuteEffect = fun _ eff ->
          async {
            if eff = 5 then failwith "Effect boom on 5!"
            System.Threading.Interlocked.Increment effCount |> ignore
          }
        OnModelChanged = fun model _ ->
          if model = 4 then failwith "OnModelChanged boom on 4!"
        OnSystemAlarm = fun _ _ -> ()
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None

      rt.Dispatch 1  // all good, model=1
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      rt.GetModel() |> Expect.equal "d1 model" 1
      effCount.Value |> Expect.equal "d1 effects" 1

      rt.Dispatch 2  // model=2, Render throws, regions preserved
      waitFor (fun () -> effCount.Value >= 2) 2000 |> ignore
      rt.GetModel() |> Expect.equal "d2 model" 2
      rt.GetRegions() |> Expect.equal "d2 regions preserved" [1]

      rt.Dispatch 3  // Update throws, model stays 2
      wait ()
      rt.GetModel() |> Expect.equal "d3 model unchanged" 2

      rt.Dispatch 4  // model=3, OnModelChanged doesn't throw (model=3, not 4)
      waitFor (fun () -> effCount.Value >= 3) 2000 |> ignore
      rt.GetModel() |> Expect.equal "d4 model" 3

      rt.Dispatch 5  // model=4, OnModelChanged throws; effect=5 throws
      wait ()
      rt.GetModel() |> Expect.equal "d5 model" 4

      rt.Dispatch 6  // model=5, all good
      waitFor (fun () -> effCount.Value >= 4) 2000 |> ignore
      rt.GetModel() |> Expect.equal "d6 model" 5
      // effects: d1(1)+d2(2)+d4(4)+d5(5 fails)+d6(6) = 4 successes
      effCount.Value |> Expect.equal "total effects" 4

    // RED: currently only ex.Message is logged, not stack trace
    testCase "Effect throws: stack trace is logged not just message" <| fun _ ->
      let logged = ConcurrentBag<string>()
      let prevError = Log.logError
      Log.logError <- fun s -> logged.Add(s); prevError s
      try
        let prog : ElmProgram<int, int, int, int> = {
          Update = fun msg model -> model + 1, [1]
          Render = fun model -> [model]
          ExecuteEffect = fun _ _ ->
            async {
              let inner () = failwith "boom in effect"
              inner ()  // named frame so stack trace is non-trivial
            }
          OnModelChanged = fun _ _ -> ()
          OnSystemAlarm = fun _ _ -> ()
        }
        let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None
        rt.Dispatch 1
        waitFor (fun () -> logged |> Seq.exists (fun s -> s.Contains("boom in effect"))) 2000 |> ignore
        let entry = logged |> Seq.find (fun s -> s.Contains("boom in effect"))
        // Before fix: only "boom in effect" with no stack frames
        // After fix: stack trace lines like "  at SageFs..." appear in the log entry
        entry.Contains("at ") |> Expect.isTrue
          "error log entry must contain stack frames (found no 'at ' — stack trace not logged)"
      finally
        Log.logError <- prevError
  ]

// ---------------------------------------------------------------------------
// 4. OnSystemAlarm — exceptions in the Elm loop surface to the caller
// ---------------------------------------------------------------------------

[<Tests>]
let elmLoopAlarmTests =
  testList "ElmLoop.OnSystemAlarm" [

    testCase "Update throws: OnSystemAlarm is called with 'update' phase" <| fun _ ->
      let alarms = System.Collections.Generic.List<string * string>()
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun _msg _model -> failwith "update-alarm-test"; 0, []
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ -> async { () }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun phase msg -> alarms.Add(phase, msg)
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None
      rt.Dispatch 1
      waitFor (fun () -> alarms.Count > 0) 2000 |> ignore
      (alarms.Count, 0) |> Expect.isGreaterThan "alarm should fire"
      let (phase, msg) = alarms.[0]
      phase |> Expect.equal "phase should be 'update'" "update"
      msg |> Expect.stringContains "message should contain exception text" "update-alarm-test"

    testCase "Render throws: OnSystemAlarm is called with 'render' phase" <| fun _ ->
      let alarms = System.Collections.Generic.List<string * string>()
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + msg, []
        Render = fun _model -> failwith "render-alarm-test"; []
        ExecuteEffect = fun _ _ -> async { () }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun phase msg -> alarms.Add(phase, msg)
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None
      // Initial render fires immediately; wait for alarm
      waitFor (fun () -> alarms.Count > 0) 2000 |> ignore
      (alarms.Count, 0) |> Expect.isGreaterThan "alarm should fire on initial render"
      let (phase, _) = alarms.[0]
      phase |> Expect.equal "phase should be 'initial_render' or 'render'" "initial_render"

    testCase "OnModelChanged throws: OnSystemAlarm is called with 'callback' phase" <| fun _ ->
      let alarms = System.Collections.Generic.List<string * string>()
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + msg, []
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ -> async { () }
        OnModelChanged = fun _model _ -> failwith "callback-alarm-test"
        OnSystemAlarm = fun phase msg -> alarms.Add(phase, msg)
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None
      // Initial OnModelChanged fires immediately
      waitFor (fun () -> alarms.Count > 0) 2000 |> ignore
      (alarms.Count, 0) |> Expect.isGreaterThan "alarm should fire"
      let (phase, msg) = alarms.[0]
      phase |> Expect.equal "phase should be 'initial_callback' or 'callback'" "initial_callback"
      msg |> Expect.stringContains "message should contain exception text" "callback-alarm-test"

    testCase "Effect throws: OnSystemAlarm is called with 'effect' phase" <| fun _ ->
      let alarms = System.Collections.Generic.List<string * string>()
      let effSignal = new System.Threading.ManualResetEventSlim(false)
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model -> model + msg, [1]
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ ->
          async {
            effSignal.Set()
            failwith "effect-alarm-test"
          }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun phase msg -> alarms.Add(phase, msg)
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None
      rt.Dispatch 1
      effSignal.Wait(2000) |> ignore
      waitFor (fun () -> alarms |> Seq.exists (fun (p, _) -> p = "effect")) 2000 |> ignore
      let effectAlarms = alarms |> Seq.filter (fun (p, _) -> p = "effect") |> Seq.toList
      (effectAlarms.Length, 0) |> Expect.isGreaterThan "effect alarm should fire"
      let (_, msg) = effectAlarms.[0]
      msg |> Expect.stringContains "message should contain exception text" "effect-alarm-test"

    testCase "Loop resilience preserved: alarm fires AND loop continues after update throw" <| fun _ ->
      let alarms = System.Collections.Generic.List<string * string>()
      let effCount = ref 0
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun msg model ->
          if msg = 1 then failwith "alarm-resilience-test"
          model + 1, [1]
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ ->
          async { System.Threading.Interlocked.Increment effCount |> ignore }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun phase msg -> alarms.Add(phase, msg)
      }
      let rt = ElmLoop.start prog 0 System.Threading.CancellationToken.None
      rt.Dispatch 1  // throws → alarm
      waitFor (fun () -> alarms.Count > 0) 2000 |> ignore
      (alarms.Count, 0) |> Expect.isGreaterThan "alarm fired"
      // Loop must still be alive — dispatch 2 should succeed
      rt.Dispatch 2
      waitFor (fun () -> effCount.Value >= 1) 2000 |> ignore
      rt.GetModel() |> Expect.equal "model updated after alarm" 1
      (effCount.Value, 0) |> Expect.isGreaterThan "effect ran after alarm"
  ]
