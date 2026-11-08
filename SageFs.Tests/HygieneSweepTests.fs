/// The sweep (SageFs/HygieneSweep.fs) against real git in throwaway repositories: it observes, it saves uncommitted work to a
/// salvage branch, and it tidies only what the ledger has cleared. Agent worktrees are the exception that needs a ledger entry,
/// so the first sweep of a worktree removes nothing, whatever git says about it.
module SageFs.Tests.HygieneSweepTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.HygieneLedger
open SageFs.HygieneSweep
open SageFs.Tests.HygieneSandbox

let private contextFor (sb: Sandbox) (grace: TimeSpan) (autoTidy: Toggle) : SweepContext =
  { MakeScan = fun () -> { sb.Scan() with Loc = { sb.Locations with WorktreeGrace = grace } }
    Git = runGit
    GitEnv = runGitEnv
    IsAlive = fun _ _ -> false
    AutoTidy = fun () -> autoTidy }

let private sweepOnce (sb: Sandbox) (grace: TimeSpan) (autoTidy: Toggle) : Task<SweepReport> = sweep (contextFor sb grace autoTidy)

let private ledgerOf (sb: Sandbox) : LedgerLine list = HygieneLedgerStore.read sb.Locations.DataDir

let private removedTargets (lines: LedgerLine list) : string list =
  lines |> List.choose (fun l -> match l.Event with | LedgerEvent.Removed r -> Some r.Target | _ -> None)

let private refExists (sb: Sandbox) (name: string) : bool =
  match gitExit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; name ] with
  | 0, _, _ -> true
  | _ -> false

let private noGrace = TimeSpan.Zero

[<Tests>]
let sweepTests =
  testList "Workspace hygiene: the sweep" [

    testTask "the first sweep of a worktree records it and removes nothing, however merged git says it is" {
      use sb = new Sandbox()
      let paths = populate sb
      let! report = sweepOnce sb noGrace On
      for KeyValue(name, path) in paths do
        Directory.Exists path |> Expect.isTrue (sprintf "%s is still there after its first sweep" name)
      let firstSeen = ledgerOf sb |> List.choose (fun l -> match l.Event with | LedgerEvent.FirstSeen(p, _, _, _) -> Some p | _ -> None)
      firstSeen |> List.length |> Expect.equal "every agent worktree has a ledger entry now" (Map.count paths)
      removedTargets (ledgerOf sb) |> Expect.isEmpty "nothing was removed"
      (report.LinesWritten > 0) |> Expect.isTrue "it wrote lines"
    }

    testTask "the second sweep takes the merged worktrees, keeps the ones that hold work, and writes each removal with its tip" {
      use sb = new Sandbox()
      let paths = populate sb
      let tipOfSquashed = (git paths.["squashed"] [ "rev-parse"; "HEAD" ]).Trim()
      let! _ = sweepOnce sb noGrace On
      let! second = sweepOnce sb noGrace On
      for name in [ "clean-merged"; "ff-merged"; "squashed"; "dirty-generated" ] do
        Directory.Exists paths.[name] |> Expect.isFalse (sprintf "%s was merged and quiet, so it is gone" name)
      for name in [ "unmerged"; "dirty-real" ] do
        Directory.Exists paths.[name] |> Expect.isTrue (sprintf "%s holds work and stays" name)
      File.ReadAllText(Path.Combine(paths.["dirty-real"], "src", "precious.fs")) |> Expect.equal "uncommitted bytes intact" "module Precious"
      branchExists sb "worktree-agent-unmerged" |> Expect.isTrue "the unmerged branch stays"
      branchExists sb "worktree-agent-clean-merged" |> Expect.isFalse "a merged branch goes with its worktree"
      // The squash-merged commit is not an ancestor of master, so its tip is kept by a ref of ours.
      let kept = (git sb.Repo [ "rev-parse"; "refs/sagefs/removed/worktree-agent-squashed" ]).Trim()
      kept |> Expect.equal "the restore point is the removed tip" tipOfSquashed
      let removals = ledgerOf sb |> List.choose (fun l -> match l.Event with | LedgerEvent.Removed r -> Some r | _ -> None)
      let squashedLine = removals |> List.find (fun r -> r.Target = Guard.normalize paths.["squashed"])
      squashedLine.Tip |> Expect.equal "the line says where the tip was" (Tip.AtTip tipOfSquashed)
      squashedLine.Merged |> Expect.equal "and how it was merged" (MergeState.MergedBy MergeHow.PatchEquivalent)
      let removed = (HygieneService.summarizeReport (match second.Tidy with | TidyHalf.Ran r -> r | TidyHalf.SwitchedOff -> failtest "tidy was on")).Removed
      (removed > 0) |> Expect.isTrue "it removed things"
    }

    testTask "a worktree's uncommitted files are saved to salvage/<name> on every sweep that finds them, once per change, and never removed" {
      use sb = new Sandbox()
      let paths = populate sb
      let wt = paths.["dirty-real"]
      let! first = sweepOnce sb noGrace On
      first.Salvage |> List.length |> Expect.equal "the first sweep saves it" 1
      let tip = (git sb.Repo [ "rev-parse"; "refs/sagefs/salvage/dirty-real" ]).Trim()
      (git sb.Repo [ "show"; tip + ":src/precious.fs" ]) |> Expect.equal "the file is in the commit" "module Precious"
      let! second = sweepOnce sb noGrace On
      second.Salvage |> Expect.isEmpty "nothing changed, so no second commit"
      (git sb.Repo [ "rev-parse"; "refs/sagefs/salvage/dirty-real" ]).Trim() |> Expect.equal "the ref did not move" tip
      write (Path.Combine(wt, "src", "precious.fs")) "module Precious2"
      let! third = sweepOnce sb noGrace On
      third.Salvage |> List.length |> Expect.equal "a changed file is saved again" 1
      File.ReadAllText(Path.Combine(wt, "src", "precious.fs")) |> Expect.equal "the worktree still has its own bytes" "module Precious2"
      let saved = ledgerOf sb |> List.filter (fun l -> match l.Event with | LedgerEvent.Salvaged _ -> true | _ -> false)
      saved |> List.length |> Expect.equal "two saves are written down" 2
    }

    testTask "a grace holds a merged worktree even once the ledger has seen it, until it has been quiet for the whole grace" {
      use sb = new Sandbox()
      let paths = populate sb
      let day = HygieneAges.oneDay
      let! _ = sweepOnce sb day On
      let! _ = sweepOnce sb day On
      Directory.Exists paths.["clean-merged"] |> Expect.isTrue "still within its grace"
      removedTargets (ledgerOf sb) |> Expect.isEmpty "nothing removed inside the grace"
    }

    testTask "with hygiene.autoTidy off the sweep still records and salvages and removes nothing" {
      use sb = new Sandbox()
      let paths = populate sb
      let! _ = sweepOnce sb noGrace Off
      let! second = sweepOnce sb noGrace Off
      for KeyValue(name, path) in paths do
        Directory.Exists path |> Expect.isTrue (sprintf "%s untouched" name)
      second.Tidy |> Expect.equal "the report says it was off" TidyHalf.SwitchedOff
      (match gitExit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/sagefs/salvage/dirty-real" ] with | 0, _, _ -> true | _ -> false)
      |> Expect.isTrue "the salvage ref exists"
    }

    testTask "a worktree a session is using is recorded as in use and is not removed, then is resumable when it goes quiet" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "busy"
      sb.Live <- { LiveFacts.none with Sessions = [ "s1", path ] }
      let! _ = sweepOnce sb noGrace On
      let! _ = sweepOnce sb noGrace On
      Directory.Exists path |> Expect.isTrue "in use, so kept"
      let record = (HygieneLedgerStore.records sb.Locations.DataDir) |> Map.find (Guard.normalize path)
      record.Activity |> Expect.equal "recorded as in use" RecordedActivity.RecordedInUse
      sb.Live <- LiveFacts.none
      let! _ = sweepOnce sb noGrace On
      let went = ledgerOf sb |> List.exists (fun l -> l.Event = LedgerEvent.WentQuiet(Guard.normalize path))
      went |> Expect.isTrue "the ledger saw it go quiet"
      Directory.Exists path |> Expect.isTrue "not removed the sweep it first went quiet"
      let! _ = sweepOnce sb noGrace On
      Directory.Exists path |> Expect.isFalse "removed once the ledger had seen it quiet"
    }

    testTask "a worktree a cohort holds is never removed, merged or not" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "cohort-held"
      sb.Live <- { LiveFacts.none with CohortHeld = [ path, "member token abc" ] }
      let! _ = sweepOnce sb noGrace On
      let! _ = sweepOnce sb noGrace On
      let! _ = sweepOnce sb noGrace On
      Directory.Exists path |> Expect.isTrue "held by the cohort, so kept"
    }
  ]
