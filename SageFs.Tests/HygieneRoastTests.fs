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
