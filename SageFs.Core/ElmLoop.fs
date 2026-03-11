namespace SageFs

/// ROLE: Core Elm Architecture types — the contract every frontend depends on.
///   Update is pure: (Msg → Model → Model * Effect list). No I/O inside Update.
///   EffectHandler runs side effects OUTSIDE the loop, dispatching results as Msgs.
/// Weight: Chesterton's fence — TUI, Dashboard, VSCode, Neovim, and Raylib all dispatch through this.
/// Assumes (2026-01): All UI goes through a single ElmProgram dispatch loop per frontend.
/// Invalidates-when: A frontend needs multiple independent dispatch loops (e.g., split-pane
///   with independent state), or when Update purity is no longer needed for replay/undo.
/// Danger: Adding side effects inside Update — breaks replay tests and time-travel debugging.
///   Adding mutable fields to ElmProgram — breaks concurrent dispatch safety.
type Update<'Model, 'Msg, 'Effect> =
  'Msg -> 'Model -> 'Model * 'Effect list

type Render<'Model, 'Region> =
  'Model -> 'Region list

type EffectHandler<'Msg, 'Effect> =
  ('Msg -> unit) -> 'Effect -> Async<unit>

/// An Elm Architecture program definition
type ElmProgram<'Model, 'Msg, 'Effect, 'Region> = {
  Update: Update<'Model, 'Msg, 'Effect>
  Render: Render<'Model, 'Region>
  ExecuteEffect: EffectHandler<'Msg, 'Effect>
  OnModelChanged: 'Model -> 'Region list -> unit
  /// Called whenever an exception is caught inside the Elm loop.
  /// phase: "update" | "render" | "callback" | "effect" | "initial_render" | "initial_callback"
  /// message: exception message
  OnSystemAlarm: string -> string -> unit
}

/// The running Elm loop — dispatch messages and read current state.
type ElmRuntime<'Model, 'Msg, 'Region> = {
  Dispatch: 'Msg -> unit
  GetModel: unit -> 'Model
  GetRegions: unit -> 'Region list
}

module ElmLoop =
  open System.Diagnostics
  open System.Collections.Concurrent
  open System.Threading
  open SageFs.Utils

  let kvp k v = System.Collections.Generic.KeyValuePair(k, v :> obj)

  let msgLabelCache =
    ConcurrentDictionary<struct (System.Type * int * System.Type * int), string>()

  /// Label a DU value for diagnostics. Unwraps one level for nested DUs:
  /// SageFsMsg.Event(SageFsEvent.EvalCompleted _) → "Event.EvalCompleted"
  /// SageFsMsg.CycleTheme → "CycleTheme"
  /// Cached by (Type, outerTag, innerTag) to avoid repeated reflection.
  let msgLabel (msg: obj) : string =
    let t = msg.GetType()
    match Microsoft.FSharp.Reflection.FSharpType.IsUnion(t) with
    | true ->
      let case, fields = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(msg, t)
      match fields.Length = 1 && Microsoft.FSharp.Reflection.FSharpType.IsUnion(fields.[0].GetType()) with
      | true ->
        let inner, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(fields.[0], fields.[0].GetType())
        let key = struct (t, case.Tag, fields.[0].GetType(), inner.Tag)
        msgLabelCache.GetOrAdd(key, fun _ -> sprintf "%s.%s" case.Name inner.Name)
      | false ->
        let key = struct (t, case.Tag, typeof<unit>, 0)
        msgLabelCache.GetOrAdd(key, fun _ -> case.Name)
    | false -> t.Name

  /// Start the Elm loop with an initial model.
  /// Uses a dedicated drain thread (not thread pool) to avoid starvation.
  /// Dispatch enqueues + signals; the drain thread wakes, processes all
  /// pending messages, renders ONCE, then sleeps until signalled again.
  /// Pass a CancellationToken to stop effects and the drain thread on shutdown.
  let start (program: ElmProgram<'Model, 'Msg, 'Effect, 'Region>)
            (initialModel: 'Model)
            (ct: System.Threading.CancellationToken) : ElmRuntime<'Model, 'Msg, 'Region> =
    let mutable model = initialModel
    let mutable latestRegions = []
    let lockObj = obj ()
    let queue = ConcurrentQueue<'Msg>()
    // Signal for the dedicated drain thread — Set() wakes it, Wait() sleeps it
    let signal = new ManualResetEventSlim(false)
    // Bounds concurrent in-flight effects. Prevents exponential effect-cascade OOM:
    // each effect that dispatches messages can produce more effects; without a cap,
    // 1 file change → 10 effects → 100 msgs → 1000 effects → ...
    let effectSemaphore = new System.Threading.SemaphoreSlim(64, 64)

    /// Drain all queued messages, render once, push once.
    /// Runs exclusively on the dedicated drain thread.
    let drain () =
      let batchSw = Stopwatch.StartNew()
      let batchTag = kvp "msg_type" "batch"

      // Phase 1: Drain queue — apply all updates under model lock
      let lockSw = Stopwatch.StartNew()
      let prevModel, snapshot, allEffects, batchCount, updateMs, msgTypes =
        lock lockObj (fun () ->
          lockSw.Stop()
          let lockWaitMs = lockSw.Elapsed.TotalMilliseconds
          match lockWaitMs > 1.0 with
          | true -> Instrumentation.elmloopLockWaitMs.Record(lockWaitMs, batchTag)
          | false -> ()
          let updateSw = Stopwatch.StartNew()
          let prev = model
          // ResizeArray avoids the O(n²) `msgEffs @ effs` left-fold: with 200 msgs × 10 effects
          // each, list concat allocates quadratically. AddRange is O(k) per message.
          let effsAcc = ResizeArray<_>()
          let mutable count = 0
          let mutable queueAlarmFired = false   // fire at most once per drain batch
          let mutable item = Unchecked.defaultof<'Msg>
          let msgCounts = System.Collections.Generic.Dictionary<string, int>()
          while queue.TryDequeue(&item) do
            count <- count + 1
            Instrumentation.elmDispatchCount.Add(1L)
            let typeName = msgLabel (item :> obj)
            match msgCounts.TryGetValue(typeName) with
            | true, c -> msgCounts.[typeName] <- c + 1
            | false, _ -> msgCounts.[typeName] <- 1
            let perMsgSw = Stopwatch.StartNew()
            try
              let m, msgEffs = program.Update item model
              model <- m
              for eff in msgEffs do effsAcc.Add(eff)
            with ex ->
              Instrumentation.elmloopErrors.Add(1L, kvp "phase" "update")
              Log.error "[ElmLoop] Update threw for %s: %s\n%s" typeName ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
              try program.OnSystemAlarm "update" ex.Message with _ -> ()
            perMsgSw.Stop()
            Instrumentation.elmloopUpdateMs.Record(perMsgSw.Elapsed.TotalMilliseconds, kvp "msg_type" typeName)
            // Queue depth high-watermark check (fires once per drain batch to avoid log spam).
            // queue.Count reflects messages that arrived while we processed this message —
            // which is exactly the effect-cascade scenario we want to surface.
            match queueAlarmFired with
            | true -> ()
            | false ->
              let qDepth = queue.Count
              match qDepth > 256 with
              | false -> ()
              | true ->
                queueAlarmFired <- true
                Instrumentation.elmloopErrors.Add(1L, kvp "phase" "queue_depth")
                Log.warn "[ElmLoop] QUEUE HIGH-WATERMARK: depth=%d (>256). Possible effect-cascade storm — check for runaway effect→msg→effect loops." qDepth
                try program.OnSystemAlarm "queue_depth" (sprintf "queue depth %d exceeded high-watermark 256" qDepth) with _ -> ()
          updateSw.Stop()
          prev, model, Seq.toList effsAcc, count, updateSw.Elapsed.TotalMilliseconds,
          msgCounts |> Seq.map (fun kv -> sprintf "%s×%d" kv.Key kv.Value) |> String.concat ",")

      match batchCount with
      | 0 -> ()
      | _ ->

      let modelChanged = not (obj.ReferenceEquals(prevModel, snapshot))

      let activity = Instrumentation.elmloopSource.StartActivity("elm.batch")
      match isNull activity with
      | false ->
        activity.SetTag("elm.batch_size", batchCount) |> ignore
        activity.SetTag("elm.model_changed", modelChanged) |> ignore
        activity.SetTag("elm.update_ms", updateMs) |> ignore
        activity.SetTag("elm.msg_types", msgTypes) |> ignore
      | true -> ()

      // Phase 2: Render once for entire batch (outside model lock)
      let renderSw = Stopwatch.StartNew()
      let regions =
        match modelChanged with
        | true ->
          try program.Render snapshot
          with ex ->
            Instrumentation.elmloopErrors.Add(1L, kvp "phase" "render")
            Log.error "[ElmLoop] Render threw: %s\n%s" ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
            try program.OnSystemAlarm "render" ex.Message with _ -> ()
            lock lockObj (fun () -> latestRegions)
        | false ->
          lock lockObj (fun () -> latestRegions)
      renderSw.Stop()
      Instrumentation.elmloopRenderMs.Record(renderSw.Elapsed.TotalMilliseconds, batchTag)

      lock lockObj (fun () -> latestRegions <- regions)

      // Phase 3: Callback (SSE push) once for entire batch
      let cbSw = Stopwatch.StartNew()
      match modelChanged with
      | true ->
        try program.OnModelChanged snapshot regions
        with ex ->
          Instrumentation.elmloopErrors.Add(1L, kvp "phase" "callback")
          Log.error "[ElmLoop] OnModelChanged threw: %s\n%s" ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
          try program.OnSystemAlarm "callback" ex.Message with _ -> ()
      | false -> ()
      cbSw.Stop()
      Instrumentation.elmloopCallbackMs.Record(cbSw.Elapsed.TotalMilliseconds, batchTag)

      // Phase 4: Spawn effects with parent trace context from batch span
      match allEffects.IsEmpty with
      | false -> Instrumentation.elmloopEffectsSpawned.Add(int64 allEffects.Length)
      | true -> ()
      let parentCtx =
        match isNull activity with
        | false -> activity.Context
        | true -> System.Diagnostics.ActivityContext()
      let hasParent =
        parentCtx.TraceId.ToString() <> "00000000000000000000000000000000"
      for effect in allEffects do
        Async.Start (async {
          // Outer catch: OperationCanceledException from WaitAsync when CT fires.
          // If WaitAsync throws, semaphore was never acquired — no Release needed.
          try
            do! effectSemaphore.WaitAsync(ct) |> Async.AwaitTask
            // Inner try/finally: semaphore is now acquired; Release on every exit path.
            try
              let effectActivity =
                match hasParent with
                | true ->
                  Instrumentation.elmloopSource.StartActivity(
                    "elm.effect",
                    ActivityKind.Internal,
                    parentCtx)
                | false -> null
              try
                do! program.ExecuteEffect (fun msg -> queue.Enqueue msg; signal.Set()) effect
                Instrumentation.succeedSpan effectActivity
              with ex ->
                Instrumentation.elmloopErrors.Add(1L, kvp "phase" "effect")
                Log.error "[ElmLoop] Effect threw: %s\n%s" ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
                try program.OnSystemAlarm "effect" ex.Message with _ -> ()
                Instrumentation.failSpan effectActivity ex.Message
            finally
              effectSemaphore.Release() |> ignore
          with :? System.OperationCanceledException -> ()
        }, ct)

      batchSw.Stop()
      let totalMs = batchSw.Elapsed.TotalMilliseconds
      Instrumentation.elmloopTotalDispatchMs.Record(totalMs, batchTag)

      match isNull activity with
      | false ->
        activity.SetTag("elm.update_ms", updateMs) |> ignore
        activity.SetTag("elm.render_ms", renderSw.Elapsed.TotalMilliseconds) |> ignore
        activity.SetTag("elm.callback_ms", cbSw.Elapsed.TotalMilliseconds) |> ignore
        activity.SetTag("elm.total_ms", totalMs) |> ignore
        activity.SetTag("elm.effects_count", allEffects.Length) |> ignore
        activity.SetTag("elm.msg_types", msgTypes) |> ignore
        activity.Stop()
        activity.Dispose()
      | true -> ()

      match totalMs > 50.0 with
      | true ->
        Log.warn "[ElmLoop] SLOW batch (%d msgs): %.1fms (update=%.1fms render=%.1fms cb=%.1fms changed=%b) msgs=[%s]"
          batchCount totalMs updateMs renderSw.Elapsed.TotalMilliseconds cbSw.Elapsed.TotalMilliseconds modelChanged msgTypes
      | false -> ()

    // Dedicated drain thread — runs outside the thread pool so it's never
    // starved by Kestrel/SSE/effect work saturating the pool.
    // Stops cleanly when the CancellationToken is cancelled.
    // Disposes the signal after exit so handles are not leaked in tests.
    let drainThread = Thread(fun () ->
      try
        while not ct.IsCancellationRequested do
          signal.Wait(ct)
          signal.Reset()
          // Drain until queue is truly empty (messages may arrive during processing)
          while not queue.IsEmpty && not ct.IsCancellationRequested do
            drain ()
      with :? System.OperationCanceledException -> ()
      signal.Dispose())
    drainThread.IsBackground <- true
    drainThread.Name <- "ElmLoop-Drain"
    drainThread.Start()

    let dispatch (msg: 'Msg) =
      queue.Enqueue msg
      // Guard against signal.Set() after disposal when CT is already cancelled
      if not ct.IsCancellationRequested then
        try signal.Set() with :? System.ObjectDisposedException -> ()

    let regions =
      try program.Render initialModel
      with ex ->
        Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "initial_render" :> obj))
        Log.error "[ElmLoop] Initial Render threw: %s\n%s" ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
        try program.OnSystemAlarm "initial_render" ex.Message with _ -> ()
        []
    latestRegions <- regions
    try program.OnModelChanged initialModel regions
    with ex ->
      Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "initial_callback" :> obj))
      Log.error "[ElmLoop] Initial OnModelChanged threw: %s\n%s" ex.Message (if isNull ex.StackTrace then "" else ex.StackTrace)
      try program.OnSystemAlarm "initial_callback" ex.Message with _ -> ()

    { Dispatch = dispatch
      GetModel = fun () -> lock lockObj (fun () -> model)
      GetRegions = fun () -> lock lockObj (fun () -> latestRegions) }
