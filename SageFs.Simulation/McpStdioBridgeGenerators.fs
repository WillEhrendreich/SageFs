namespace SageFs.Simulation

open System
open SageFs.McpBridge
open SageFs.Simulation.McpStdioBridgeSim

/// Seeded, dependency-free generators for the stdio-bridge startup-race sim
/// (mirrors `MgrGenerators.fromSeed`). `run (fromSeed n)` replays identically
/// forever — chaos is data, not ambient randomness.
module McpStdioBridgeGenerators =

  /// A uniquely-tagged request, so `messagesNeverLostOrDuplicated` can tell
  /// two messages apart by structural equality alone — `tag` is embedded in
  /// both the JSON-RPC id and the raw text.
  let private taggedMessage (tag: int) : RpcMessage =
    RpcMessage.Request(RpcId.N(int64 tag), "tools/list", sprintf """{"jsonrpc":"2.0","id":%d,"method":"tools/list","tag":%d}""" tag tag)

  /// A general scenario: 1 or 2 independent bridges (modelling one client, or
  /// two racing to spawn the bridge at once), a randomized daemon
  /// startup/absence, and a mixed, seeded stream of probes, client sends,
  /// closes and mid-session connection drops.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let bridgeCount = 1 + rnd.Next(0, 2) // 1 or 2
    let daemon =
      { AlreadyUp = rnd.Next(0, 4) = 0 // 25% already up, 75% must be started
        WarmupTicks = rnd.Next(0, 6) }
    let policy = { MaxProbeAttempts = 3 + rnd.Next(0, 6) } // small, so give-up paths get exercised too
    let opCount = rnd.Next(3, 30)
    let mutable tag = 0
    let nextTag () =
      tag <- tag + 1
      tag
    let closedBridges = System.Collections.Generic.HashSet<int>()
    let ops =
      [ for _ in 1 .. opCount do
          let b = rnd.Next(0, bridgeCount)
          match closedBridges.Contains b with
          | true ->
            // A closed bridge's own client is gone — no more ops scheduled
            // for it. Route to probing another bridge instead so a closed
            // instance doesn't silently shrink the op count.
            yield BridgeOp.ProbeTick((b + 1) % bridgeCount)
          | false ->
            match rnd.Next(0, 10) with
            | 0 | 1 | 2 | 3 | 4 -> yield BridgeOp.ProbeTick b
            | 5 | 6 | 7 -> yield BridgeOp.ClientSend(b, taggedMessage(nextTag ()))
            | 8 ->
              closedBridges.Add b |> ignore
              yield BridgeOp.ClientClose b
            | _ -> yield BridgeOp.ConnectionDies b ]
    { Seed = seed
      Policy = policy
      BridgeCount = bridgeCount
      Daemon = daemon
      Ops = ops }

  // ── Named canonical scenarios (worked examples) ───────────────────────────

  /// The exact regression this bridge exists to fix: the daemon is absent
  /// when the client spawns the bridge, and a message arrives immediately —
  /// before the client, or anything, could possibly know the daemon isn't up
  /// yet.
  let daemonAbsentAtSpawn : Scenario =
    { Seed = -1
      Policy = defaultPolicy
      BridgeCount = 1
      Daemon = { AlreadyUp = false; WarmupTicks = 3 }
      Ops =
        [ BridgeOp.ClientSend(0, taggedMessage 1)
          BridgeOp.ProbeTick 0
          BridgeOp.ProbeTick 0
          BridgeOp.ProbeTick 0
          BridgeOp.ProbeTick 0 ] }

  /// Two clients spawn the bridge at the same instant, daemon absent. Both
  /// race to probe and (in the real decide) both independently decide to
  /// start it — the system-level "never started twice" guarantee is the
  /// daemon's own attach check (Program.fs's `decideDaemonLaunch`), not this
  /// bridge; what THIS invariant proves is that neither bridge asks twice
  /// itself.
  let twoClientsRaceToStart : Scenario =
    { Seed = -2
      Policy = defaultPolicy
      BridgeCount = 2
      Daemon = { AlreadyUp = false; WarmupTicks = 2 }
      Ops = [ BridgeOp.ProbeTick 0; BridgeOp.ProbeTick 1; BridgeOp.ProbeTick 0; BridgeOp.ProbeTick 1 ] }

  /// The daemon never comes up at all within the probe budget — a hard
  /// give-up, with a message still queued when it happens.
  let daemonNeverAppears : Scenario =
    { Seed = -3
      Policy = { MaxProbeAttempts = 4 }
      BridgeCount = 1
      Daemon = { AlreadyUp = false; WarmupTicks = 1000 } // never reached within the probe budget
      Ops = [ BridgeOp.ClientSend(0, taggedMessage 1) ] }

  /// The daemon is already up (a second client attaching to one another
  /// client already started) — the fast path, no StartDaemon at all.
  let daemonAlreadyUp : Scenario =
    { Seed = -4
      Policy = defaultPolicy
      BridgeCount = 1
      Daemon = { AlreadyUp = true; WarmupTicks = 0 }
      Ops = [ BridgeOp.ClientSend(0, taggedMessage 1); BridgeOp.ProbeTick 0 ] }

  /// The daemon dies mid-session, after the bridge is fully connected.
  let daemonDiesMidSession : Scenario =
    { Seed = -5
      Policy = defaultPolicy
      BridgeCount = 1
      Daemon = { AlreadyUp = true; WarmupTicks = 0 }
      Ops = [ BridgeOp.ProbeTick 0; BridgeOp.ClientSend(0, taggedMessage 1); BridgeOp.ConnectionDies 0 ] }
