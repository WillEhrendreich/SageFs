/// `sagefs hygiene --rescue <name>` (SageFs/HygieneRescue.fs) against real git in throwaway repositories: the work of a worktree
/// lands on `rescue/<name>` on top of the base, the worktree is not touched, and the answer says whether the base already has it.
module SageFs.Tests.HygieneRescueTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.HygieneGather
open SageFs.HygieneRescue
open SageFs.Tests.HygieneSandbox

let private contextFor (sb: Sandbox) : RescueContext =
  { Git = runGit; GitEnv = runGitEnv; Repo = sb.Repo; ScratchDir = sb.Root }

let private statusOf (path: string) : string = git path [ "status"; "--porcelain=v1"; "--untracked-files=normal" ]

let private show (sb: Sandbox) (spec: string) : string = git sb.Repo [ "show"; spec ]

let private rescued (result: RescueResult) : string * string * Replay * BaseHas =
  match result with
  | RescueResult.Rescued(branch, sha, replay, has, _) -> branch, sha, replay, has
  | RescueResult.NoSuchWorktree _
  | RescueResult.CouldNotRescue _ -> failtestf "expected Rescued, got %A" result

[<Tests>]
let rescueTests =
  testList "Workspace hygiene: sagefs hygiene --rescue" [

    testTask "a worktree with a commit and an uncommitted file lands on rescue/<name> on top of a base that moved on, and is untouched" {
      use sb = new Sandbox()
      let wt = sb.AddWorktree "work"
      write (Path.Combine(wt, "src", "feature.fs")) "module Feature"
      commitAll wt "add feature"
      write (Path.Combine(wt, "src", "draft.fs")) "module Draft"
      // The base moves on in an unrelated file, so the rescue has something to rebase onto.
      write (Path.Combine(sb.Repo, "src", "other.fs")) "module Other"
      commitAll sb.Repo "other work on master"
      let headBefore = (git wt [ "rev-parse"; "HEAD" ]).Trim()
      let statusBefore = statusOf wt
      let! result = rescue (contextFor sb) "work"
      let branch, sha, replay, has = rescued result
      branch |> Expect.equal "named for the worktree" "rescue/work"
      replay |> Expect.equal "replayed onto master" (Replay.OntoBase "master")
      has |> Expect.equal "the base lacks the commit" (BaseHas.NotAll 2)
      show sb (sha + ":src/feature.fs") |> Expect.equal "the commit's file is there" "module Feature"
      show sb (sha + ":src/draft.fs") |> Expect.equal "the uncommitted file is there" "module Draft"
      show sb (sha + ":src/other.fs") |> Expect.equal "and the base's own change, so it is rebased" "module Other"
      (match gitExit sb.Repo [ "merge-base"; "--is-ancestor"; "master"; sha ] with | 0, _, _ -> true | _ -> false)
      |> Expect.isTrue "master is an ancestor of the rescue tip: it sits on top of the base"
      (git wt [ "rev-parse"; "HEAD" ]).Trim() |> Expect.equal "the worktree's HEAD did not move" headBefore
      statusOf wt |> Expect.equal "the worktree's status is as it was" statusBefore
      File.ReadAllText(Path.Combine(wt, "src", "draft.fs")) |> Expect.equal "its uncommitted bytes are intact" "module Draft"
    }

    testTask "a worktree whose commits the base has by patch id says so, and makes the branch without replaying" {
      use sb = new Sandbox()
      let paths = populate sb
      let! result = rescue (contextFor sb) "squashed"
      let _, _, replay, has = rescued result
      has |> Expect.equal "every commit has an equivalent on the base" BaseHas.AllByPatch
      replay |> Expect.equal "nothing to replay" Replay.NothingToReplay
      branchExists sb "rescue/squashed" |> Expect.isTrue "the branch exists anyway"
      Directory.Exists paths.["squashed"] |> Expect.isTrue "the worktree is still there"
    }

    testTask "work the base has by content, whatever its history, says so" {
      use sb = new Sandbox()
      let wt = sb.AddWorktree "copied"
      write (Path.Combine(wt, "src", "copied.fs")) "module Copied"
      commitAll wt "add copied.fs in the worktree"
      // Somebody added the same file by hand on master, in two steps and beside another change, so no single commit has the
      // worktree's commit's patch id and the trees differ, while the base ends up with the file's content.
      write (Path.Combine(sb.Repo, "src", "copied.fs")) "module Draft"
      commitAll sb.Repo "a first try at copied.fs"
      write (Path.Combine(sb.Repo, "src", "copied.fs")) "module Copied"
      write (Path.Combine(sb.Repo, "src", "unrelated.fs")) "module Unrelated"
      commitAll sb.Repo "finish copied.fs and add unrelated.fs"
      let! result = rescue (contextFor sb) "copied"
      let _, _, _, has = rescued result
      has |> Expect.equal "the base has the content" BaseHas.AllByContent
    }

    testTask "a commit that conflicts with the base leaves the branch on the worktree's own tip and names what conflicted" {
      use sb = new Sandbox()
      let wt = sb.AddWorktree "clash"
      write (Path.Combine(wt, "src", "a.fs")) "module A_from_worktree"
      commitAll wt "change a.fs in the worktree"
      let tip = (git wt [ "rev-parse"; "HEAD" ]).Trim()
      write (Path.Combine(sb.Repo, "src", "a.fs")) "module A_from_master"
      commitAll sb.Repo "change a.fs on master"
      let! result = rescue (contextFor sb) "clash"
      let _, sha, replay, _ = rescued result
      (match replay with | Replay.ConflictedAt _ -> true | Replay.OntoBase _ | Replay.NothingToReplay -> false) |> Expect.isTrue "the conflict is reported"
      sha |> Expect.equal "the branch is the worktree's own tip" tip
      (RescueResult.Rescued("rescue/clash", sha, replay, BaseHas.NotAll 1, TempWorktree.Removed) |> describe) |> Expect.stringContains "says how to go on" "rebase"
      // The temporary worktree is gone: only the main checkout and the one under test remain.
      (git sb.Repo [ "worktree"; "list"; "--porcelain" ]).Split([| "worktree " |], StringSplitOptions.RemoveEmptyEntries).Length
      |> Expect.equal "no temporary worktree is left behind" 2
    }

    testTask "an unknown name lists the worktrees there are, and a rescue branch that already exists is not moved" {
      use sb = new Sandbox()
      let wt = sb.AddWorktree "twice"
      write (Path.Combine(wt, "src", "x.fs")) "module X"
      commitAll wt "x"
      let! unknown = rescue (contextFor sb) "nope"
      (describe unknown) |> Expect.stringContains "names the worktrees" "twice"
      let! first = rescue (contextFor sb) "twice"
      let _, firstSha, _, _ = rescued first
      write (Path.Combine(sb.Repo, "src", "later.fs")) "module Later"
      commitAll sb.Repo "master moves"
      let! second = rescue (contextFor sb) "twice"
      match second with
      | RescueResult.CouldNotRescue why -> why |> Expect.stringContains "says why" "already exists"
      | other -> failtestf "expected a refusal, got %A" other
      (git sb.Repo [ "rev-parse"; "refs/heads/rescue/twice" ]).Trim() |> Expect.equal "the first rescue stands" firstSha
    }
  ]
