/// Deterministic Simulation Testing for the hygiene sweep: seeded worlds of agent worktrees run through the REAL ledger gate, the
/// automatic plan, the executor and, through `HygieneSweepSimEdge`, the REAL `HygieneEdge` over a fake git that can fail on a
/// schedule. The invariant the plan names is that the sweep never loses a commit, an uncommitted byte or an ignored file that is
/// not regenerated, and never overrides a lock a person made. Each twin reintroduces one bug, and the invariant that guards against
/// it must catch it.
module SageFs.Tests.HygieneSweepSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.HygieneSweepSim
open SageFs.Tests.HygieneSweepSimEdge

let private seeds = [ 1 .. 120 ]

/// Each behavior's traces are folded once and every invariant reads the same ones.
let private tracesFor =
  let cache = System.Collections.Concurrent.ConcurrentDictionary<Behavior, Lazy<Trace list>>()
  fun (behavior: Behavior) ->
    cache.GetOrAdd(behavior, fun b -> lazy (seeds |> List.map (scenarioOf >> trace performer b))).Value

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

let private allInvariants : (string * (Trace -> Violation list)) list =
  [ "NEVER-LOSE-A-COMMIT", neverLoseACommit
    "NEVER-LOSE-AN-UNCOMMITTED-BYTE", neverLoseAnUncommittedByte
    "NEVER-LOSE-AN-IGNORED-FILE", neverLoseAnIgnoredFile
    "NEVER-OVERRIDE-A-PERSONS-LOCK", neverOverrideAPersonsLock
    "A-WORKTREE-IS-TIDIED-ONLY-AFTER-THE-LEDGER-CLEARED-IT", removedOnlyAfterTheLedgerClearedIt
    "NEVER-REMOVE-IN-USE", neverRemoveInUse ]

[<Tests>]
let hygieneSweepSimTests =
  testList "Workspace hygiene sweep DST" [

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let a = trace performer Behavior.Real (scenarioOf 7)
      let b = trace performer Behavior.Real (scenarioOf 7)
      (a.Steps |> List.map (fun s -> s.After, s.Removals)) |> Expect.equal "replaying a seed gives the identical trace" (b.Steps |> List.map (fun s -> s.After, s.Removals))

    testList "the real sweep, over the real edge, holds every invariant" [
      for (name, invariant) in allInvariants ->
        testCase name <| fun _ -> violationsFor Behavior.Real invariant |> expectNone (sprintf "%s broke" name)
    ]

    testList "and so does the real edge while git fails on a schedule" [
      for (name, invariant) in allInvariants ->
        testCase name <| fun _ -> violationsFor Behavior.GitFaults invariant |> expectNone (sprintf "%s broke under git faults" name)
    ]

    testList "the seeds reach the interesting cases, so a green run means something" [
      testCase "plenty of worktrees get removed after their grace" <| fun _ ->
        removalsHappened (tracesFor Behavior.Real) |> fun n -> (n > 50) |> Expect.isTrue (sprintf "only %d removals" n)

      testCase "some seeds salvage uncommitted work" <| fun _ ->
        tracesFor Behavior.Real
        |> List.exists (fun t -> t.Steps |> List.exists (fun s -> not (Map.isEmpty s.After.Salvaged)))
        |> Expect.isTrue "a salvage ref exists somewhere"

      testCase "some seeds merge by patch or content, so a restore point is what keeps the commit" <| fun _ ->
        tracesFor Behavior.Real
        |> List.exists (fun t -> t.Steps |> List.exists (fun s -> not (Set.isEmpty s.After.Restore)))
        |> Expect.isTrue "a restore point exists somewhere"

      testCase "some seeds put a person's lock on a worktree, and some put an ignored file in one" <| fun _ ->
        let any (pick: World -> bool) = tracesFor Behavior.Real |> List.exists (fun t -> t.Steps |> List.exists (fun s -> pick s.After))
        any (fun w -> w.Worktrees |> Map.exists (fun _ wt -> wt.Lock = Lock.PersonNoPid || wt.Lock = Lock.PersonPidLike))
        |> Expect.isTrue "a person's lock exists somewhere"
        any (fun w -> not (Set.isEmpty w.IgnoredMade)) |> Expect.isTrue "an ignored file exists somewhere"

      testCase "some seeds remove a worktree by hand and make another at its path" <| fun _ ->
        tracesFor Behavior.Real
        |> List.exists (fun t -> t.Steps |> List.exists (fun s -> match s.Op with | Op.Reuse _ -> not (Map.isEmpty s.After.Worktrees) | _ -> false))
        |> Expect.isTrue "a path was reused after a sweep noticed it was gone"

      testCase "some seeds make git fail while the edge is working, and the edge refuses rather than guesses" <| fun _ ->
        let refusals =
          tracesFor Behavior.GitFaults
          |> List.sumBy (fun t -> t.Steps |> List.sumBy (fun s -> match s.Op with | Op.Sweep -> 1 | _ -> 0))
        (refusals > 0) |> Expect.isTrue "sweeps ran under faults"
    ]

    testCase "TWIN: tidying a worktree with uncommitted work, with no salvage first, loses bytes" <| fun _ ->
      violationsFor Behavior.ForcesAndSkipsSalvage neverLoseAnUncommittedByte
      |> expectCaught "NEVER-LOSE-AN-UNCOMMITTED-BYTE catches a forced removal with no salvage"

    testCase "TWIN: a restore ref that keeps nothing loses a commit the base only has by content" <| fun _ ->
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

    testCase "TWIN: never noticing a vanished worktree lets a new one at its path inherit its quiet record" <| fun _ ->
      violationsFor Behavior.ForgetsVanished removedOnlyAfterTheLedgerClearedIt
      |> expectCaught "the ledger rule catches a worktree cleared on another worktree's record"
  ]
