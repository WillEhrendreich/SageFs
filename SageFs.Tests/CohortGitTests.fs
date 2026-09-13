module SageFs.Tests.CohortGitTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// RED tests for sagefs-multiagent-vision.md item 14a: the cohort landing
/// git performer. `SageFs.Features.CohortGit` is the pure-IO layer a LATER
/// slice injects into `CohortOwner`'s effect loop — this suite exercises it
/// standalone, against real throwaway git repos, never against the working
/// repo these tests run from (every repo/worktree path here is a fresh
/// `Directory.CreateTempSubdirectory`, cleaned up in a `finally`).
///
/// These tests spawn a real `git` process per assertion, so they are
/// `[Integration]` (run via `--integration-host`), not part of the fast
/// default suite.
///
/// Independent oracle: the `git` helper below is a SEPARATE, minimal
/// process-spawning implementation from `CohortGit`'s own — assertions
/// compare CohortGit's results against this independent git invocation,
/// never against CohortGit's own output, so a bug shared by both
/// implementations can't hide.
[<Tests>]
let tests =
  Integration.hostList "CohortGit" [

    let gitAvailable () : bool =
      try
        let psi =
          ProcessStartInfo(
            "git", "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false)
        use proc = Process.Start psi
        proc.WaitForExit(5000) |> ignore
        proc.HasExited && proc.ExitCode = 0
      with _ -> false

    /// Independent oracle: spawns `git` directly, checks the exit code, and
    /// returns trimmed stdout. Used for BOTH fixture setup (init/commit/
    /// checkout) and for verifying CohortGit's results — a fail-loud helper
    /// (throws on nonzero exit) since a broken fixture must never masquerade
    /// as a CohortGit bug.
    let git (dir: string) (args: string list) : Async<string> =
      async {
        let psi =
          ProcessStartInfo(
            "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = dir)
        for a in args do psi.ArgumentList.Add a
        use proc = new Process(StartInfo = psi)
        proc.Start() |> ignore
        let! stdout = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
        let! stderr = proc.StandardError.ReadToEndAsync() |> Async.AwaitTask
        do! proc.WaitForExitAsync() |> Async.AwaitTask
        match proc.ExitCode with
        | 0 -> return stdout.Trim()
        | code -> return failwithf "git %s failed (%d) in %s: %s" (String.concat " " args) code dir stderr
      }

    let initRepo (dir: string) : Async<unit> =
      async {
        let! _ = git dir [ "init"; "--quiet"; "-b"; "main" ]
        let! _ = git dir [ "config"; "user.email"; "sagefs-test@example.com" ]
        let! _ = git dir [ "config"; "user.name"; "SageFs Test" ]
        return ()
      }

    let writeAndCommit (dir: string) (relPath: string) (content: string) (message: string) : Async<unit> =
      async {
        let full = Path.Combine(dir, relPath)
        let parent = Path.GetDirectoryName full
        if not (String.IsNullOrEmpty parent) then Directory.CreateDirectory parent |> ignore
        File.WriteAllText(full, content)
        let! _ = git dir [ "add"; relPath ]
        let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
        return ()
      }

    let isRebaseInProgress (dir: string) : bool =
      Directory.Exists(Path.Combine(dir, ".git", "rebase-merge"))
      || Directory.Exists(Path.Combine(dir, ".git", "rebase-apply"))

    if not (gitAvailable ()) then
      testCase "git must be available on PATH" <| fun () ->
        failtest "git is not available on PATH — CohortGit's performer requires a real git executable; install git rather than skip this suite"
    else

    yield! [

      testAsync "currentHead returns the HEAD sha after a commit" {
        let repo = Directory.CreateTempSubdirectory("cohortgit-head-").FullName
        try
          do! initRepo repo
          do! writeAndCommit repo "a.txt" "hello" "initial"
          let! expectedSha = git repo [ "rev-parse"; "HEAD" ]
          let! result = CohortGit.currentHead repo
          match result with
          | Ok sha -> sha |> Expect.equal "sha should match the independent git rev-parse HEAD" expectedSha
          | Error e -> failtestf "expected Ok, got Error %s" e
        finally Directory.Delete(repo, true)
      }

      testAsync "diffNames lists exactly the files changed between two commits" {
        let repo = Directory.CreateTempSubdirectory("cohortgit-diff-").FullName
        try
          do! initRepo repo
          do! writeAndCommit repo "a.txt" "1" "base"
          let! baseSha = git repo [ "rev-parse"; "HEAD" ]
          do! writeAndCommit repo "b.txt" "2" "add b"
          do! writeAndCommit repo "c.txt" "3" "add c"
          let! headSha = git repo [ "rev-parse"; "HEAD" ]
          let! result = CohortGit.diffNames repo baseSha headSha
          match result with
          | Ok files ->
            files |> List.sort |> Expect.equal "should list exactly b.txt and c.txt" [ "b.txt"; "c.txt" ]
          | Error e -> failtestf "expected Ok, got Error %s" e
        finally Directory.Delete(repo, true)
      }

      testAsync "rebase of a non-conflicting commit onto an advanced main returns Ok and leaves the worktree clean" {
        let repo = Directory.CreateTempSubdirectory("cohortgit-rebase-ok-").FullName
        try
          do! initRepo repo
          do! writeAndCommit repo "shared.txt" "base\n" "base"
          let! _ = git repo [ "checkout"; "-b"; "feature" ]
          do! writeAndCommit repo "feature.txt" "feature work\n" "feature commit"
          let! _ = git repo [ "checkout"; "main" ]
          do! writeAndCommit repo "main-only.txt" "main progressed\n" "main commit"
          let! ontoSha = git repo [ "rev-parse"; "main" ]
          let! _ = git repo [ "checkout"; "feature" ]
          let! result = CohortGit.rebase repo ontoSha
          match result with
          | Ok newHead ->
            (newHead <> "") |> Expect.isTrue "the returned sha should not be empty"
            let! status = git repo [ "status"; "--porcelain" ]
            status |> Expect.isEmpty "worktree should be clean after a successful rebase"
            isRebaseInProgress repo |> Expect.isFalse "no rebase should be left in progress"
          | Error files -> failtestf "expected Ok, got %A" files
        finally Directory.Delete(repo, true)
      }

      testAsync "rebase with a conflicting change returns Error with the conflicting file, and leaves the worktree clean" {
        let repo = Directory.CreateTempSubdirectory("cohortgit-rebase-conflict-").FullName
        try
          do! initRepo repo
          do! writeAndCommit repo "shared.txt" "base\n" "base"
          let! _ = git repo [ "checkout"; "-b"; "feature" ]
          do! writeAndCommit repo "shared.txt" "feature change\n" "feature edits shared"
          let! _ = git repo [ "checkout"; "main" ]
          do! writeAndCommit repo "shared.txt" "main change\n" "main edits shared"
          let! ontoSha = git repo [ "rev-parse"; "main" ]
          let! _ = git repo [ "checkout"; "feature" ]
          let! result = CohortGit.rebase repo ontoSha
          match result with
          | Error files ->
            files |> Expect.equal "should report shared.txt as the conflicting file" [ "shared.txt" ]
            do
              match CohortGit.classifyRebaseFailure files with
              | CohortGit.RebaseFailure.Conflict _ -> ()
              | CohortGit.RebaseFailure.InfraFailure reason ->
                failtestf "expected a Conflict, got InfraFailure %s" reason
            let! status = git repo [ "status"; "--porcelain" ]
            status |> Expect.isEmpty "worktree should be clean after an aborted rebase"
            isRebaseInProgress repo |> Expect.isFalse "no rebase should be left in progress"
          | Ok sha -> failtestf "expected a conflict, got Ok %s" sha
        finally Directory.Delete(repo, true)
      }

      testAsync "rebase onto a nonexistent ref is an infra failure, not a conflict, and leaves the worktree clean" {
        let repo = Directory.CreateTempSubdirectory("cohortgit-rebase-infra-").FullName
        try
          do! initRepo repo
          do! writeAndCommit repo "a.txt" "1" "base"
          let! result = CohortGit.rebase repo "refs/heads/does-not-exist"
          match result with
          | Error files ->
            do
              match CohortGit.classifyRebaseFailure files with
              | CohortGit.RebaseFailure.InfraFailure _ -> ()
              | CohortGit.RebaseFailure.Conflict conflictFiles ->
                failtestf "expected an InfraFailure, got Conflict %A" conflictFiles
            let! status = git repo [ "status"; "--porcelain" ]
            status |> Expect.isEmpty "worktree should be clean after an infra failure too"
            isRebaseInProgress repo |> Expect.isFalse "no rebase should be left in progress"
          | Ok sha -> failtestf "expected Error for a nonexistent onto ref, got Ok %s" sha
        finally Directory.Delete(repo, true)
      }

      testAsync "fastForwardBranch fast-forwards when possible and returns the sha" {
        let repo = Directory.CreateTempSubdirectory("cohortgit-ff-ok-").FullName
        try
          do! initRepo repo
          do! writeAndCommit repo "a.txt" "1" "base"
          let! _ = git repo [ "checkout"; "-b"; "integration" ]
          do! writeAndCommit repo "b.txt" "2" "advance"
          let! headSha = git repo [ "rev-parse"; "integration" ]
          let! _ = git repo [ "checkout"; "main" ]
          let! result = CohortGit.fastForwardBranch repo "main" headSha
          match result with
          | Ok sha ->
            sha |> Expect.equal "should return the target sha" headSha
            let! mainSha = git repo [ "rev-parse"; "main" ]
            mainSha |> Expect.equal "main should now point at the target sha" headSha
          | Error e -> failtestf "expected Ok, got Error %s" e
        finally Directory.Delete(repo, true)
      }

      testAsync "fastForwardBranch returns Error (no merge commit) when ff is impossible" {
        let repo = Directory.CreateTempSubdirectory("cohortgit-ff-fail-").FullName
        try
          do! initRepo repo
          do! writeAndCommit repo "a.txt" "1" "base"
          let! baseSha = git repo [ "rev-parse"; "main" ]
          do! writeAndCommit repo "b.txt" "2" "main diverges"
          let! mainShaBefore = git repo [ "rev-parse"; "main" ]
          let! _ = git repo [ "checkout"; "-b"; "other"; baseSha ]
          do! writeAndCommit repo "c.txt" "3" "other diverges"
          let! otherSha = git repo [ "rev-parse"; "other" ]
          let! result = CohortGit.fastForwardBranch repo "main" otherSha
          match result with
          | Error _ ->
            let! mainShaAfter = git repo [ "rev-parse"; "main" ]
            mainShaAfter |> Expect.equal "main must be untouched when a fast-forward isn't possible" mainShaBefore
          | Ok sha -> failtestf "expected Error when fast-forward is impossible, got Ok %s" sha
        finally Directory.Delete(repo, true)
      }

      testAsync "addWorktree creates a worktree on a new branch at a base ref, and removeWorktree cleans it up" {
        let repo = Directory.CreateTempSubdirectory("cohortgit-wt-repo-").FullName
        let worktreeParent = Directory.CreateTempSubdirectory("cohortgit-wt-parent-").FullName
        let worktreePath = Path.Combine(worktreeParent, "wt")
        try
          do! initRepo repo
          do! writeAndCommit repo "a.txt" "1" "base"
          let! baseSha = git repo [ "rev-parse"; "main" ]
          let! addResult = CohortGit.addWorktree repo worktreePath "cohort-branch" baseSha
          match addResult with
          | Ok () ->
            Directory.Exists worktreePath |> Expect.isTrue "the worktree directory should exist"
            File.Exists(Path.Combine(worktreePath, "a.txt")) |> Expect.isTrue "the worktree should contain the base ref's files"
            let! removeResult = CohortGit.removeWorktree repo worktreePath
            match removeResult with
            | Ok () -> Directory.Exists worktreePath |> Expect.isFalse "the worktree directory should be removed"
            | Error e -> failtestf "expected removeWorktree to succeed, got Error %s" e
          | Error e -> failtestf "expected addWorktree to succeed, got Error %s" e
        finally
          (try Directory.Delete(repo, true) with _ -> ())
          (try Directory.Delete(worktreeParent, true) with _ -> ())
      }
    ]
  ]
