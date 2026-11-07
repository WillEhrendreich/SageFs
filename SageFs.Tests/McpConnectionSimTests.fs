module SageFs.Tests.McpConnectionSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.McpConnectionSim

/// DST over MCP connections that open, call, stop reading and are abandoned while the daemon broadcasts notifications,
/// folded through the REAL `McpFanout.broadcast`. The twin is the daemon of 2026-11-07 (a failed connection is only
/// forgotten by the registry and its next call registers it again): the invariants must FAIL against it, or they
/// prove nothing.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 50 }

let private steps = 400

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | found -> failtestf "INVARIANT VIOLATION (first of %d): %s" found.Length (List.head found)

[<Tests>]
let tests =
  testList "DST MCP connections" [

    testList "the real fan-out holds every invariant" [

      testPropertyWithConfig simConfig "seeded lifecycles: no stopped client holds more than one notification, none stays registered" <|
        fun (seed: int) -> assertHolds (run Behavior.Real (scenarioOf seed steps))

      testPropertyWithConfig simConfig "sessions opened and abandoned many times end with nothing held for them" <|
        fun (seed: int) ->
          let final = run Behavior.Real (scenarioOf seed steps) |> List.last
          stuckBytes final |> Expect.equal "every abandoned connection's buffer was freed" 0L

      testProperty "same seed, same trace (replay by seed)" <| fun (seed: int) ->
        let events = scenarioOf seed steps
        let heldAt trace = trace |> List.map (fun (s: State) -> stuckBytes s, s.Registered.Count)
        heldAt (run Behavior.Real events) |> Expect.equal "identical" (heldAt (run Behavior.Real events))
    ]

    testList "the twin reproduces the leak" [

      testCase "REPRODUCED — a stuck client's buffer grows without bound when the failed connection is only forgotten" <| fun _ ->
        let events =
          [ yield SimEvent.Open 0
            yield SimEvent.Abandon 0
            for _ in 1 .. 50 do
              yield SimEvent.Call 0
              yield SimEvent.Notify 500 ]
        let states = run Behavior.KeepsStuckConnectionTwin events
        (stuckBytes (List.last states) > int64 maxNotificationBytes * 40L) |> Expect.isTrue "50 notifications of 500 bytes pile up"
        violations states |> List.isEmpty |> Expect.isFalse "and the invariants catch it"

      testCase "the same events against the real fan-out hold nothing" <| fun _ ->
        let events =
          [ yield SimEvent.Open 0
            yield SimEvent.Abandon 0
            for _ in 1 .. 50 do
              yield SimEvent.Call 0
              yield SimEvent.Notify 500 ]
        let states = run Behavior.Real events
        stuckBytes (List.last states) |> Expect.equal "freed at the first failed send" 0L
        violations states |> Expect.isEmpty "no violation"

      testProperty "some seed breaks the twin (the invariants have teeth)" <| fun (seed: int) ->
        // Not every seed reaches a stuck-then-called connection, so this holds for the whole family, not each seed.
        let broken =
          [ for offset in 0 .. 20 -> violations (run Behavior.KeepsStuckConnectionTwin (scenarioOf (seed + offset) steps)) ]
          |> List.exists (List.isEmpty >> not)
        broken |> Expect.isTrue "within 21 neighbouring seeds the twin breaks an invariant"
    ]
  ]
