module SageFs.Tests.WarmupSupervisionTests

/// Coverage for the four onboarding-trial defects (fcs-trial-a/b/c,
/// 2026-09-22) at the pure decision layer WarmupSupervision.fs extracts them
/// to:
///   1. "Warmup on a big repo is unbounded and silent" -> BoundedReach.
///   2. "stop_session hangs" -> StopAlwaysWins.
///   3. "Session state disagrees with itself" -> NoResurrection (once
///      terminal, the state can't be read differently by different callers
///      depending on timing).
///   4. The build-timing trap is a docs/message fix, not a state-machine
///      one — not covered here.
///
/// "twin" tests at the bottom of each invariant group deliberately break
/// the real function (a local, mutated copy) and assert the property FAILS
/// against it — proof the property is actually discriminating, not
/// vacuously true.
open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WarmupSupervision
open SageFs.Tests.SharedGenerators

let private bounds : Bounds =
  { Absolute = TimeSpan.FromMinutes 10.0
    Inactivity = TimeSpan.FromSeconds 30.0 }

let private exampleTests = testList "decidePoll examples" [
  test "Ready observation marks ready regardless of elapsed, within bound" {
    decidePoll bounds (TimeSpan.FromSeconds 5.0) TimeSpan.Zero (PollObservation.Ready "loaded")
    |> Expect.equal "MarkReady" (PollDecision.MarkReady "loaded")
  }

  test "Faulted observation with a reason marks faulted with that reason" {
    decidePoll bounds (TimeSpan.FromSeconds 5.0) TimeSpan.Zero (PollObservation.Faulted (Some "boom"))
    |> Expect.equal "MarkFaulted boom" (PollDecision.MarkFaulted "boom")
  }

  test "Faulted observation with no reason gets the shared default reason" {
    decidePoll bounds (TimeSpan.FromSeconds 5.0) TimeSpan.Zero (PollObservation.Faulted None)
    |> Expect.equal "MarkFaulted default" (PollDecision.MarkFaulted defaultFaultReason)
  }

  test "StillWarming within both bounds keeps polling" {
    decidePoll bounds (TimeSpan.FromMinutes 1.0) (TimeSpan.FromSeconds 5.0) PollObservation.StillWarming
    |> Expect.equal "KeepPolling" PollDecision.KeepPolling
  }

  test "StillWarming past the inactivity bound (but under the absolute bound) times out" {
    match decidePoll bounds (TimeSpan.FromMinutes 1.0) (TimeSpan.FromSeconds 31.0) PollObservation.StillWarming with
    | PollDecision.TimedOut reason -> reason |> Expect.stringContains "mentions no progress" "no progress"
    | other -> failtestf "expected TimedOut, got %A" other
  }

  test "Progressed past the inactivity bound still keeps polling — progress IS the reset" {
    // sinceLastActivity here models what the CALLER would already have reset
    // to near-zero on seeing Progressed; decidePoll itself always treats
    // Progressed as forward motion regardless of the elapsed/inactivity
    // clocks it's handed, exactly like Ready/Faulted.
    decidePoll bounds (TimeSpan.FromMinutes 1.0) (TimeSpan.FromSeconds 31.0) PollObservation.Progressed
    |> Expect.equal "KeepPolling" PollDecision.KeepPolling
  }

  test "ProbeFailed behaves exactly like StillWarming for the inactivity bound" {
    match decidePoll bounds (TimeSpan.FromMinutes 1.0) (TimeSpan.FromSeconds 31.0) (PollObservation.ProbeFailed "connection refused") with
    | PollDecision.TimedOut _ -> ()
    | other -> failtestf "expected TimedOut, got %A" other
  }

  test "elapsed past the absolute bound times out even with a fresh Progressed-reset inactivity clock" {
    match decidePoll bounds (TimeSpan.FromMinutes 11.0) TimeSpan.Zero PollObservation.StillWarming with
    | PollDecision.TimedOut reason -> reason |> Expect.stringContains "mentions absolute limit" "absolute limit"
    | other -> failtestf "expected TimedOut, got %A" other
  }

  test "absolute bound wins even over a Ready observation — no argument, no exceptions" {
    match decidePoll bounds (TimeSpan.FromMinutes 11.0) TimeSpan.Zero (PollObservation.Ready "loaded") with
    | PollDecision.TimedOut _ -> ()
    | other -> failtestf "expected TimedOut (absolute bound is absolute), got %A" other
  }
]

// ---------------------------------------------------------------------
// Property 3 (BoundedReach): a Starting session driven purely by
// ClockAdvance + StillWarming ticks ALWAYS reaches a terminal state
// (Faulted, since nothing ever says Ready) within Bounds.Inactivity of
// starting — never stays Starting forever. This is defect #1's fix,
// proven as a property rather than an example.
// ---------------------------------------------------------------------
let private boundedReachTests = testList "BoundedReach (defect #1: unbounded silent warmup)" [
  testPropertyWithConfig propConfig "silence beyond the inactivity bound always reaches Faulted, never lingers in Starting" <|
    fun (PositiveInt extraSeconds) ->
      let b = { Absolute = TimeSpan.FromMinutes 10.0; Inactivity = TimeSpan.FromSeconds 30.0 }
      let silentFor = b.Inactivity + TimeSpan.FromSeconds(float extraSeconds)
      let model =
        run<unit> b [
          LifecycleEvent.ClockAdvance silentFor
          LifecycleEvent.PollTick PollObservation.StillWarming
        ]
      match model.State with
      | LifecycleState.Faulted _ -> ()
      | other -> failtestf "expected Faulted after %A of silence (bound %A), got %A" silentFor b.Inactivity other

  testPropertyWithConfig propConfig "a warmup that keeps progressing survives past the inactivity bound, up to the absolute bound" <|
    fun (PositiveInt tickCount) ->
      let ticks = min tickCount 20
      let b = { Absolute = TimeSpan.FromMinutes 10.0; Inactivity = TimeSpan.FromSeconds 5.0 }
      // Each cycle: advance 4s (under the 5s inactivity bound), then report
      // Progressed (which resets the inactivity clock in `step`). Repeating
      // this `ticks` times keeps the session alive for 4*ticks seconds even
      // though that easily exceeds the 5s inactivity bound in total — proof
      // that ongoing progress, not raw elapsed time, is what matters here.
      let events =
        [ for _ in 1 .. ticks do
            yield LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds 4.0)
            yield LifecycleEvent.PollTick PollObservation.Progressed ]
      let model = run<unit> b events
      match model.State with
      | LifecycleState.Starting -> ()
      | other -> failtestf "a continuously-progressing warmup should still be Starting after %d ticks, got %A" ticks other

  test "TWIN: a decidePoll that ignores the absolute bound would let a Progressed stream run forever — the property above would NOT catch that regression, so a dedicated absolute-bound test exists" {
    // This twin targets the OTHER invariant directly (see the standalone
    // "elapsed past the absolute bound times out" example above) rather
    // than duplicating it here — recorded as a comment, not a duplicate
    // assertion, so the intent (why that example test exists) is on record
    // next to the property it complements.
    ()
  }

  test "TWIN: a decidePoll with the inactivity check DELETED lets a silent warmup run forever" {
    // A deliberately broken copy of decidePoll — the exact regression
    // BoundedReach exists to catch. If the inactivity branch is dropped,
    // StillWarming always keeps polling regardless of sinceLastActivity.
    let brokenDecidePoll (bounds: Bounds) (elapsed: TimeSpan) (_sinceLastActivity: TimeSpan) (observation: PollObservation<'Loaded>) : PollDecision<'Loaded> =
      match elapsed > bounds.Absolute with
      | true -> PollDecision.TimedOut (absoluteTimeoutReason elapsed)
      | false ->
        match observation with
        | PollObservation.Ready loaded -> PollDecision.MarkReady loaded
        | PollObservation.Faulted reason -> PollDecision.MarkFaulted (reason |> Option.defaultValue defaultFaultReason)
        | PollObservation.Progressed
        | PollObservation.StillWarming
        | PollObservation.ProbeFailed _ -> PollDecision.KeepPolling // BUG: no inactivity check at all
    let b = { Absolute = TimeSpan.FromMinutes 10.0; Inactivity = TimeSpan.FromSeconds 30.0 }
    let decision = brokenDecidePoll b (TimeSpan.FromMinutes 1.0) (TimeSpan.FromMinutes 5.0) PollObservation.StillWarming
    // The broken version keeps polling despite 5 minutes of silence — this
    // is the exact bug fcs-trial-a hit (20+ minutes, no error). Asserting
    // it here proves the twin actually reproduces the old behavior, and by
    // contrast that the REAL decidePoll (exercised in the property above)
    // does NOT do this.
    decision |> Expect.equal "the broken version has no inactivity guard" PollDecision.KeepPolling
    match decidePoll b (TimeSpan.FromMinutes 1.0) (TimeSpan.FromMinutes 5.0) PollObservation.StillWarming with
    | PollDecision.TimedOut _ -> ()
    | other -> failtestf "the REAL decidePoll must catch what the broken twin misses, got %A" other
  }
]

// ---------------------------------------------------------------------
// Property 1 (StopAlwaysWins): defect #2's fix. A StopRequested event from
// ANY state — including mid-warmup, already-Faulted, or already-Stopped —
// always yields Stopped in exactly one step.
// ---------------------------------------------------------------------
let private stopAlwaysWinsTests = testList "StopAlwaysWins (defect #2: stop_session hangs)" [
  testPropertyWithConfig propConfig "StopRequested from a freshly-Starting session always yields Stopped" <|
    fun () ->
      let model = run<unit> bounds [ LifecycleEvent.StopRequested ]
      model.State |> Expect.equal "Stopped" LifecycleState.Stopped

  testPropertyWithConfig propConfig "StopRequested after any number of StillWarming ticks always yields Stopped" <|
    fun (PositiveInt tickCount) ->
      let ticks = min tickCount 50
      let events =
        [ for _ in 1 .. ticks -> LifecycleEvent.PollTick PollObservation.StillWarming ]
        @ [ LifecycleEvent.StopRequested ]
      let model = run<unit> bounds events
      model.State |> Expect.equal "Stopped" LifecycleState.Stopped

  test "StopRequested on an already-Faulted session (a stop arriving after a fault) still yields Stopped" {
    let model =
      run<unit> bounds [
        LifecycleEvent.PollTick (PollObservation.Faulted (Some "boom"))
        LifecycleEvent.StopRequested
      ]
    model.State |> Expect.equal "Stopped, not stuck on Faulted" LifecycleState.Stopped
  }

  test "StopRequested on an already-Ready session still yields Stopped" {
    let model =
      run<string> bounds [
        LifecycleEvent.PollTick (PollObservation.Ready "loaded")
        LifecycleEvent.StopRequested
      ]
    model.State |> Expect.equal "Stopped" LifecycleState.Stopped
  }

  test "a stop arriving WHILE starting (mid-warmup, no prior PollTick) yields Stopped, not left Starting" {
    let model = run<unit> bounds [ LifecycleEvent.ClockAdvance (TimeSpan.FromSeconds 1.0); LifecycleEvent.StopRequested ]
    model.State |> Expect.equal "Stopped" LifecycleState.Stopped
  }

  testPropertyWithConfig propConfig "StopRequested is idempotent — repeating it changes nothing further" <|
    fun (PositiveInt extraStops) ->
      let n = min extraStops 10
      let events = LifecycleEvent.StopRequested :: List.replicate n LifecycleEvent.StopRequested
      let model = run<unit> bounds events
      model.State |> Expect.equal "still Stopped" LifecycleState.Stopped

  test "TWIN: a step function that only handles StopRequested from Starting would fail on late stops" {
    // A deliberately broken copy of `step` — StopRequested is only honored
    // while Starting, exactly the "stop hangs on a session past warmup"
    // failure mode.
    let brokenStep (model: LifecycleModel<'Loaded>) (event: LifecycleEvent<'Loaded>) : LifecycleModel<'Loaded> =
      match event, model.State with
      | LifecycleEvent.StopRequested, LifecycleState.Starting -> { model with State = LifecycleState.Stopped }
      | LifecycleEvent.StopRequested, _ -> model // BUG: stop silently ignored once not Starting
      | LifecycleEvent.ClockAdvance span, _ -> { model with Elapsed = model.Elapsed + span; SinceLastActivity = model.SinceLastActivity + span }
      | LifecycleEvent.PollTick obs, LifecycleState.Starting ->
        match decidePoll model.Bounds model.Elapsed model.SinceLastActivity obs with
        | PollDecision.MarkReady l -> { model with State = LifecycleState.Ready l }
        | PollDecision.MarkFaulted r | PollDecision.TimedOut r -> { model with State = LifecycleState.Faulted r }
        | PollDecision.KeepPolling -> model
      | LifecycleEvent.PollTick _, _ -> model
    let brokenModel =
      [ LifecycleEvent.PollTick (PollObservation.Faulted (Some "boom")); LifecycleEvent.StopRequested ]
      |> List.fold brokenStep (LifecycleModel.starting bounds)
    // The broken version leaves a Faulted session Faulted instead of
    // honoring the stop — reproducing "stop_session never returns for a
    // Faulted session" if this shape ever regressed into production.
    brokenModel.State |> Expect.notEqual "the broken twin fails to stop a Faulted session" LifecycleState.Stopped
    // ...and the REAL step (exercised via `run` in the test above) does not
    // have this bug.
    let realModel = run<unit> bounds [ LifecycleEvent.PollTick (PollObservation.Faulted (Some "boom")); LifecycleEvent.StopRequested ]
    realModel.State |> Expect.equal "the REAL step stops a Faulted session" LifecycleState.Stopped
  }
]

// ---------------------------------------------------------------------
// Property 2 (NoResurrection): defect #3's fix at the pure-model layer.
// Once terminal (Faulted or Stopped), no PollTick — including one racing
// in with stale Ready/Faulted information — moves the state anywhere
// else. This is what makes "one source of truth" true: whichever caller
// asks, and whenever they ask, a terminal state reads the same way.
// ---------------------------------------------------------------------
let private noResurrectionTests = testList "NoResurrection (defect #3: status disagreeing with itself)" [
  testPropertyWithConfig propConfig "once Faulted, no further PollTick changes the state" <|
    fun (faultReason: string) ->
      let reason = if String.IsNullOrEmpty faultReason then "x" else faultReason
      let afterFault =
        run<string> bounds [ LifecycleEvent.PollTick (PollObservation.Faulted (Some reason)) ]
      let afterLateReady =
        [ LifecycleEvent.PollTick (PollObservation.Ready "late-loaded") ]
        |> List.fold step afterFault
      afterLateReady.State |> Expect.equal "still Faulted — a late Ready can't resurrect it" afterFault.State

  testPropertyWithConfig propConfig "once Stopped, no further PollTick (including a late Ready or Faulted) changes the state" <|
    fun () ->
      let afterStop = run<string> bounds [ LifecycleEvent.StopRequested ]
      let afterLateFault =
        [ LifecycleEvent.PollTick (PollObservation.Faulted (Some "late fault")) ]
        |> List.fold step afterStop
      afterLateFault.State |> Expect.equal "still Stopped" LifecycleState.Stopped

  test "a session that reached Ready is not knocked back to Starting by a stray StillWarming tick" {
    let model =
      run<string> bounds [
        LifecycleEvent.PollTick (PollObservation.Ready "loaded")
        LifecycleEvent.PollTick PollObservation.StillWarming
      ]
    model.State |> Expect.equal "still Ready" (LifecycleState.Ready "loaded")
  }

  test "TWIN: a step that doesn't guard terminal states lets a late fault or ready flip a Stopped session back" {
    let brokenStep (model: LifecycleModel<'Loaded>) (event: LifecycleEvent<'Loaded>) : LifecycleModel<'Loaded> =
      match event with
      | LifecycleEvent.StopRequested -> { model with State = LifecycleState.Stopped }
      | LifecycleEvent.ClockAdvance span -> { model with Elapsed = model.Elapsed + span; SinceLastActivity = model.SinceLastActivity + span }
      | LifecycleEvent.PollTick observation ->
        // BUG: no terminal-state guard — a PollTick is processed even after
        // Stopped/Faulted, exactly the "status disagrees with itself"
        // failure mode (a late worker report resurrecting a state a caller
        // already observed as terminal).
        match decidePoll model.Bounds model.Elapsed model.SinceLastActivity observation with
        | PollDecision.MarkReady l -> { model with State = LifecycleState.Ready l }
        | PollDecision.MarkFaulted r | PollDecision.TimedOut r -> { model with State = LifecycleState.Faulted r }
        | PollDecision.KeepPolling -> model
    let brokenModel =
      [ LifecycleEvent.StopRequested; LifecycleEvent.PollTick (PollObservation.Ready "resurrected") ]
      |> List.fold brokenStep (LifecycleModel.starting bounds)
    brokenModel.State |> Expect.notEqual "the broken twin resurrects a Stopped session" LifecycleState.Stopped
    let realModel = run<string> bounds [ LifecycleEvent.StopRequested; LifecycleEvent.PollTick (PollObservation.Ready "resurrected") ]
    realModel.State |> Expect.equal "the REAL step keeps it Stopped" LifecycleState.Stopped
  }
]

let private isTerminalTests = testList "isTerminal" [
  test "Starting is not terminal" { LifecycleState<unit>.Starting |> isTerminal |> Expect.isFalse "not terminal" }
  test "Ready is not terminal" { LifecycleState.Ready "x" |> isTerminal |> Expect.isFalse "not terminal" }
  test "Faulted is terminal" { LifecycleState<unit>.Faulted "x" |> isTerminal |> Expect.isTrue "terminal" }
  test "Stopped is terminal" { LifecycleState<unit>.Stopped |> isTerminal |> Expect.isTrue "terminal" }
]

[<Tests>]
let tests = testList "WarmupSupervision" [
  exampleTests
  boundedReachTests
  stopAlwaysWinsTests
  noResurrectionTests
  isTerminalTests
]
