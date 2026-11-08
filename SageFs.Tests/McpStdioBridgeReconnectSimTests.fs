module SageFs.Tests.McpStdioBridgeReconnectSimTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.McpBridge
open SageFs.Simulation.McpStdioBridgeReconnectSim

/// DST for the stdio bridge's reconnect with the re-handshake NOT atomic (roast 2026-11-08, finding 3).
/// SageFs.Core/McpBridge.fs folded through SageFs.Simulation/McpStdioBridgeReconnectSim.fs: stdin lines,
/// 404s, captures and push-stream ends interleave on a seeded schedule against a model of the daemon. A
/// failure here is a genuine bug in the pure decision core; the failing seed replays exactly.

let pick (gen: Gen<'a>) = (Gen.sample 1 gen).[0]

let simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let genSeed: Gen<int> = Gen.choose (0, TestMagnitudes.seedBound)

let describe (t: Trace) : string =
  sprintf
    "replay this scenario:\n  Seed=%d\n  Policy=%A\n  Latencies=%A\n  Script=%A\n  Violations=%A"
    t.Scenario.Seed
    t.Scenario.Policy
    t.Scenario.Latencies
    t.Scenario.Script
    (violations t |> List.map (fun v -> v.Id, v.Message))

let assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | _ -> failtest (describe t)

let idsOf (t: Trace) : string list = violations t |> List.map (fun v -> v.Id) |> List.distinct

[<Tests>]
let tests =
  testList "McpStdioBridge reconnect DST (the re-handshake is not atomic)" [

    testList "invariants hold over generated upgrades (real decide)" [
      testPropertyWithConfig simConfig "seeded upgrades with stdin lines in the gap: every invariant holds" <| fun () ->
        assertHolds (run (fromSeed (pick genSeed)))

      testProperty "same seed => identical trace" <| fun (seed: int) ->
        let a = run (fromSeed seed)
        let b = run (fromSeed seed)
        a.Actions = b.Actions && a.Final = b.Final && a.Answered = b.Answered
    ]

    testList "named scenarios" [
      testCase "WHY — the roast's case: a line arrives while the new session is being minted, and the bridge ends on ONE session, ONE push stream, the line answered once" <| fun _ ->
        let t = run lineDuringRehandshake
        assertHolds t
        t.Daemon.Minted |> Map.find 2 |> Expect.equal "the new daemon minted one session for this bridge" 1
        t.Captures |> List.length |> Expect.equal "one capture after the first daemon" 1
        t.Notifications |> Expect.equal "the notification reached the client once" [ 1, 1 ]

      testCase "two requests under an id the daemon forgot mint ONE session between them, because the heal is gated" <| fun _ ->
        let t = run twoRequestsUnderAStaleId
        assertHolds t
        t.Daemon.Minted |> Map.find 2 |> Expect.equal "one session minted by the heal" 1

      testCase "WHY — a request in flight when the new daemon answers and the bridge's own re-handshake share ONE session (found by the seeded sweep, seed 218752)" <| fun _ ->
        let t = run healRacesRehandshake
        assertHolds t
        t.Daemon.Minted |> Map.find 2 |> Expect.equal "one session minted on the new daemon" 1
        let ungated = run { healRacesRehandshake with Heal = Heal.Ungated }
        idsOf ungated |> Expect.contains "without the shared gate the two mint one each" "one-session-per-daemon-incarnation"

      testCase "WHY — a client that outruns a daemon that stays down hits the cap: the queue stops at it and every line past it is answered" <| fun _ ->
        let t = run floodWhileDown
        assertHolds t
        t.MostQueued |> Expect.equal "the queue stopped at the cap" floodWhileDown.Policy.QueueCap
        t.Answered |> List.length |> Expect.equal "all forty lines were answered, delivered or rejected" 40
    ]

    testList "the twins have teeth" [
      testCase "the bridge before the fix (atomic re-handshake, no stream cancel) forwards under the stale id on the roast's case" <| fun _ ->
        idsOf (runTwinAtomicRehandshake lineDuringRehandshake)
        |> Expect.contains "the forward under the stale id is caught" "no-request-under-a-session-the-new-daemon-does-not-know"
        // With the heal ungated as well (the whole of the old behaviour) the second session, the second push
        // stream and the doubled notification all show.
        idsOf (runWith Twin.decideAtomicRehandshake { lineDuringRehandshake with Heal = Heal.Ungated })
        |> Expect.containsAll
          "the stale-id forward, the second session, the second stream and the doubled notification are all caught"
          [ "no-request-under-a-session-the-new-daemon-does-not-know"
            "one-session-per-daemon-incarnation"
            "never-two-push-streams"
            "each-notification-at-most-once" ]

      testCase "an ungated heal mints a session per stale request" <| fun _ ->
        let t = run { twoRequestsUnderAStaleId with Heal = Heal.Ungated }
        idsOf t |> Expect.contains "the second mint is caught" "one-session-per-daemon-incarnation"

      testCase "an unbounded queue grows with the flood" <| fun _ ->
        let t = runTwinUnbounded floodWhileDown
        idsOf t |> Expect.contains "the queue past the cap is caught" "queue-within-cap"

      testCase "the atomic twin is caught on a good share of seeded upgrades, so the property over seeds is not vacuous" <| fun _ ->
        let caught =
          [ 0..299 ]
          |> List.filter (fun seed -> idsOf (runTwinAtomicRehandshake (fromSeed seed)) |> List.isEmpty |> not)
          |> List.length
        (caught > 20) |> Expect.isTrue (sprintf "the twin broke an invariant on %d of 300 seeds, which should be more than 20" caught)
    ]
  ]
