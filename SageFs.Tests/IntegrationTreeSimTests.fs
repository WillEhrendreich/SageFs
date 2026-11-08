module SageFs.Tests.IntegrationTreeSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.IntegrationTreeSim

/// DST of the integration worktree and the build its session holds (P14, C7 and C8). See
/// SageFs.Simulation/IntegrationTreeSim.fs: the daemon's own decisions (`CohortLandingPlan`) run against a model of commits,
/// the session's build and the worktree, and the same invariants are broken by two twins that carry the pre-fix behaviour.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (trace: Trace) =
  match violations trace with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION (%A) - replay:\n  Scenario=%A\n  Violations=%A" trace.Behaviour trace.Scenario vs

let private violatedIds (trace: Trace) = violations trace |> List.map fst

[<Tests>]
let tests =
  testList "DST integration worktree and session build (P14)" [

    testList "the real decisions hold the invariants" [
      testPropertyWithConfig simConfig "seeded scenarios: verification is sound and the worktree is idle at the head" <| fun (seed: int) ->
        assertHolds (run Behaviour.Real (fromSeed seed))

      test "a refused source landing, then a test-only landing: the second is verified against a rebuilt session" {
        let trace = run Behaviour.Real refusedSourceThenTestOnly
        assertHolds trace
        trace.Final.Verifications |> List.last |> fun v -> v.Rebuilt |> Expect.isTrue "the session was rebuilt for the second landing"
      }

      test "a refused landing last leaves the worktree at the head" {
        let trace = run Behaviour.Real refusedLast
        assertHolds trace
        trace.Final.WorktreeAt |> Expect.equal "back at the head" trace.Final.Head
      }

      test "landings that only touch tests never rebuild" {
        let trace = run Behaviour.Real onlyTestsLanding
        assertHolds trace
        trace.Final.Rebuilds |> Expect.equal "no rebuilds" 0
      }
    ]

    testList "the twins break them, so the invariants have teeth" [
      test "a verifier that trusts the session's build whatever it was made from verifies against a failed landing's build" {
        let trace = run Behaviour.NeverChecksTheBuild refusedSourceThenTestOnly
        violatedIds trace |> Expect.contains "sound verification is violated" "sound-verification"
      }

      test "a worktree that is never reset stays on the refused landing's commit" {
        let trace = run Behaviour.NeverResets refusedLast
        violatedIds trace |> Expect.contains "idle at the head is violated" "idle-at-the-head"
      }

      testPropertyWithConfig simConfig "over seeds, the twins are caught whenever a refused landing is followed by what exposes them" <| fun (seed: int) ->
        let scenario = fromSeed seed
        let refused (l: Landing) = l.Outcome <> Outcome.Good
        // The last landing refused or withdrawn leaves the worktree off the head under NeverResets.
        match List.tryLast scenario with
        | Some last when refused last ->
          violatedIds (run Behaviour.NeverResets scenario) |> List.contains "idle-at-the-head"
        | _ -> true
    ]
  ]
