/// Deterministic Simulation Testing for the hygiene sweep: seeded worlds of agent worktrees run through the REAL ledger gate, the
/// automatic plan and the executor. The invariant the plan names is that the sweep never loses a commit or an uncommitted byte.
/// Each twin reintroduces one bug, and the invariant that guards against it must catch it.
module SageFs.Tests.HygieneSweepSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.HygieneSweepSim

let private seeds = [ 1 .. 120 ]

/// Each behavior's traces are folded once and every invariant reads the same ones.
let private tracesFor =
  let cache = System.Collections.Concurrent.ConcurrentDictionary<Behavior, Lazy<Trace list>>()
  fun (behavior: Behavior) ->
    cache.GetOrAdd(behavior, fun b -> lazy (seeds |> List.map (scenarioOf >> trace b))).Value

let private violationsFor (behavior: Behavior) (invariant: Trace -> Violation list) =
  tracesFor behavior
  |> List.map (fun t -> t.Scenario, invariant t)
  |> List.filter (fun (_, vs) -> not (List.isEmpty vs))

let private expectNone (label: string) (bad: (Scenario * Violation list) list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad
       |> List.truncate 3
       |> List.map (fun (s, vs) ->
         sprintf "seed=%d\n  %s" s.Seed
           (vs |> List.truncate 3 |> List.map (fun v -> sprintf "[%d] %s" v.Index v.Why) |> String.concat "\n  "))
       |> String.concat "\n")

let private expectCaught (label: string) (bad: (Scenario * Violation list) list) =
  bad |> List.isEmpty |> Expect.isFalse label

[<Tests>]
let hygieneSweepSimTests =
  testList "Workspace hygiene sweep DST" [

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let a = trace Behavior.Real (scenarioOf 7)
      let b = trace Behavior.Real (scenarioOf 7)
      (a.Steps |> List.map (fun s -> s.After, s.Removals)) |> Expect.equal "replaying a seed gives the identical trace" (b.Steps |> List.map (fun s -> s.After, s.Removals))

    testList "the real sweep holds every invariant" [
      testCase "NEVER-LOSE-A-COMMIT" <| fun _ ->
        violationsFor Behavior.Real neverLoseACommit |> expectNone "a commit became reachable from nothing"

      testCase "NEVER-LOSE-AN-UNCOMMITTED-BYTE" <| fun _ ->
        violationsFor Behavior.Real neverLoseAnUncommittedByte |> expectNone "uncommitted work was lost"

      testCase "A-WORKTREE-IS-TIDIED-ONLY-AFTER-THE-LEDGER-CLEARED-IT" <| fun _ ->
        violationsFor Behavior.Real removedOnlyAfterTheLedgerClearedIt |> expectNone "a worktree went before its grace was up in the ledger"

      testCase "NEVER-REMOVE-IN-USE" <| fun _ ->
        violationsFor Behavior.Real neverRemoveInUse |> expectNone "a worktree with a session in it was removed"

      testCase "NEVER-LOSE-AN-IGNORED-FILE" <| fun _ ->
        violationsFor Behavior.Real neverLoseAnIgnoredFile |> expectNone "an ignored file that no rule regenerates was lost"

      testCase "NEVER-OVERRIDE-A-PERSONS-LOCK" <| fun _ ->
        violationsFor Behavior.Real neverOverrideAPersonsLock |> expectNone "a lock a person made was released and the worktree removed"
    ]

    testList "the seeds reach the interesting cases, so a green run means something" [
      testCase "plenty of worktrees get removed after their grace" <| fun _ ->
        removalsHappened (tracesFor Behavior.Real) |> fun n -> (n > 50) |> Expect.isTrue (sprintf "only %d removals" n)

      testCase "some seeds salvage uncommitted work, and some of it is in a worktree that is then left alone" <| fun _ ->
        tracesFor Behavior.Real
        |> List.exists (fun t -> t.Steps |> List.exists (fun s -> not (Map.isEmpty s.After.Salvaged)))
        |> Expect.isTrue "a salvage branch exists somewhere"

      testCase "some seeds merge by patch or content, so a restore point is what keeps the commit" <| fun _ ->
        tracesFor Behavior.Real
        |> List.exists (fun t -> t.Steps |> List.exists (fun s -> not (Set.isEmpty s.After.Restore)))
        |> Expect.isTrue "a restore point exists somewhere"

      testCase "some seeds put a person's lock on a worktree, and some put an ignored file in one" <| fun _ ->
        let any (pick: World -> bool) = tracesFor Behavior.Real |> List.exists (fun t -> t.Steps |> List.exists (fun s -> pick s.After))
        any (fun w -> w.Worktrees |> Map.exists (fun _ wt -> wt.Lock = Lock.PersonNoPid || wt.Lock = Lock.PersonPidLike))
        |> Expect.isTrue "a person's lock exists somewhere"
        any (fun w -> not (Set.isEmpty w.IgnoredMade)) |> Expect.isTrue "an ignored file exists somewhere"
    ]

    testCase "TWIN: tidying a worktree with uncommitted work, with no salvage first, loses bytes" <| fun _ ->
      violationsFor Behavior.ForcesAndSkipsSalvage neverLoseAnUncommittedByte
      |> expectCaught "NEVER-LOSE-AN-UNCOMMITTED-BYTE catches a forced removal with no salvage"

    testCase "TWIN: deleting a branch merged by patch or content with no restore point loses commits" <| fun _ ->
      violationsFor Behavior.NoRestorePoint neverLoseACommit
      |> expectCaught "NEVER-LOSE-A-COMMIT catches a branch dropped with no restore ref"

    testCase "TWIN: believing git's age and ignoring the ledger tidies a worktree the ledger never saw" <| fun _ ->
      violationsFor Behavior.IgnoresLedger removedOnlyAfterTheLedgerClearedIt
      |> expectCaught "the ledger rule catches a worktree tidied on its first sweep"

    testCase "TWIN: reading every lock without a live pid as stale releases a person's lock" <| fun _ ->
      violationsFor Behavior.UnlocksAnyLock neverOverrideAPersonsLock
      |> expectCaught "NEVER-OVERRIDE-A-PERSONS-LOCK catches the old unlock-anything rule"

    testCase "TWIN: not looking at ignored files removes a worktree with a .env unsaved" <| fun _ ->
      violationsFor Behavior.RemovesIgnoredFiles neverLoseAnIgnoredFile
      |> expectCaught "NEVER-LOSE-AN-IGNORED-FILE catches a removal that deletes an ignored file"
  ]
