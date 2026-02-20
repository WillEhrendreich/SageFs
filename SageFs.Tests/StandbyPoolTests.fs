module SageFs.Tests.StandbyPoolTests

open Expecto
open Expecto.Flip
open System
open System.Diagnostics
open System.Threading
open SageFs
open SageFs.WorkerProtocol

// --- Helpers ---

let dummyProxy : SessionProxy =
  fun _ -> async { return WorkerResponse.ResetResult("ok", Ok ()) }

let makeStandby state proxyOpt = {
  Process = new Process()
  Proxy = proxyOpt
  State = state
  WarmupProgress = None
  Projects = ["test.fsproj"]
  WorkingDir = "C:\\test"
  CreatedAt = DateTime.UtcNow
}

// --- Tests ---

[<Tests>]
let standbyPoolTests = testList "StandbyPool" [

  testList "shouldWarmStandby" [
    testCase "warms when primary Ready, no standby, enabled" <| fun _ ->
      StandbyPool.shouldWarmStandby SessionStatus.Ready None true
      |> Expect.isTrue "should warm"

    testCase "does not warm when disabled" <| fun _ ->
      StandbyPool.shouldWarmStandby SessionStatus.Ready None false
      |> Expect.isFalse "disabled"

    testCase "does not warm when primary not Ready" <| fun _ ->
      StandbyPool.shouldWarmStandby SessionStatus.Starting None true
      |> Expect.isFalse "not ready"

    testCase "does not warm when standby already exists" <| fun _ ->
      let existing = makeStandby StandbyState.Warming None
      StandbyPool.shouldWarmStandby SessionStatus.Ready (Some existing) true
      |> Expect.isFalse "already warming"
  ]

  testList "canSwap" [
    testCase "can swap Ready standby with proxy" <| fun _ ->
      let s = makeStandby StandbyState.Ready (Some dummyProxy)
      StandbyPool.canSwap (Some s)
      |> Expect.isTrue "should swap"

    testCase "cannot swap Warming standby" <| fun _ ->
      let s = makeStandby StandbyState.Warming None
      StandbyPool.canSwap (Some s)
      |> Expect.isFalse "warming"

    testCase "cannot swap Ready without proxy" <| fun _ ->
      let s = makeStandby StandbyState.Ready None
      StandbyPool.canSwap (Some s)
      |> Expect.isFalse "no proxy"

    testCase "cannot swap None" <| fun _ ->
      StandbyPool.canSwap None
      |> Expect.isFalse "none"

    testCase "cannot swap Invalidated" <| fun _ ->
      let s = makeStandby StandbyState.Invalidated (Some dummyProxy)
      StandbyPool.canSwap (Some s)
      |> Expect.isFalse "invalidated"
  ]

  testList "shouldInvalidate" [
    testCase "invalidates Warming standby" <| fun _ ->
      let s = makeStandby StandbyState.Warming None
      StandbyPool.shouldInvalidate (Some s)
      |> Expect.isTrue "warming should invalidate"

    testCase "invalidates Ready standby" <| fun _ ->
      let s = makeStandby StandbyState.Ready (Some dummyProxy)
      StandbyPool.shouldInvalidate (Some s)
      |> Expect.isTrue "ready should invalidate"

    testCase "does not invalidate None" <| fun _ ->
      StandbyPool.shouldInvalidate None
      |> Expect.isFalse "none"

    testCase "does not invalidate already Invalidated" <| fun _ ->
      let s = makeStandby StandbyState.Invalidated (Some dummyProxy)
      StandbyPool.shouldInvalidate (Some s)
      |> Expect.isFalse "already invalidated"
  ]

  testList "afterSwap" [
    testCase "swap consumes standby (returns None)" <| fun _ ->
      let s = makeStandby StandbyState.Ready (Some dummyProxy)
      StandbyPool.afterSwap (Some s)
      |> Expect.isNone "consumed after swap"
  ]
]

[<Tests>]
let restartDecisionTests = testList "RestartDecision" [
  testCase "swap when no rebuild and standby ready" <| fun _ ->
    let s = makeStandby StandbyState.Ready (Some dummyProxy)
    match StandbyPool.decideRestart false (Some s) with
    | RestartDecision.SwapStandby _ -> ()
    | RestartDecision.ColdRestart -> failtest "expected SwapStandby"

  testCase "cold restart when rebuild requested even with ready standby" <| fun _ ->
    let s = makeStandby StandbyState.Ready (Some dummyProxy)
    match StandbyPool.decideRestart true (Some s) with
    | RestartDecision.ColdRestart -> ()
    | RestartDecision.SwapStandby _ -> failtest "rebuild should force cold restart"

  testCase "cold restart when standby is Warming" <| fun _ ->
    let s = makeStandby StandbyState.Warming None
    match StandbyPool.decideRestart false (Some s) with
    | RestartDecision.ColdRestart -> ()
    | RestartDecision.SwapStandby _ -> failtest "warming standby cannot swap"

  testCase "cold restart when no standby" <| fun _ ->
    match StandbyPool.decideRestart false None with
    | RestartDecision.ColdRestart -> ()
    | RestartDecision.SwapStandby _ -> failtest "no standby"

  testCase "cold restart when standby Invalidated" <| fun _ ->
    let s = makeStandby StandbyState.Invalidated (Some dummyProxy)
    match StandbyPool.decideRestart false (Some s) with
    | RestartDecision.ColdRestart -> ()
    | RestartDecision.SwapStandby _ -> failtest "invalidated cannot swap"

  testCase "cold restart when standby Ready but no proxy" <| fun _ ->
    let s = makeStandby StandbyState.Ready None
    match StandbyPool.decideRestart false (Some s) with
    | RestartDecision.ColdRestart -> ()
    | RestartDecision.SwapStandby _ -> failtest "no proxy"
]

[<Tests>]
let poolStateTests = testList "PoolState" [
  let key1 = StandbyKey.fromSession ["a.fsproj"] "C:\\proj1"
  let key2 = StandbyKey.fromSession ["b.fsproj"] "C:\\proj2"

  testCase "empty has no standbys" <| fun _ ->
    PoolState.getStandby key1 PoolState.empty
    |> Expect.isNone "empty"

  testCase "set and get standby" <| fun _ ->
    let s = makeStandby StandbyState.Ready (Some dummyProxy)
    let state = PoolState.setStandby key1 s PoolState.empty
    PoolState.getStandby key1 state
    |> Expect.isSome "should find"

  testCase "tryConsumeStandby succeeds for Ready" <| fun _ ->
    let s = makeStandby StandbyState.Ready (Some dummyProxy)
    let state = PoolState.setStandby key1 s PoolState.empty
    let consumed, newState = PoolState.tryConsumeStandby key1 state
    consumed |> Expect.isSome "should consume"
    PoolState.getStandby key1 newState
    |> Expect.isNone "should be removed"

  testCase "tryConsumeStandby fails for Warming" <| fun _ ->
    let s = makeStandby StandbyState.Warming None
    let state = PoolState.setStandby key1 s PoolState.empty
    let consumed, newState = PoolState.tryConsumeStandby key1 state
    consumed |> Expect.isNone "should not consume"
    PoolState.getStandby key1 newState
    |> Expect.isSome "should remain"

  testCase "tryConsumeStandby fails for missing key" <| fun _ ->
    let consumed, _ = PoolState.tryConsumeStandby key1 PoolState.empty
    consumed |> Expect.isNone "nothing to consume"

  testCase "invalidateForDir marks matching standby" <| fun _ ->
    let s = makeStandby StandbyState.Ready (Some dummyProxy)
    let state = PoolState.setStandby key1 s PoolState.empty
    let newState = PoolState.invalidateForDir "C:\\proj1" state
    let sb = PoolState.getStandby key1 newState
    sb |> Expect.isSome "should still exist"
    sb.Value.State
    |> Expect.equal "should be invalidated" StandbyState.Invalidated

  testCase "invalidateForDir does not affect other dirs" <| fun _ ->
    let s1 = makeStandby StandbyState.Ready (Some dummyProxy)
    let s2 = makeStandby StandbyState.Ready (Some dummyProxy)
    let state =
      PoolState.empty
      |> PoolState.setStandby key1 s1
      |> PoolState.setStandby key2 s2
    let newState = PoolState.invalidateForDir "C:\\proj1" state
    let sb2 = PoolState.getStandby key2 newState
    sb2.Value.State
    |> Expect.equal "proj2 unaffected" StandbyState.Ready

  testCase "project order is normalized" <| fun _ ->
    let k1 = StandbyKey.fromSession ["b.fsproj"; "a.fsproj"] "C:\\dir"
    let k2 = StandbyKey.fromSession ["a.fsproj"; "b.fsproj"] "C:\\dir"
    k1 |> Expect.equal "normalized key" k2
]

[<Tests>]
let warmupProgressLabelTests = testList "StandbyInfo warmup progress labels" [
  testCase "Warming with empty string shows default label" <| fun _ ->
    StandbyInfo.label (StandbyInfo.Warming "")
    |> Expect.equal "empty progress = default label" "⏳ standby"
  testCase "Warming with progress message shows phase" <| fun _ ->
    StandbyInfo.label (StandbyInfo.Warming "2/4 Scanned 12 files")
    |> Expect.equal "shows phase text" "⏳ 2/4 Scanned 12 files"
  testCase "Warming with final phase shows completion" <| fun _ ->
    StandbyInfo.label (StandbyInfo.Warming "4/4 Warm-up complete in 1234ms")
    |> Expect.equal "shows final phase" "⏳ 4/4 Warm-up complete in 1234ms"
  testCase "Ready label unchanged" <| fun _ ->
    StandbyInfo.label StandbyInfo.Ready
    |> Expect.equal "ready unchanged" "✓ standby"
  testCase "Invalidated label unchanged" <| fun _ ->
    StandbyInfo.label StandbyInfo.Invalidated
    |> Expect.equal "invalidated unchanged" "⚠ standby"
  testCase "NoPool label unchanged" <| fun _ ->
    StandbyInfo.label StandbyInfo.NoPool
    |> Expect.equal "nopool unchanged" ""
]

[<Tests>]
let standbyBenchmarkTests = testList "StandbyPool benchmarks" [
  testCase "decideRestart is < 1µs" <| fun _ ->
    let s = makeStandby StandbyState.Ready (Some dummyProxy)
    // warmup
    for _ in 1..1000 do
      StandbyPool.decideRestart false (Some s) |> ignore
    let sw = Stopwatch.StartNew()
    let n = 100_000
    for _ in 1..n do
      StandbyPool.decideRestart false (Some s) |> ignore
    sw.Stop()
    let usPerOp = sw.Elapsed.TotalMicroseconds / float n
    printfn "decideRestart: %.3fµs/op" usPerOp
    Expect.isLessThan "should be sub-microsecond" (usPerOp, 1.0)

  testCase "tryConsumeStandby is < 5µs" <| fun _ ->
    let key = StandbyKey.fromSession ["test.fsproj"] "C:\\test"
    let s = makeStandby StandbyState.Ready (Some dummyProxy)
    let state = PoolState.setStandby key s PoolState.empty
    // warmup
    for _ in 1..1000 do
      PoolState.tryConsumeStandby key state |> ignore
    let sw = Stopwatch.StartNew()
    let n = 100_000
    for _ in 1..n do
      PoolState.tryConsumeStandby key state |> ignore
    sw.Stop()
    let usPerOp = sw.Elapsed.TotalMicroseconds / float n
    printfn "tryConsumeStandby: %.3fµs/op" usPerOp
    Expect.isLessThan "should be fast" (usPerOp, 5.0)

  testCase "invalidateForDir is < 10µs for 5 standbys" <| fun _ ->
    let mutable state = PoolState.empty
    for i in 1..5 do
      let k = StandbyKey.fromSession [sprintf "proj%d.fsproj" i] "C:\\test"
      state <- PoolState.setStandby k (makeStandby StandbyState.Ready (Some dummyProxy)) state
    // warmup
    for _ in 1..100 do
      PoolState.invalidateForDir "C:\\test" state |> ignore
    let sw = Stopwatch.StartNew()
    let n = 10_000
    for _ in 1..n do
      PoolState.invalidateForDir "C:\\test" state |> ignore
    sw.Stop()
    let usPerOp = sw.Elapsed.TotalMicroseconds / float n
    printfn "invalidateForDir (5 standbys): %.3fµs/op" usPerOp
    Expect.isLessThan "should be fast" (usPerOp, 10.0)
]

[<Tests>]
let sseProgressCallbackTests = testList "SSE progress callback" [
  testCase "StandbyProgress for nonexistent key does NOT trigger callback" <| fun _ ->
    use cts = new CancellationTokenSource(5000)
    let mutable callCount = 0
    let mgr = SessionManager.create cts.Token (fun () -> callCount <- callCount + 1)
    let fakeKey = { StandbyKey.Projects = ["test.fsproj"]; WorkingDir = "C:\\fake" }
    mgr.Post(SessionManager.SessionCommand.StandbyProgress(fakeKey, "1/4 test"))
    Thread.Sleep(100)
    Expect.equal "callback should not fire for non-existent standby" 0 callCount

  testCase "callback not invoked on creation alone" <| fun _ ->
    use cts = new CancellationTokenSource(5000)
    let mutable callCount = 0
    let _mgr = SessionManager.create cts.Token (fun () -> callCount <- callCount + 1)
    Thread.Sleep(100)
    Expect.equal "callback should not fire on creation alone" 0 callCount
]
