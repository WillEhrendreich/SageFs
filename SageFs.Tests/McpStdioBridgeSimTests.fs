module SageFs.Tests.McpStdioBridgeSimTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.McpBridge
open SageFs.Simulation
open SageFs.Simulation.McpStdioBridgeSim
open SageFs.Simulation.McpStdioBridgeInvariants

/// DST for the `sagefs mcp` startup race — SageFs.Core/McpBridge.fs folded
/// through SageFs.Simulation/McpStdioBridgeSim.fs. A failure here is a
/// genuine bug in the pure decision core; the failing seed (and the printed
/// scenario) replays exactly.

let private pick (gen: Gen<'a>) = (Gen.sample 1 gen).[0]

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private genSeed: Gen<int> = Gen.choose (0, 1_000_000)

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION — replay this scenario:\n  Seed=%d\n  BridgeCount=%d\n  Daemon=%A\n  Policy=%A\n  Ops=%A\n  Violations=%A"
      t.Scenario.Seed
      t.Scenario.BridgeCount
      t.Scenario.Daemon
      t.Scenario.Policy
      t.Scenario.Ops
      (vs |> List.map (fun (inv, msg) -> inv.Id, msg))

[<Tests>]
let tests =
  testList "McpStdioBridge DST simulation" [

    testList "invariants hold over generated scenarios (real decide)" [

      testPropertyWithConfig simConfig "seeded scenarios — every invariant holds" <| fun () ->
        let seed = pick genSeed
        assertHolds (run (McpStdioBridgeGenerators.fromSeed seed))

      testPropertyWithConfig simConfig "connected-or-clear-error holds" <| fun () ->
        let t = run (McpStdioBridgeGenerators.fromSeed (pick genSeed))
        connectedOrClearError.Check t |> Expect.equal (sprintf "for seed %d" t.Scenario.Seed) Outcome.Holds

      testPropertyWithConfig simConfig "daemon-started-at-most-once-per-bridge holds" <| fun () ->
        let t = run (McpStdioBridgeGenerators.fromSeed (pick genSeed))
        daemonStartedAtMostOncePerBridge.Check t |> Expect.equal (sprintf "for seed %d" t.Scenario.Seed) Outcome.Holds

      testPropertyWithConfig simConfig "daemon-start-attempted holds" <| fun () ->
        let t = run (McpStdioBridgeGenerators.fromSeed (pick genSeed))
        daemonStartAttempted.Check t |> Expect.equal (sprintf "for seed %d" t.Scenario.Seed) Outcome.Holds

      testPropertyWithConfig simConfig "messages-never-lost-or-duplicated holds" <| fun () ->
        let t = run (McpStdioBridgeGenerators.fromSeed (pick genSeed))
        messagesNeverLostOrDuplicated.Check t |> Expect.equal (sprintf "for seed %d" t.Scenario.Seed) Outcome.Holds
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical trace" <| fun (seed: int) ->
        let a = run (McpStdioBridgeGenerators.fromSeed seed)
        let b = run (McpStdioBridgeGenerators.fromSeed seed)
        a.Bridges |> Expect.equal "replaying the same seed yields identical bridge traces" b.Bridges
    ]

    testList "named canonical scenarios" [
      testCase "daemon absent at spawn: the message sent before any probe is queued then forwarded, not lost" <| fun _ ->
        let t = run McpStdioBridgeGenerators.daemonAbsentAtSpawn
        assertHolds t
        t.Bridges.[0].State.Transport |> Expect.equal "bridge reaches Ready" (SageFs.McpBridge.TransportState.Ready None)

      testCase "two clients racing to start: neither bridge starts the daemon twice" <| fun _ ->
        let t = run McpStdioBridgeGenerators.twoClientsRaceToStart
        assertHolds t
        t.Bridges |> Map.forall (fun _ bt -> bt.StartDaemonCount <= 1) |> Expect.isTrue "at most one StartDaemon per bridge"

      testCase "daemon never appears: a clean Fatal, and the queued message is rejected, not lost" <| fun _ ->
        let t = run McpStdioBridgeGenerators.daemonNeverAppears
        assertHolds t
        match t.Bridges.[0].State.Transport with
        | SageFs.McpBridge.TransportState.Fatal _ -> ()
        | other -> failtestf "expected Fatal, got %A" other

      testCase "daemon already up: no StartDaemon at all" <| fun _ ->
        let t = run McpStdioBridgeGenerators.daemonAlreadyUp
        assertHolds t
        t.Bridges.[0].StartDaemonCount |> Expect.equal "never asked to start an already-running daemon" 0

      testCase "daemon dies mid-session: a clear Fatal, not a hang" <| fun _ ->
        let t = run McpStdioBridgeGenerators.daemonDiesMidSession
        match t.Bridges.[0].State.Transport with
        | SageFs.McpBridge.TransportState.Fatal _ -> ()
        | other -> failtestf "expected Fatal after the connection dies, got %A" other
    ]

    testList "the twin has teeth" [
      testCase "runTwinNeverStarts violates daemon-start-attempted (the real regression, reproduced)" <| fun _ ->
        let t = runTwinNeverStarts McpStdioBridgeGenerators.daemonAbsentAtSpawn
        match daemonStartAttempted.Check t with
        | Outcome.Violated _ -> ()
        | Outcome.Holds -> failtest "the twin should have violated daemon-start-attempted, but didn't — the invariant has no teeth"

      testCase "the twin still satisfies connected-or-clear-error (it fails closed, not open)" <| fun _ ->
        // This is the reason a SEPARATE invariant was needed: the twin's bug
        // is a silent liveness gap, not a hang — it still gives up cleanly.
        let t = runTwinNeverStarts McpStdioBridgeGenerators.daemonNeverAppears
        connectedOrClearError.Check t |> Expect.equal "the twin still reaches a clean terminal state" Outcome.Holds

      testPropertyWithConfig simConfig "the twin violates daemon-start-attempted whenever the daemon isn't already up" <| fun () ->
        let scenario = McpStdioBridgeGenerators.fromSeed (pick genSeed)
        match scenario.Daemon.AlreadyUp with
        | true -> () // nothing to prove — the fast path never calls StartDaemon under either reducer
        | false ->
          let t = runTwinNeverStarts scenario
          // A bridge whose client closed before ever mattering is exempt
          // from the invariant itself (see daemonStartAttempted's doc) — so
          // the precondition for expecting a violation has to mirror that
          // exemption too, or this property claims a violation is due when
          // the invariant correctly says none is.
          let anyBridgeStillInPlay =
            t.Bridges
            |> Map.exists (fun _ bt ->
              match bt.State.Transport with
              | SageFs.McpBridge.TransportState.Closed -> false
              | SageFs.McpBridge.TransportState.Ready _
              | SageFs.McpBridge.TransportState.Fatal _
              | SageFs.McpBridge.TransportState.AwaitingDaemon _ -> bt.UnhealthyProbeCount > 0)
          match anyBridgeStillInPlay with
          | false -> () // this particular schedule never actually probed a bridge that stayed connected — nothing to prove
          | true ->
            match daemonStartAttempted.Check t with
            | Outcome.Violated _ -> ()
            | Outcome.Holds -> failtestf "twin should violate daemon-start-attempted for seed %d" scenario.Seed
    ]

    testList "the Mcp-Session-Id capture-ordering race (issue #138)" [
      let sampleMessage (tag: int) : RpcMessage =
        RpcMessage.Request(RpcId.N(int64 tag), "tools/list", sprintf """{"jsonrpc":"2.0","id":%d,"method":"tools/list"}""" tag)

      testCase "RED — today's ordering (capture-after-write) loses to an instant client" <| fun _ ->
        // This is the exact shape of issue #138: the Python repro's
        // notifications/initialized + tools/list sent immediately after
        // initialize's reply, with no sleep in between. Verified by hand to
        // fail against the OLD `forward` (capture posted after the stdout
        // write) before the fix landed — Expecto reported exactly:
        // "CaptureAfterWrite + Instant: the client's next request (...) was
        // forwarded WITHOUT the session id sid-1 the daemon had already
        // issued".
        match sessionIdRaceHolds defaultPolicy CapturePolicy.CaptureAfterWrite ClientSpeed.Instant "sid-1" (sampleMessage 2) with
        | Outcome.Violated _ -> ()
        | Outcome.Holds -> failtest "capture-after-write + an instant client should lose the race — it doesn't, the invariant has no teeth"

      testCase "capture-after-write survives a slow client — the issue's own '3-second pause' workaround" <| fun _ ->
        sessionIdRaceHolds defaultPolicy CapturePolicy.CaptureAfterWrite ClientSpeed.Slow "sid-1" (sampleMessage 2)
        |> Expect.equal "a slow-enough client never sees the race" Outcome.Holds

      testCase "GREEN — the fix (capture-before-write) survives an instant client" <| fun _ ->
        sessionIdRaceHolds defaultPolicy CapturePolicy.CaptureBeforeWrite ClientSpeed.Instant "sid-1" (sampleMessage 2)
        |> Expect.equal "capture-before-write is unconditionally safe" Outcome.Holds

      testCase "capture-before-write survives a slow client too" <| fun _ ->
        sessionIdRaceHolds defaultPolicy CapturePolicy.CaptureBeforeWrite ClientSpeed.Slow "sid-1" (sampleMessage 2)
        |> Expect.equal "safe regardless of client speed" Outcome.Holds

      testPropertyWithConfig simConfig "capture-before-write is safe for every session id, message, and client speed" <| fun () ->
        let n = pick genSeed
        let sid = sprintf "sid-%d" n
        let speed = if n % 2 = 0 then ClientSpeed.Instant else ClientSpeed.Slow
        sessionIdRaceHolds defaultPolicy CapturePolicy.CaptureBeforeWrite speed sid (sampleMessage n)
        |> Expect.equal (sprintf "capture-before-write never loses, for n=%d" n) Outcome.Holds

      testPropertyWithConfig simConfig "capture-after-write always loses to an instant client, for every session id and message" <| fun () ->
        let n = pick genSeed
        let sid = sprintf "sid-%d" n
        match sessionIdRaceHolds defaultPolicy CapturePolicy.CaptureAfterWrite ClientSpeed.Instant sid (sampleMessage n) with
        | Outcome.Violated _ -> ()
        | Outcome.Holds -> failtestf "should have lost the race for n=%d" n
    ]
  ]
