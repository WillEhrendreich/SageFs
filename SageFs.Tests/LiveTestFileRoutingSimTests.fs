module SageFs.Tests.LiveTestFileRoutingSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.LiveTestFileRoutingSim
open SageFs.Simulation.LiveTestFileRoutingInvariants

/// DST for "which sessions' live testing does a file change reach". The subject is the real
/// `SageFs.LiveTestFileRouting.route`, which `SageFsUpdate` consults for `FileContentChanged`.
/// The fold is `SageFs.Simulation/LiveTestFileRoutingSim.fs`; the twins are the original
/// primary-gated shape and an activation-ignoring shape.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s), replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Router t.Scenario.Seed t.Scenario.Ops vs

let private firedBy (t: Trace) = violations t |> List.map fst

[<Tests>]
let tests =
  testList "DST live-testing file routing" [

    testList "the real routing holds the invariants" [

      testPropertyWithConfig simConfig
        "WHY — seeded scenarios: a save reaches exactly the owners that have live testing on, whoever is primary"
        <| fun (seed: int) -> assertHolds (run (LiveTestFileRoutingGenerators.fromSeed seed))

      testCase "WHY — primaryOffOtherOn: session 1 receives its save while the primary session has it off" <| fun _ ->
        let t = run LiveTestFileRoutingGenerators.primaryOffOtherOn
        (t.Final.Decisions |> List.exactlyOne).Routed
        |> Expect.equal "session 1 receives it" (Routed.Sessions [ 1 ])
        assertHolds t

      testCase "WHY — sharedDirectoryOneOn: only the owner of the shared directory with live testing on receives it" <| fun _ ->
        let t = run LiveTestFileRoutingGenerators.sharedDirectoryOneOn
        (t.Final.Decisions |> List.exactlyOne).Routed
        |> Expect.equal "session 2 only" (Routed.Sessions [ 2 ])
        assertHolds t

      testCase "WHY — nestedWorktree: a save in a worktree belongs to the worktree session, not the checkout around it" <| fun _ ->
        let t = run LiveTestFileRoutingGenerators.nestedWorktree
        t.Final.Decisions
        |> List.map (fun d -> d.Routed)
        |> Expect.equal "worktree save to session 3, checkout save to session 0" [ Routed.Sessions [ 3 ]; Routed.Sessions [ 0 ] ]
        assertHolds t

      testCase "WHY — outsiderGoesToFallback: a file no session owns is the fallback's" <| fun _ ->
        let t = run LiveTestFileRoutingGenerators.outsiderGoesToFallback
        (t.Final.Decisions |> List.exactlyOne).Routed
        |> Expect.equal "fallback" Routed.Fallback
        assertHolds t
    ]

    testList "the twins reproduce the wrong shapes" [

      testCase "WHY — REPRODUCED: the primary-gated twin drops a save for a session that has live testing on" <| fun _ ->
        firedBy (runPrimaryGated LiveTestFileRoutingGenerators.primaryOffOtherOn)
        |> Expect.contains
             "the primary-never-gates invariant must fire"
             "the-primary-sessions-activation-never-gates-another-session"

      testCase "WHY — REPRODUCED: the activation-ignoring twin delivers to a session that has live testing off" <| fun _ ->
        firedBy (runIgnoresActivation LiveTestFileRoutingGenerators.sharedDirectoryOneOn)
        |> Expect.contains
             "the receives-nothing invariant must fire"
             "a-session-with-live-testing-off-or-not-owning-the-path-receives-nothing"

      testCase "WHY — the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 120 ]
        let anyViolates twin =
          seeds |> List.exists (fun seed -> not (List.isEmpty (violations (twin (LiveTestFileRoutingGenerators.fromSeed seed)))))
        anyViolates runPrimaryGated
        |> Expect.isTrue "the primary-gated twin is caught by some seed"
        anyViolates runIgnoresActivation
        |> Expect.isTrue "the activation-ignoring twin is caught by some seed"
    ]

    testList "determinism and replay" [

      testProperty "WHY — the same seed replays to the identical final state" <|
        fun (seed: int) ->
          let a = run (LiveTestFileRoutingGenerators.fromSeed seed)
          let b = run (LiveTestFileRoutingGenerators.fromSeed seed)
          a.Final = b.Final
    ]
  ]
