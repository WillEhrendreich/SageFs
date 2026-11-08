module SageFs.Tests.IntegrationReplaceSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.IntegrationReplaceSim

/// DST of `set_integration_ref` against a landing queue and two repositories (P16, roast critical 2). See
/// SageFs.Simulation/IntegrationReplaceSim.fs: the daemon's own decisions (`CohortIntegrationScope`) run against a model of
/// git's worktree registry, the directories and the verifying session, and every invariant is broken by a twin that carries
/// the pre-fix behaviour.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (trace: Trace) =
  match violations trace with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION (%A) - replay:\n  Events=%A\n  Violations=%A" trace.Behaviour trace.Events vs

let private violatedIds (trace: Trace) = violations trace |> List.map fst

[<Tests>]
let tests =
  testList "DST replacing the integration worktree (P16)" [

    testList "the real decisions hold the invariants" [
      testPropertyWithConfig simConfig "seeded event sequences: a verification keeps its sources, a refusal changes nothing, no old worktree is left, no path is shared" <| fun (seed: int) ->
        assertHolds (run Behaviour.Real (fromSeed seed))

      test "a retry during a verification is refused, and the landing lands" {
        let trace = run Behaviour.Real retryDuringVerification
        assertHolds trace
        trace.Final.Landed |> List.length |> Expect.equal "the landing landed" 1
        trace.Final.Calls |> List.exists (fun c -> c.Reply = Reply.Refused) |> Expect.isTrue "the second call was refused"
      }

      test "moving to the other repository removes the first worktree from the first repository" {
        let trace = run Behaviour.Real moveToTheOtherRepository
        assertHolds trace
        trace.Final.Calls |> List.map (fun c -> c.Reply) |> Expect.equal "both calls replaced" [ Reply.Replaced; Reply.Replaced ]
        trace.Final.Registry |> Map.count |> Expect.equal "only B's worktree is registered" 1
      }

      test "there and back leaves one worktree per call and no shared path" {
        assertHolds (run Behaviour.Real thereAndBack)
      }
    ]

    testList "the twins break them, so the invariants have teeth" [
      test "a call that ignores the queue replaces the worktree under a verification" {
        let trace = run Behaviour.ReplacesDuringVerification retryDuringVerification
        violatedIds trace |> Expect.contains "verification loses its sources" "verification-keeps-its-sources"
      }

      test "a call that stops the session before it reads the queue is refused with the world already changed" {
        let trace = run Behaviour.StopsTheSessionBeforeRefusing retryDuringVerification
        violatedIds trace |> Expect.contains "the refusal changed the world" "refusal-changes-nothing"
        violatedIds trace |> Expect.contains "and the verification lost its session" "verification-keeps-its-sources"
      }

      test "git run in the new repository leaves the old worktree and nothing reads the answer" {
        let trace = run Behaviour.RemovesFromTheWrongRepository moveToTheOtherRepository
        violatedIds trace |> Expect.contains "the old worktree is still there" "replacement-leaves-no-old-worktree"
      }

      test "one fixed path serves both repositories" {
        let trace = run Behaviour.SharedPath moveToTheOtherRepository
        violatedIds trace |> Expect.contains "the path served two repositories" "two-repositories-never-share-a-path"
      }

    ]
  ]
