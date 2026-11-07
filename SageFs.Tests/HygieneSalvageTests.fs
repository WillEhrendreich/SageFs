/// Step 5 of docs/plans/hygiene-that-runs-itself.md: uncommitted files become a commit on a salvage branch, made without touching
/// the worktree, so a worktree is never the only place somebody's work lives. Real git, in throwaway repositories.
module SageFs.Tests.HygieneSalvageTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.HygieneSalvage
open SageFs.Tests.HygieneSandbox

let private salvageIn (path: string) : Task<SalvageResult> = salvage runGitEnv path

let private tipOf (sb: Sandbox) (branch: string) : string = (git sb.Repo [ "rev-parse"; "refs/heads/" + branch ]).Trim()

let private show (sb: Sandbox) (spec: string) : string = git sb.Repo [ "show"; spec ]

let private statusOf (path: string) : string = git path [ "status"; "--porcelain=v1"; "--untracked-files=normal" ]

let private salvagedSha (result: SalvageResult) : string =
  match result with
  | SalvageResult.Salvaged(_, sha, _) -> sha
  | SalvageResult.NothingToSalvage
  | SalvageResult.AlreadySalvaged _
  | SalvageResult.CouldNotSalvage _ -> failtestf "expected Salvaged, got %A" result

[<Tests>]
let salvageTests =
  testList "Workspace hygiene: salvage" [

    testTask "an untracked file is committed to salvage/<name>, with the worktree's HEAD as its parent, and the worktree is untouched" {
      use sb = new Sandbox()
      let paths = populate sb
      let wt = paths.["dirty-real"]
      let headBefore = (git wt [ "rev-parse"; "HEAD" ]).Trim()
      let statusBefore = statusOf wt
      let indexFile = Path.Combine(sb.Repo, ".git", "worktrees", "dirty-real", "index")
      let indexBefore = File.GetLastWriteTimeUtc indexFile
      let! result = salvageIn wt
      match result with
      | SalvageResult.Salvaged(branch, sha, files) ->
        branch |> Expect.equal "named for the worktree" "salvage/dirty-real"
        files |> Expect.equal "one file" 1
        tipOf sb branch |> Expect.equal "the branch points at the commit" sha
        (git sb.Repo [ "rev-parse"; sha + "^" ]).Trim() |> Expect.equal "the parent is the worktree's HEAD" headBefore
        show sb (sha + ":src/precious.fs") |> Expect.equal "the file is in the commit, byte for byte" "module Precious"
      | other -> failtestf "expected Salvaged, got %A" other
      File.GetLastWriteTimeUtc indexFile |> Expect.equal "the worktree's own index was not written" indexBefore
      (git wt [ "rev-parse"; "HEAD" ]).Trim() |> Expect.equal "HEAD did not move" headBefore
      statusOf wt |> Expect.equal "the worktree still shows the file as untracked, exactly as before" statusBefore
      File.ReadAllText(Path.Combine(wt, "src", "precious.fs")) |> Expect.equal "the file itself is intact" "module Precious"
    }

    testTask "salvaging again with nothing changed makes no commit" {
      use sb = new Sandbox()
      let wt = (populate sb).["dirty-real"]
      let! first = salvageIn wt
      let! second = salvageIn wt
      match second with
      | SalvageResult.AlreadySalvaged(branch, sha) ->
        sha |> Expect.equal "the same commit" (salvagedSha first)
        branch |> Expect.equal "the same branch" "salvage/dirty-real"
      | other -> failtestf "expected AlreadySalvaged, got %A" other
    }

    testTask "files that change after a salvage make a second commit that keeps the first reachable" {
      use sb = new Sandbox()
      let wt = (populate sb).["dirty-real"]
      let! first = salvageIn wt
      write (Path.Combine(wt, "src", "precious.fs")) "module Precious2"
      write (Path.Combine(wt, "src", "second.fs")) "module Second"
      let! second = salvageIn wt
      match second with
      | SalvageResult.Salvaged(branch, sha, files) ->
        files |> Expect.equal "both files are listed" 2
        sha |> Expect.notEqual "a new commit" (salvagedSha first)
        (git sb.Repo [ "rev-parse"; sha + "^2" ]).Trim() |> Expect.equal "the earlier salvage is the second parent" (salvagedSha first)
        show sb (sha + ":src/precious.fs") |> Expect.equal "the newest bytes" "module Precious2"
        tipOf sb branch |> Expect.equal "the branch moved" sha
      | other -> failtestf "expected Salvaged, got %A" other
    }

    testTask "a tracked file deleted in the worktree is deleted in the salvage commit, and a new file is carried" {
      use sb = new Sandbox()
      let wt = sb.AddWorktree "edits"
      File.Delete(Path.Combine(wt, "src", "a.fs"))
      write (Path.Combine(wt, "notes.md")) "kept"
      let! result = salvageIn wt
      match result with
      | SalvageResult.Salvaged(_, sha, _) ->
        (git sb.Repo [ "ls-tree"; "-r"; "--name-only"; sha ]).Split('\n') |> Array.contains "src/a.fs" |> Expect.isFalse "the deleted file is gone from the commit"
        show sb (sha + ":notes.md") |> Expect.equal "the new file is in" "kept"
      | other -> failtestf "expected Salvaged, got %A" other
    }

    testTask "a path with glob characters is salvaged as itself" {
      use sb = new Sandbox()
      let wt = sb.AddWorktree "globs"
      write (Path.Combine(wt, "src", "[x]*.fs")) "module Glob"
      write (Path.Combine(wt, "src", "x.fs")) "module Other"
      let! result = salvageIn wt
      match result with
      | SalvageResult.Salvaged(_, sha, files) ->
        files |> Expect.equal "two files, each named literally" 2
        show sb (sha + ":src/[x]*.fs") |> Expect.equal "the literal path is in the commit" "module Glob"
      | other -> failtestf "expected Salvaged, got %A" other
    }

    testTask "a clean worktree and a worktree with only build output have nothing to salvage" {
      use sb = new Sandbox()
      let paths = populate sb
      let! clean = salvageIn paths.["clean-merged"]
      let! generated = salvageIn paths.["dirty-generated"]
      clean |> Expect.equal "clean" SalvageResult.NothingToSalvage
      generated |> Expect.equal "generated only" SalvageResult.NothingToSalvage
      branchExists sb "salvage/clean-merged" |> Expect.isFalse "no branch was made"
    }

    testTask "a salvaged worktree still holds work: salvage is a copy, so the worktree is not offered for removal" {
      use sb = new Sandbox()
      let wt = (populate sb).["dirty-real"]
      let! _ = salvageIn wt
      let! leftovers = gather (sb.Scan()) None
      match standingOfPath leftovers wt with
      | Standing.DirtyReal _ -> ()
      | other -> failtestf "expected DirtyReal, got %A" other
    }

    testTask "a git that will not answer is a result, never an exception" {
      use sb = new Sandbox()
      let wt = (populate sb).["dirty-real"]
      let broken : GitWithEnv = fun _ _ _ -> Task.FromResult(GitResult.Unavailable "no git here")
      let! result = salvage broken wt
      match result with
      | SalvageResult.CouldNotSalvage why -> why |> Expect.stringContains "says why" "no git"
      | other -> failtestf "expected CouldNotSalvage, got %A" other
    }
  ]
