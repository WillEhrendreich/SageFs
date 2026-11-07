/// Step 6 of docs/plans/hygiene-that-runs-itself.md: what the daemon may tidy by itself. The list of kinds is closed, and
/// nothing that holds work, is in use, or could not be judged is ever one of the steps an automatic sweep runs.
module SageFs.Tests.HygieneAutoTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.WorkspaceHygiene
open SageFs.Tests.HygieneFixtures

type private ArbHolder =
  static member Subject() = arbSubject

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 300; arbitrary = [ typeof<ArbHolder> ] }

[<Tests>]
let autoTests =
  testList "Workspace hygiene: what runs by itself" [

    testCase "the automatic kinds are exactly worktrees, merged branches, worker logs, temp runs and registry entries" <| fun _ ->
      SageFs.HygieneAuto.kinds |> List.sortBy Kind.describe
      |> Expect.equal "the closed list"
           ([ LeftoverKind.AgentWorktree; LeftoverKind.StaleBranch; LeftoverKind.WorkerLogFile; LeftoverKind.TempRunDir; LeftoverKind.SpawnedRegistryEntry ] |> List.sortBy Kind.describe)

    testCase "a stray process, a gate checkout, a gate pass record and a host cache entry are never automatic" <| fun _ ->
      for kind in [ LeftoverKind.OrphanProcess; LeftoverKind.GateCheckout; LeftoverKind.GateTierClone; LeftoverKind.GatePassRecord; LeftoverKind.HostCacheEntry ] do
        SageFs.HygieneAuto.isAutomatic kind |> Expect.isFalse (sprintf "%A is not tidied by the daemon on its own" kind)

    testCase "a worktree that holds work is in the plan the sweep makes, but is not a step it runs" <| fun _ ->
      let held = classify now (worktree "held" |> unmergedWith [])
      let dirty = classify now (worktree "dirty" |> mergedWith MergeHow.Ancestor [ realFile "src/a.fs" ])
      let merged = classify now (worktree "merged")
      let plan = SageFs.HygieneAuto.plan [ held; dirty; merged ]
      plan.Steps |> List.filter SageFs.HygieneAuto.runs |> List.map (fun s -> Leftover.target s.Target)
      |> Expect.equal "only the merged one runs" [ Target.Directory(worktreeRoot + "/merged") ]

    testPropertyWithConfig cfg "PROPERTY: a step the sweep runs is Safe, of an automatic kind, and never holds work or is in use" <| fun (subjects: Subject list) ->
      let plan = subjects |> List.map (classify now) |> SageFs.HygieneAuto.plan
      plan.Steps
      |> List.filter SageFs.HygieneAuto.runs
      |> List.forall (fun step ->
        let standing = (Leftover.entry step.Target).Standing
        step.Risk = Risk.Safe
        && SageFs.HygieneAuto.isAutomatic (Leftover.kind step.Target)
        && (match standing with
            | Standing.CleanButUnmerged _
            | Standing.DirtyReal _
            | Standing.InUse _
            | Standing.Unknown _
            | Standing.WithinRetention _
            | Standing.Newest _ -> false
            | _ -> true))
  ]
