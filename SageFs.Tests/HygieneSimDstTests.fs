/// Deterministic Simulation Testing for workspace hygiene: seeded worlds of worktrees, branches and orphaned
/// processes folded through the REAL classify, planner, confirmation and executor. Each twin reintroduces
/// one bug and the invariant that guards against it must catch it.
module SageFs.Tests.HygieneSimDstTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.HygieneSim

let private seeds = [ 1 .. 120 ]

/// Each behavior's traces are folded once and every invariant reads the same ones.
let private tracesFor =
  let cache = System.Collections.Concurrent.ConcurrentDictionary<Behavior, Lazy<Trace list>>()
  fun (behavior: Behavior) ->
    cache.GetOrAdd(behavior, fun b -> lazy (seeds |> List.map (scenarioOf >> trace b))).Value

let private violationsFor (behavior: Behavior) (invariant: Trace -> HygieneSimInvariants.Violation list) =
  tracesFor behavior
  |> List.map (fun t -> t.Scenario, invariant t)
  |> List.filter (fun (_, vs) -> not (List.isEmpty vs))

let private expectNone (label: string) (bad: (Scenario * HygieneSimInvariants.Violation list) list) =
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

let private expectCaught (label: string) (bad: (Scenario * HygieneSimInvariants.Violation list) list) =
  bad |> List.isEmpty |> Expect.isFalse label

[<Tests>]
let hygieneSimDstTests =
  testList "Workspace hygiene DST" [

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let a = trace Behavior.Real (scenarioOf 7)
      let b = trace Behavior.Real (scenarioOf 7)
      (a.Steps |> List.map (fun s -> s.After, s.PlanId)) |> Expect.equal "replaying a seed gives the identical trace" (b.Steps |> List.map (fun s -> s.After, s.PlanId))

    testList "the real tidy holds every invariant" [
      testCase "NEVER-REMOVE-IN-USE" <| fun _ ->
        violationsFor Behavior.Real HygieneSimInvariants.neverRemoveInUse |> expectNone "something in use was removed"

      testCase "NEVER-KILL-ANOTHER-PROCESS" <| fun _ ->
        violationsFor Behavior.Real HygieneSimInvariants.neverKillAnotherProcess |> expectNone "a reused pid was killed"

      testCase "NEVER-LOSE-UNMERGED-COMMITS" <| fun _ ->
        violationsFor Behavior.Real HygieneSimInvariants.neverLoseUnmergedCommits |> expectNone "a commit lost its branch"

      testCase "NEVER-LOSE-REAL-DIRTY-WORK" <| fun _ ->
        violationsFor Behavior.Real HygieneSimInvariants.neverLoseRealDirtyWork |> expectNone "uncommitted work was removed"

      testCase "PLAN-IS-DRY-UNLESS-CONFIRMED" <| fun _ ->
        violationsFor Behavior.Real HygieneSimInvariants.planIsDryUnlessConfirmed |> expectNone "planning or a stale plan changed the world"

      testCase "EXECUTE-IS-IDEMPOTENT" <| fun _ ->
        violationsFor Behavior.Real HygieneSimInvariants.executeIsIdempotent |> expectNone "a second run did something"
    ]

    testList "the seeds reach the interesting cases, so a green run means something" [
      let traces () = tracesFor Behavior.Real

      testCase "plenty of worktrees, branches and processes get removed" <| fun _ ->
        HygieneSimInvariants.removalsHappened (traces ()) |> fun n -> (n > 500) |> Expect.isTrue (sprintf "only %d removals" n)

      testCase "some seeds see a worktree become busy between looking and acting" <| fun _ ->
        traces ()
        |> List.exists (fun t ->
          t.Steps |> List.exists (fun s ->
            match s.Op with
            | Op.Execute late -> late |> List.exists (function Op.BecomeBusy _ -> true | _ -> false)
            | _ -> false))
        |> Expect.isTrue "a late BecomeBusy exists"

      testCase "some seeds hold work that must not go: real dirt and unmerged commits in a worktree" <| fun _ ->
        traces ()
        |> List.exists (fun t ->
          t.Steps |> List.exists (fun s -> s.After.Worktrees |> Map.exists (fun _ w -> w.Dirt = Dirt.RealDirt)))
        |> Expect.isTrue "real dirt exists somewhere"

      testCase "some seeds run a second Execute that finds everything already gone" <| fun _ ->
        traces ()
        |> List.exists (fun t ->
          t.Steps |> List.pairwise |> List.exists (fun (a, b) ->
            match a.Op, b.Op with
            | Op.Execute [], Op.ExecuteAgain -> not (List.isEmpty a.Removals)
            | _ -> false))
        |> Expect.isTrue "an idempotence pair with a removal in the first run exists"
    ]

    testCase "TWIN: believing the plan instead of looking again removes something that became busy" <| fun _ ->
      violationsFor Behavior.TrustsThePlan HygieneSimInvariants.neverRemoveInUse
      |> expectCaught "NEVER-REMOVE-IN-USE catches a plan trusted over a second look"

    testCase "TWIN: deleting branches with -D unchecked loses commits" <| fun _ ->
      violationsFor Behavior.ForcesEverything HygieneSimInvariants.neverLoseUnmergedCommits
      |> expectCaught "NEVER-LOSE-UNMERGED-COMMITS catches an unchecked -D"

    testCase "TWIN: forcing every worktree removal removes real dirty work" <| fun _ ->
      violationsFor Behavior.ForcesEverything HygieneSimInvariants.neverLoseRealDirtyWork
      |> expectCaught "NEVER-LOSE-REAL-DIRTY-WORK catches a forced removal"

    testCase "TWIN: killing by pid alone kills a process that reused the pid" <| fun _ ->
      violationsFor Behavior.KillsByPid HygieneSimInvariants.neverKillAnotherProcess
      |> expectCaught "NEVER-KILL-ANOTHER-PROCESS catches a kill by pid"

    testCase "TWIN: a planner that also performs the Safe steps is not dry" <| fun _ ->
      violationsFor Behavior.PlanningMutates HygieneSimInvariants.planIsDryUnlessConfirmed
      |> expectCaught "PLAN-IS-DRY catches planning with side effects"

    testCase "TWIN: counting a removal that is already gone makes the second run do something" <| fun _ ->
      violationsFor Behavior.CountsGoneTwice HygieneSimInvariants.executeIsIdempotent
      |> expectCaught "EXECUTE-IS-IDEMPOTENT catches a second run that reclaims again"
  ]
