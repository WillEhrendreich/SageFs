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

[<Tests>]
let ledgerGateTests =
  let grace = HygieneAges.oneDay
  let path = worktreeRoot + "/a"
  let record (activity: SageFs.HygieneLedger.RecordedActivity) : SageFs.HygieneLedger.WorktreeRecord =
    { Path = path
      Branch = BranchLabel.NoBranch
      Tip = "abc"
      Maker = SageFs.HygieneLedger.Maker.MakerUnrecorded
      FirstSeen = now - HygieneAges.days 3.0
      Activity = activity
      Salvage = SageFs.HygieneLedger.SalvageState.NotSalvaged
      Presence = SageFs.HygieneLedger.Presence.OnDisk }
  let known (activity: SageFs.HygieneLedger.RecordedActivity) = Map.ofList [ path, record activity ]
  let merged () = classify now (worktree "a")
  let standingOf (l: Leftover) = (Leftover.entry l).Standing

  testList "Workspace hygiene: agent worktrees need a ledger entry before the daemon tidies them" [

    testCase "a worktree the ledger has never seen is held for a full grace from now, however merged git says it is" <| fun _ ->
      match SageFs.HygieneLedger.Ledger.holdBack grace now Map.empty [ merged () ] with
      | [ held ] ->
        match standingOf held with
        | Standing.WithinRetention until -> until |> Expect.equal "kept until now plus the grace" (now + grace)
        | other -> failtestf "expected WithinRetention, got %A" other
      | other -> failtestf "expected one leftover, got %A" other

    testCase "a worktree in use at the last sweep is held, a quiet one is held until the grace since it went quiet, then cleared" <| fun _ ->
      let wentQuiet = now - HygieneAges.anHour * 5.0
      let still = SageFs.HygieneLedger.Ledger.holdBack grace now (known SageFs.HygieneLedger.RecordedActivity.RecordedInUse) [ merged () ]
      standingOf still.Head |> function | Standing.WithinRetention _ -> () | other -> failtestf "in use: expected held, got %A" other
      match SageFs.HygieneLedger.Ledger.holdBack grace now (known (SageFs.HygieneLedger.RecordedActivity.RecordedQuietSince wentQuiet)) [ merged () ] |> List.head |> standingOf with
      | Standing.WithinRetention until -> until |> Expect.equal "until the grace after it went quiet" (wentQuiet + grace)
      | other -> failtestf "expected held, got %A" other
      let long = now - HygieneAges.anHour * 30.0
      standingOf (SageFs.HygieneLedger.Ledger.holdBack grace now (known (SageFs.HygieneLedger.RecordedActivity.RecordedQuietSince long)) [ merged () ]).Head
      |> Expect.equal "cleared after the whole grace" (Standing.Merged MergeHow.Ancestor)

    testCase "no grace means a recorded quiet worktree is cleared at once, and an unrecorded one waits for the next sweep" <| fun _ ->
      standingOf (SageFs.HygieneLedger.Ledger.holdBack TimeSpan.Zero now (known (SageFs.HygieneLedger.RecordedActivity.RecordedQuietSince now)) [ merged () ]).Head
      |> Expect.equal "cleared" (Standing.Merged MergeHow.Ancestor)
      match standingOf (SageFs.HygieneLedger.Ledger.holdBack TimeSpan.Zero now Map.empty [ merged () ]).Head with
      | Standing.WithinRetention _ -> ()
      | other -> failtestf "expected held, got %A" other

    testPropertyWithConfig cfg "PROPERTY: the ledger gate never makes a leftover more reclaimable, and changes nothing but merged agent worktrees" <| fun (subjects: Subject list) ->
      let leftovers = subjects |> List.map (classify now)
      let held = SageFs.HygieneLedger.Ledger.holdBack grace now Map.empty leftovers
      List.length held = List.length leftovers
      && List.zip leftovers held
         |> List.forall (fun (before, after) ->
           let b = Leftover.entry before
           let a = Leftover.entry after
           match Standing.reclaimability a.Standing, Standing.reclaimability b.Standing with
           | Reclaimability.Reclaimable, Reclaimability.Reclaimable -> true
           | Reclaimability.Reclaimable, _ -> false
           | _ -> true)
  ]
