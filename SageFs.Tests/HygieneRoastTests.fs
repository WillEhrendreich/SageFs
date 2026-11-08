/// What the roast of 2026-11-08 found in the hygiene sweep besides the lock and ignored-file defects (HygieneLockAndIgnoredTests):
/// the edge reads "git could not decide" as "not merged" and keeps no restore point, the ledger never notices a worktree that was
/// removed by hand, and salvage keeps its work on a branch that `git push --all` would publish. Real git in throwaway
/// repositories.
module SageFs.Tests.HygieneRoastTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.HygieneSweep
open SageFs.Tests.HygieneSandbox

let private contextFor (sb: Sandbox) (live: unit -> LiveFacts) : SweepContext =
  { MakeScan = fun () -> { sb.ScanWith((fun _ _ -> false), [], live ()) with Loc = { sb.Locations with WorktreeGrace = TimeSpan.Zero } }
    Git = runGit
    GitEnv = runGitEnv
    IsAlive = fun _ _ -> false
    AutoTidy = fun () -> On }

/// Git that fails the commands that decide whether a commit has reached the base, as a git that is out of memory or timed out would.
let private gitThatCannotDecide : Git =
  fun dir args ->
    match args with
    | "merge-base" :: _
    | "cherry" :: _
    | "diff" :: "--quiet" :: _ -> Task.FromResult(GitResult.Exit(128, "fatal: injected failure"))
    | _ -> runGit dir args

[<Tests>]
let failOpenTests =
  testList "Workspace hygiene: the edge never lets go of a commit it could not account for" [

    testTask "when git cannot say whether a rebased worktree has reached the base, the removal is refused and the commits stay reachable" {
      use sb = new Sandbox()
      let paths = populate sb
      let path = paths.["squashed"]
      let tip = (git path [ "rev-parse"; "HEAD" ]).Trim()
      let alive : int -> int64 option -> bool = fun _ _ -> false
      let ctx : HygieneEdge.EdgeContext = { MakeScan = (fun () -> sb.Scan()); Git = gitThatCannotDecide; IsAlive = alive }
      let accepted =
        match Guard.accept sb.Roots HygieneEdge.resolvePath path with
        | Result.Ok a -> a
        | Result.Error refusal -> failtestf "the path gate refused: %A" refusal
      let! outcome = HygieneEdge.performWith HygieneEdge.noJournal ctx (Operation.RemoveWorktree(accepted, sb.Repo, ForceNeed.NoForce))
      let reachable =
        (match gitExit sb.Repo [ "branch"; "--contains"; tip ] with | 0, out, _ -> out.Contains "worktree-agent-squashed" | _ -> false)
        || (match gitExit sb.Repo [ "for-each-ref"; "--contains"; tip; "refs/sagefs/removed" ] with | 0, out, _ -> out.Trim() <> "" | _ -> false)
      let removed = not (Directory.Exists path)
      // Either the worktree stayed, or its tip is reachable from a ref: what must never happen is neither.
      (not removed || reachable) |> Expect.isTrue (sprintf "the worktree went and its tip is on no ref (outcome %A)" outcome)
      Directory.Exists path |> Expect.isTrue "the worktree is still there"
    }
  ]

[<Tests>]
let vanishedTests =
  testList "Workspace hygiene: a worktree removed by hand is forgotten by the ledger" [

    testTask "a new worktree at the path of one that vanished is unseen: it waits a sweep, it is not cleared on sight" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "reused"
      let busy () = { LiveFacts.none with Sessions = [ "s1", path ] }
      let idle () = LiveFacts.none
      let live = ref busy
      let context = contextFor sb (fun () -> live.Value ())
      // The ledger sees it in use, then quiet.
      let! _ = sweep context
      live.Value <- idle
      let! _ = sweep context
      // A person removes it by hand, and an agent makes another at the same path.
      git sb.Repo [ "worktree"; "remove"; "--force"; path ] |> ignore
      git sb.Repo [ "branch"; "-D"; "worktree-agent-reused" ] |> ignore
      // A sweep runs while it is gone, and the ledger has to notice.
      let! _ = sweep context
      let again = sb.AddWorktree "reused"
      let! _ = sweep context
      Directory.Exists again |> Expect.isTrue "the new worktree is not removed by the first sweep that sees it"
    }
  ]

[<Tests>]
let salvageRefTests =
  testList "Workspace hygiene: salvage does not publish what a worktree held" [

    testTask "uncommitted work is kept under refs/sagefs/salvage, which git push --all does not send, and not on a branch" {
      use sb = new Sandbox()
      let paths = populate sb
      let! _ = sweep (contextFor sb (fun () -> LiveFacts.none))
      (match gitExit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/heads/salvage/dirty-real" ] with | 0, _, _ -> true | _ -> false)
      |> Expect.isFalse "no branch under refs/heads/salvage"
      (match gitExit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/sagefs/salvage/dirty-real" ] with | 0, _, _ -> true | _ -> false)
      |> Expect.isTrue "the work is under refs/sagefs/salvage"
      File.Exists(Path.Combine(paths.["dirty-real"], "src", "precious.fs")) |> Expect.isTrue "the worktree is untouched"
    }
  ]

[<Tests>]
let moreEdgeTests =
  testList "Workspace hygiene: what the edge does when git cannot answer" [

    testTask "a branch git cannot place is not deleted" {
      use sb = new Sandbox()
      let paths = populate sb
      git sb.Repo [ "worktree"; "remove"; "--force"; paths.["squashed"] ] |> ignore
      let alive : int -> int64 option -> bool = fun _ _ -> false
      let ctx : HygieneEdge.EdgeContext = { MakeScan = (fun () -> sb.Scan()); Git = gitThatCannotDecide; IsAlive = alive }
      let! outcome = HygieneEdge.performWith HygieneEdge.noJournal ctx (Operation.DeleteBranch(sb.Repo, "worktree-agent-squashed", BranchDeletion.BecauseMergedBy MergeHow.PatchEquivalent))
      (match outcome with | Outcome.Failed _ -> true | Outcome.Done _ -> false) |> Expect.isTrue "refused"
      branchExists sb "worktree-agent-squashed" |> Expect.isTrue "the branch is still there"
    }

    testTask "merge state is Undecided when git cannot say, and a tip that cannot be read cannot be kept" {
      use sb = new Sandbox()
      let paths = populate sb
      let ctx : HygieneEdge.EdgeContext = { MakeScan = (fun () -> sb.Scan()); Git = gitThatCannotDecide; IsAlive = (fun _ _ -> false) }
      let tip = (git paths.["squashed"] [ "rev-parse"; "HEAD" ]).Trim()
      let! state = HygieneEdge.mergeStateAt ctx sb.Repo tip
      state |> Expect.equal "undecided, not not-merged" HygieneLedger.MergeState.Undecided
      let! kept = HygieneEdge.keepRestorePoint ctx sb.Repo "x" HygieneLedger.Tip.TipUnknown HygieneLedger.MergeState.Undecided
      (match kept with | Result.Error _ -> true | Result.Ok() -> false) |> Expect.isTrue "no tip, no removal"
    }
  ]

[<Tests>]
let salvageLifecycleTests =
  testList "Workspace hygiene: salvage refs are listed and reaped" [

    testTask "a salvage ref is listed, kept while its worktree is there, kept when the base lacks the work, and reaped once the base has it" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "keeper"
      write (Path.Combine(path, "src", "keeper.fs")) "module Keeper"
      let context = contextFor sb (fun () -> LiveFacts.none)
      let! _ = sweep context
      let! listing = HygieneSalvage.listRefs runGit sb.Repo
      (match listing with
       | SalvageRefs.Listing.Listed [ r ] -> r.Name
       | other -> failtestf "expected one salvage ref, got %A" other)
      |> Expect.equal "listed by worktree name" "keeper"
      // The worktree is still there: kept.
      let! _ = sweep context
      (match gitExit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/sagefs/salvage/keeper" ] with | 0, _, _ -> true | _ -> false)
      |> Expect.isTrue "kept while the worktree is there"
      // The worktree goes (by hand) and the base does NOT have the file: kept.
      git sb.Repo [ "worktree"; "remove"; "--force"; path ] |> ignore
      let! _ = sweep context
      (match gitExit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/sagefs/salvage/keeper" ] with | 0, _, _ -> true | _ -> false)
      |> Expect.isTrue "kept: the worktree is gone but the base lacks the work"
      // The base gets the same file: reaped.
      write (Path.Combine(sb.Repo, "src", "keeper.fs")) "module Keeper"
      commitAll sb.Repo "the work reached master by hand"
      let! report = sweep context
      (match gitExit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/sagefs/salvage/keeper" ] with | 0, _, _ -> true | _ -> false)
      |> Expect.isFalse "reaped once the base has everything on it"
      report.Reaped |> List.length |> Expect.equal "the reap is in the report" 1
      let reapedLine = HygieneLedgerStore.read sb.Locations.DataDir |> List.exists (fun l -> match l.Event with | HygieneLedger.LedgerEvent.SalvageReaped _ -> true | _ -> false)
      reapedLine |> Expect.isTrue "and in the ledger"
    }

    testCase "decide reaps only when the worktree is known to be gone, and the retention is the only other way out" <| fun _ ->
      let now = DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc)
      let r : SalvageRefs.SalvageRef = { Name = "x"; Ref = "refs/sagefs/salvage/x"; Sha = "abc"; At = now - HygieneAges.days 3.0 }
      let verdict w h age = SalvageRefs.decide now DataRetention.salvageRetention w h { r with At = now - age }
      let isReap v = (match v with | SalvageRefs.Verdict.Reap _ -> true | SalvageRefs.Verdict.Keep _ -> false)
      for holds in [ SalvageRefs.BaseHolds.EverythingOnIt; SalvageRefs.BaseHolds.NotEverything; SalvageRefs.BaseHolds.CouldNotTell ] do
        for age in [ HygieneAges.days 3.0; HygieneAges.ancient ] do
          verdict SalvageRefs.WorktreeOfIt.StillThere holds age |> isReap |> Expect.isFalse "a worktree that is there keeps it"
          verdict SalvageRefs.WorktreeOfIt.NotListed holds age |> isReap |> Expect.isFalse "an unlisted worktree keeps it"
      verdict SalvageRefs.WorktreeOfIt.Gone SalvageRefs.BaseHolds.EverythingOnIt (HygieneAges.days 3.0) |> isReap |> Expect.isTrue "gone and held by the base"
      verdict SalvageRefs.WorktreeOfIt.Gone SalvageRefs.BaseHolds.NotEverything (HygieneAges.days 3.0) |> isReap |> Expect.isFalse "gone, not held, young"
      verdict SalvageRefs.WorktreeOfIt.Gone SalvageRefs.BaseHolds.CouldNotTell HygieneAges.ancient |> isReap |> Expect.isTrue "gone and past the retention"
  ]

[<Tests>]
let sweepResilienceTests =
  testList "Workspace hygiene: a pass that fails says so and the rest go on" [

    testTask "one repository whose pass throws is reported with its exception and the others still run" {
      let sweepOne (repo: string) : Task<SweepReport> =
        task {
          match repo with
          | "bad" -> return failwith "this repository's pass blew up"
          | other -> return { Repo = other; LinesWritten = 0; Salvage = []; Tidy = TidyHalf.SwitchedOff; Reaped = []; LedgerFailures = [] }
        }
      let! results = sweepRepositories [ "first"; "bad"; "last" ] sweepOne
      results |> List.map fst |> Expect.equal "every repository was tried, in order" [ "first"; "bad"; "last" ]
      (match results |> List.find (fun (r, _) -> r = "bad") |> snd with
       | Result.Error ex -> ex.ToString()
       | Result.Ok _ -> failtest "the bad one should have failed")
      |> Expect.stringContains "the whole exception, not only its message" "this repository's pass blew up"
      (results |> List.filter (fun (_, r) -> match r with | Result.Ok _ -> true | Result.Error _ -> false) |> List.length) |> Expect.equal "the others ran" 2
    }

    testTask "a ledger that cannot be written is in the report, and the worktree is not tidied on a ledger it could not keep" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "unwritable"
      // The data dir is a file, so no line can be appended.
      Directory.CreateDirectory(Path.GetDirectoryName sb.Locations.DataDir) |> ignore
      File.WriteAllText(sb.Locations.DataDir, "not a directory")
      let context = contextFor sb (fun () -> LiveFacts.none)
      let! first = sweep context
      List.isEmpty first.LedgerFailures |> Expect.isFalse "the lost lines are said"
      let! second = sweep context
      List.isEmpty second.LedgerFailures |> Expect.isFalse "and again"
      Directory.Exists path |> Expect.isTrue "a worktree the ledger never recorded is not tidied"
    }
  ]
