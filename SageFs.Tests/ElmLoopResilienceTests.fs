module SageFs.Tests.ElmLoopResilienceTests

open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Utils
open System.Collections.Concurrent
open System.Collections.Generic

/// Await a condition with a hard ceiling, without sleep-polling.
/// The ElmLoop drain runs on a dedicated thread, so the async yield never
/// starves the loop; returns true only when the condition was satisfied before
/// the ceiling elapsed.
let waitForAsync (condition: unit -> bool) (timeoutMs: int) =
  task {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable ok = false
    while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
      if condition () then ok <- true
      else do! Task.Delay 10
    return ok
  }

/// Fixed bounded settle window for negative assertions (nothing-more-happens
/// checks). A short delay is the only way to assert the absence of an event.
let settleAsync () =
  Task.Delay 50

type CoalescingMsg =
  | Gate
  | Tick of int
  | Batch of int list

module CoalescingMsg =
  let private tryReplaceLast
    (pending: ResizeArray<CoalescingMsg>)
    (chooseReplacement: CoalescingMsg -> CoalescingMsg option)
    =
    let rec loop index =
      match index < 0 with
      | true -> false
      | false ->
        match chooseReplacement pending[index] with
        | Some replacement ->
          pending[index] <- replacement
          true
        | None ->
          loop (index - 1)
    loop (pending.Count - 1)

  let tryAbsorbPending
    (pending: ResizeArray<CoalescingMsg>)
    (incoming: CoalescingMsg)
    =
    match incoming with
    | Tick value ->
      tryReplaceLast
        pending
        (function
        | Tick _ -> Some (Tick value)
        | _ -> None)
    | Batch values ->
      tryReplaceLast
        pending
        (function
        | Batch existing -> Some (Batch (existing @ values))
        | _ -> None)
    | Gate ->
      false

[<Tests>]
let elmLoopResilienceTests =
  // Decision: Elm loop catches exceptions in Update/Render/OnModelChanged/Effect.
  //   A single bad message must not kill the dispatch loop — all frontends depend on it.
  // Assumes (2026-01): Every frontend shares one ElmProgram per instance.
  // See ElmLoop.fs header for the full contract.
  testList "ElmLoop resilience" [

    testTask "Update throws: model stays, no effect, loop recovers" {
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
      let! fired = waitForAsync (fun () -> effCount.Value >= 1) 2000
      fired |> Expect.isTrue "effect should fire on d1"
      effCount.Value |> Expect.equal "effect fired on d1" 1
      rt.GetModel() |> Expect.equal "model is 1" 1

      rt.Dispatch 2  // Update throws, model stays 1, no effect
      do! settleAsync ()
      effCount.Value |> Expect.equal "no new effect on d2" 1
      rt.GetModel() |> Expect.equal "model still 1" 1

      rt.Dispatch 3  // recovers, model=2
      let! fired3 = waitForAsync (fun () -> effCount.Value >= 2) 2000
      fired3 |> Expect.isTrue "effect should fire on d3 — loop survived throw (see ElmLoop.fs header)"
      effCount.Value |> Expect.equal "effect fires on d3 — loop survived throw (see ElmLoop.fs header)" 2
      rt.GetModel() |> Expect.equal "model is 2" 2
    }

    testTask "Render throws: previous regions preserved, effects fire" {
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
      let! fired1 = waitForAsync (fun () -> effCount.Value >= 1) 2000
      fired1 |> Expect.isTrue "effect should fire on d1"
      rt.GetRegions() |> Expect.equal "regions from d1" [10]

      rt.Dispatch 2  // model=2, Render throws, regions stay [10]
      let! fired2 = waitForAsync (fun () -> effCount.Value >= 2) 2000
      fired2 |> Expect.isTrue "effect should still fire on d2"
      effCount.Value |> Expect.equal "effect still fires" 2
      rt.GetRegions() |> Expect.equal "regions preserved" [10]

      rt.Dispatch 3  // model=3, Render succeeds with [30]
      let! fired3 = waitForAsync (fun () -> effCount.Value >= 3) 2000
      fired3 |> Expect.isTrue "effect should fire on d3"
      rt.GetRegions() |> Expect.equal "regions recover" [30]
    }

    testTask "OnModelChanged throws: effects still fire" {
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
      let! fired1 = waitForAsync (fun () -> effCount.Value >= 1) 2000
      fired1 |> Expect.isTrue "effect on d1"
      effCount.Value |> Expect.equal "effect on d1" 1

      rt.Dispatch 2  // OnModelChanged throws, but effect should still fire
      let! fired2 = waitForAsync (fun () -> effCount.Value >= 2) 2000
      fired2 |> Expect.isTrue "effect on d2 despite throw — resilience contract (see ElmLoop.fs header)"
      effCount.Value |> Expect.equal "effect on d2 despite throw — resilience contract (see ElmLoop.fs header)" 2
      rt.GetModel() |> Expect.equal "model updated" 2

      rt.Dispatch 3  // recovers
      let! fired3 = waitForAsync (fun () -> effCount.Value >= 3) 2000
      fired3 |> Expect.isTrue "effect on d3"
      effCount.Value |> Expect.equal "effect on d3" 3
    }

    testTask "Effect throws: loop still works for subsequent dispatches" {
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
      let! fired1 = waitForAsync (fun () -> effCount.Value >= 1) 2000
      fired1 |> Expect.isTrue "effect 1 should run"
      effCount.Value |> Expect.equal "effect 1 ran" 1

      rt.Dispatch 2  // effect=2, throws
      do! settleAsync ()
      effCount.Value |> Expect.equal "effect 2 failed" 1

      rt.Dispatch 3  // effect=3, succeeds
      let! fired3 = waitForAsync (fun () -> effCount.Value >= 2) 2000
      fired3 |> Expect.isTrue "effect 3 should run — loop survived bad effect (see ElmLoop.fs header)"
      effCount.Value |> Expect.equal "effect 3 ran — loop survived bad effect (see ElmLoop.fs header)" 2
    }

    testTask "Initial Render throws: starts with empty regions" {
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
      do! settleAsync ()
      rt.GetRegions() |> Expect.equal "regions recover" [10]
    }

    testTask "Initial OnModelChanged throws: loop still works" {
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
      let! fired = waitForAsync (fun () -> effCount.Value >= 1) 2000
      fired |> Expect.isTrue "effect fires"
      effCount.Value |> Expect.equal "effect fires" 1
      rt.GetModel() |> Expect.equal "model updated" 1
    }

    testTask "Multiple failures in sequence: loop survives all" {
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
      let! fired1 = waitForAsync (fun () -> effCount.Value >= 1) 2000
      fired1 |> Expect.isTrue "d1 effect should fire"
      rt.GetModel() |> Expect.equal "d1 model" 1
      effCount.Value |> Expect.equal "d1 effects" 1

      rt.Dispatch 2  // model=2, Render throws, regions preserved
      let! fired2 = waitForAsync (fun () -> effCount.Value >= 2) 2000
      fired2 |> Expect.isTrue "d2 effect should fire"
      rt.GetModel() |> Expect.equal "d2 model" 2
      rt.GetRegions() |> Expect.equal "d2 regions preserved" [1]

      rt.Dispatch 3  // Update throws, model stays 2
      do! settleAsync ()
      rt.GetModel() |> Expect.equal "d3 model unchanged" 2

      rt.Dispatch 4  // model=3, OnModelChanged doesn't throw (model=3, not 4)
      let! fired4 = waitForAsync (fun () -> effCount.Value >= 3) 2000
      fired4 |> Expect.isTrue "d4 effect should fire"
      rt.GetModel() |> Expect.equal "d4 model" 3

      rt.Dispatch 5  // model=4, OnModelChanged throws; effect=5 throws
      do! settleAsync ()
      rt.GetModel() |> Expect.equal "d5 model" 4

      rt.Dispatch 6  // model=5, all good
      let! fired6 = waitForAsync (fun () -> effCount.Value >= 4) 2000
      fired6 |> Expect.isTrue "d6 effect should fire"
      rt.GetModel() |> Expect.equal "d6 model" 5
      // effects: d1(1)+d2(2)+d4(4)+d5(5 fails)+d6(6) = 4 successes
      effCount.Value |> Expect.equal "total effects" 4
    }

    // RED: currently only ex.Message is logged, not stack trace
    testTask "Effect throws: stack trace is logged not just message" {
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
        let! loggedIt = waitForAsync (fun () -> logged |> Seq.exists (fun s -> s.Contains("boom in effect"))) 2000
        loggedIt |> Expect.isTrue "error should be logged"
        let entry = logged |> Seq.find (fun s -> s.Contains("boom in effect"))
        // Before fix: only "boom in effect" with no stack frames
        // After fix: stack trace lines like "  at SageFs..." appear in the log entry
        entry.Contains("at ") |> Expect.isTrue
          "error log entry must contain stack frames (found no 'at ' — stack trace not logged)"
      finally
        Log.logError <- prevError
    }
  ]

// ---------------------------------------------------------------------------
// 4. OnSystemAlarm — exceptions in the Elm loop surface to the caller
// ---------------------------------------------------------------------------

[<Tests>]
let elmLoopAlarmTests =
  testList "ElmLoop.OnSystemAlarm" [

    testTask "Update throws: OnSystemAlarm is called with 'update' phase" {
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
      let! fired = waitForAsync (fun () -> alarms.Count > 0) 2000
      fired |> Expect.isTrue "alarm should fire"
      (alarms.Count, 0) |> Expect.isGreaterThan "alarm should fire"
      let (phase, msg) = alarms.[0]
      phase |> Expect.equal "phase should be 'update'" "update"
      msg |> Expect.stringContains "message should contain exception text" "update-alarm-test"
    }

    testTask "Render throws: OnSystemAlarm is called with 'render' phase" {
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
      let! fired = waitForAsync (fun () -> alarms.Count > 0) 2000
      fired |> Expect.isTrue "alarm should fire on initial render"
      (alarms.Count, 0) |> Expect.isGreaterThan "alarm should fire on initial render"
      let (phase, _) = alarms.[0]
      phase |> Expect.equal "phase should be 'initial_render' or 'render'" "initial_render"
    }

    testTask "OnModelChanged throws: OnSystemAlarm is called with 'callback' phase" {
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
      let! fired = waitForAsync (fun () -> alarms.Count > 0) 2000
      fired |> Expect.isTrue "alarm should fire"
      (alarms.Count, 0) |> Expect.isGreaterThan "alarm should fire"
      let (phase, msg) = alarms.[0]
      phase |> Expect.equal "phase should be 'initial_callback' or 'callback'" "initial_callback"
      msg |> Expect.stringContains "message should contain exception text" "callback-alarm-test"
    }

    testTask "Effect throws: OnSystemAlarm is called with 'effect' phase" {
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
      let! fired = waitForAsync (fun () -> alarms |> Seq.exists (fun (p, _) -> p = "effect")) 2000
      fired |> Expect.isTrue "effect alarm should fire"
      let effectAlarms = alarms |> Seq.filter (fun (p, _) -> p = "effect") |> Seq.toList
      (effectAlarms.Length, 0) |> Expect.isGreaterThan "effect alarm should fire"
      let (_, msg) = effectAlarms.[0]
      msg |> Expect.stringContains "message should contain exception text" "effect-alarm-test"
    }

    testTask "Loop resilience preserved: alarm fires AND loop continues after update throw" {
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
      let! fired = waitForAsync (fun () -> alarms.Count > 0) 2000
      fired |> Expect.isTrue "alarm fired"
      (alarms.Count, 0) |> Expect.isGreaterThan "alarm fired"
      // Loop must still be alive — dispatch 2 should succeed
      rt.Dispatch 2
      let! ran = waitForAsync (fun () -> effCount.Value >= 1) 2000
      ran |> Expect.isTrue "effect should run after alarm"
      rt.GetModel() |> Expect.equal "model updated after alarm" 1
      (effCount.Value, 0) |> Expect.isGreaterThan "effect ran after alarm"
    }
  ]

// ---------------------------------------------------------------------------
// 5. Backpressure — queue depth alarm and bounded effect concurrency
// ---------------------------------------------------------------------------

[<Tests>]
let elmLoopBackpressureTests =
  // Decision: ElmLoop must not silently OOM under effect-cascade storms.
  //   Two defences: (1) queue-depth high-watermark alarm via OnSystemAlarm,
  //   (2) SemaphoreSlim cap on concurrent in-flight effects.
  // Assumes (2026-01): 256 is the queue-depth high-watermark; 64 is max concurrent effects.
  // Danger: Raising these caps without benchmarking first.
  testList "ElmLoop backpressure" [

    testTask "queue depth high-watermark alarm fires when >256 msgs pile up" {
      // Strategy: block the drain thread inside Update while it holds the model lock,
      // enqueue 300 more messages (ConcurrentQueue.Enqueue needs no lock), then release.
      // After Update returns, the drain checks queue.Count inside the while-TryDequeue loop
      // and fires OnSystemAlarm "queue_depth" once the threshold is exceeded.
      let alarms = ConcurrentBag<string * string>()
      let drainStarted = new System.Threading.SemaphoreSlim(0, 1)
      let releaseGate = new System.Threading.ManualResetEventSlim(false)
      let processed = ConcurrentBag<unit>()
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun _msg model ->
          match model with
          | 0 ->
            drainStarted.Release() |> ignore    // signal: drain is now inside Update
            releaseGate.Wait()                  // block drain (model lock held) while test fills queue
          | _ -> ()
          processed.Add(())
          model + 1, []
        Render = fun m -> [m]
        ExecuteEffect = fun _ _ -> async { () }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun phase msg -> alarms.Add(phase, msg)
      }
      let cts = new System.Threading.CancellationTokenSource()
      let rt = ElmLoop.start prog 0 cts.Token

      rt.Dispatch 1                              // wake drain; model=0 hits gate
      drainStarted.Wait(2000) |> ignore          // drain is now inside Update holding lock
      for _ in 1..300 do rt.Dispatch 1          // 300 msgs pile into ConcurrentQueue
      do! Task.Delay 50                          // let all enqueues settle
      releaseGate.Set()                          // unblock drain

      let! drained = waitForAsync (fun () -> processed.Count >= 301) 15000
      drained |> Expect.isTrue "drain should process all queued messages"

      alarms |> Seq.exists (fun (p, _) -> p = "queue_depth")
      |> Expect.isTrue
           "queue_depth alarm must fire when >256 msgs are pending (see ElmLoop.fs — high-watermark check inside while-TryDequeue loop)"

      cts.Cancel()
    }

    testTask "effect concurrency bounded: at most 64 effects run simultaneously" {
      // 20 msgs × 5 effects = 100 potential concurrent in-flight effects.
      // Without a SemaphoreSlim cap, all 100 Async.Start immediately; Async.Sleep(50) keeps
      // them all in-flight simultaneously so maxConcurrent ≈ 100.
      // With SemaphoreSlim(64), at most 64 can hold the semaphore at once → maxConcurrent ≤ 64.
      let currentConcurrent = ref 0
      let maxConcurrent = ref 0
      let effectsCompleted = ref 0
      let counterLock = obj ()
      let prog : ElmProgram<int, int, int, int> = {
        Update = fun _msg model -> model + 1, List.replicate 5 1
        Render = fun m -> [m]
        ExecuteEffect = fun _ _ ->
          async {
            lock counterLock (fun () ->
              currentConcurrent := !currentConcurrent + 1
              match !currentConcurrent > !maxConcurrent with
              | true -> maxConcurrent := !currentConcurrent
              | false -> ())
            do! Async.Sleep 50
            lock counterLock (fun () ->
              currentConcurrent := !currentConcurrent - 1
              effectsCompleted := !effectsCompleted + 1)
          }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun _ _ -> ()
      }
      let cts = new System.Threading.CancellationTokenSource()
      let rt = ElmLoop.start prog 0 cts.Token

      for _ in 1..20 do rt.Dispatch 1           // 100 potential concurrent effects
      let! allDone = waitForAsync (fun () -> !effectsCompleted >= 100) 15000
      allDone |> Expect.isTrue "all 100 effects should complete"

      (!maxConcurrent <= 64)
      |> Expect.isTrue
           "max concurrent effects must be ≤64 — unbounded Async.Start allows all 100 to run at once (see ElmLoop.fs SemaphoreSlim cap)"

      cts.Cancel()
    }
  ]

[<Tests>]
let elmLoopCoalescingTests =
  testList "ElmLoop coalescing" [

    testTask "latest wins coalescing replaces stale pending ticks before the drain resumes" {
      let drainStarted = new System.Threading.SemaphoreSlim(0, 1)
      let releaseGate = new System.Threading.ManualResetEventSlim(false)
      let processed = ResizeArray<CoalescingMsg>()
      let processedLock = obj ()
      let prog : ElmProgram<int, CoalescingMsg, unit, int> = {
        Update = fun msg model ->
          lock processedLock (fun () -> processed.Add msg)
          match msg with
          | Gate ->
            drainStarted.Release() |> ignore
            releaseGate.Wait()
          | _ -> ()
          model + 1, []
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ -> async { () }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun _ _ -> ()
      }
      let cts = new System.Threading.CancellationTokenSource()
      let rt = ElmLoop.startWithCoalescer CoalescingMsg.tryAbsorbPending prog 0 cts.Token

      rt.Dispatch Gate
      drainStarted.Wait(2000)
      |> Expect.isTrue "gate message should block the drain so pending work can accumulate"
      rt.Dispatch (Tick 1)
      rt.Dispatch (Tick 2)
      rt.Dispatch (Tick 3)
      releaseGate.Set()

      let! processed2 = waitForAsync (fun () -> lock processedLock (fun () -> processed.Count >= 2)) 5000
      processed2 |> Expect.isTrue "gate and the final coalesced tick should both be processed"

      let seen =
        lock processedLock (fun () -> processed |> Seq.toList)
      seen
      |> Expect.equal "stale ticks should collapse to the latest pending tick, not replay every intermediate pulse"
           [ Gate; Tick 3 ]

      cts.Cancel()
    }

    testTask "merge coalescing preserves every payload item while collapsing multiple pending batches" {
      let drainStarted = new System.Threading.SemaphoreSlim(0, 1)
      let releaseGate = new System.Threading.ManualResetEventSlim(false)
      let processed = ResizeArray<CoalescingMsg>()
      let processedLock = obj ()
      let prog : ElmProgram<int, CoalescingMsg, unit, int> = {
        Update = fun msg model ->
          lock processedLock (fun () -> processed.Add msg)
          match msg with
          | Gate ->
            drainStarted.Release() |> ignore
            releaseGate.Wait()
          | _ -> ()
          model + 1, []
        Render = fun model -> [model]
        ExecuteEffect = fun _ _ -> async { () }
        OnModelChanged = fun _ _ -> ()
        OnSystemAlarm = fun _ _ -> ()
      }
      let cts = new System.Threading.CancellationTokenSource()
      let rt = ElmLoop.startWithCoalescer CoalescingMsg.tryAbsorbPending prog 0 cts.Token

      rt.Dispatch Gate
      drainStarted.Wait(2000)
      |> Expect.isTrue "gate message should block the drain so pending batches can merge"
      rt.Dispatch (Batch [ 1 ])
      rt.Dispatch (Batch [ 2; 3 ])
      rt.Dispatch (Batch [ 4 ])
      releaseGate.Set()

      let! processed2 = waitForAsync (fun () -> lock processedLock (fun () -> processed.Count >= 2)) 5000
      processed2 |> Expect.isTrue "gate and the merged batch should both be processed"

      let seen =
        lock processedLock (fun () -> processed |> Seq.toList)
      seen
      |> Expect.equal "batch coalescing should preserve every payload item while collapsing redundant renders"
           [ Gate; Batch [ 1; 2; 3; 4 ] ]

      cts.Cancel()
    }
  ]
