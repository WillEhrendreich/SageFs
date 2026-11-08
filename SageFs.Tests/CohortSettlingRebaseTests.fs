module SageFs.Tests.CohortSettlingRebaseTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// A landing's rebase settles a conflict that is ONLY in generated pages (P14, C6), and only that: the page is regenerated
/// from the merged tree afterwards, so neither side's text of it matters, while a conflict in a file somebody wrote is still
/// the member's to resolve. Real throwaway git repositories; the oracle is plain git, never `CohortGit`.
[<Tests>]
let tests =
  Integration.hostList "CohortGit settling rebase (P14)" [

    let git (dir: string) (args: string list) : Async<string> =
      async {
        let psi = ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir)
        for a in args do psi.ArgumentList.Add a
        SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
        use proc = new Process(StartInfo = psi)
        proc.Start() |> ignore
        let! stdout = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
        let! stderr = proc.StandardError.ReadToEndAsync() |> Async.AwaitTask
        do! proc.WaitForExitAsync() |> Async.AwaitTask
        match proc.ExitCode with
        | 0 -> return stdout.Trim()
        | code -> return failwithf "git %s failed (%d) in %s: %s" (String.concat " " args) code dir stderr
      }

    let write (dir: string) (relative: string) (content: string) =
      let full = Path.Combine(dir, relative)
      Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
      File.WriteAllText(full, content)

    let commit (dir: string) (relative: string) (content: string) (message: string) : Async<unit> =
      async {
        write dir relative content
        let! _ = git dir [ "add"; relative ]
        let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
        return ()
      }

    /// A repository where `main` and `member` both changed `docs/roadmap.md`, and each changed one more file of its own.
    let conflictingRepo (alsoConflictInCode: bool) : Async<string * string * string> =
      async {
        let repo = Directory.CreateTempSubdirectory("cohortgit-settle-").FullName
        let! _ = git repo [ "init"; "--quiet"; "-b"; "main" ]
        let! _ = git repo [ "config"; "user.email"; "sagefs-test@example.com" ]
        let! _ = git repo [ "config"; "user.name"; "SageFs Test" ]
        do! commit repo "docs/roadmap.md" "base\n" "base page"
        do! commit repo "Core/Json.fs" "let a = 1\n" "base code"
        let! _ = git repo [ "checkout"; "--quiet"; "-b"; "member" ]
        do! commit repo "docs/roadmap.md" "member's page\n" "member page"
        do! commit repo "Core/Own.fs" "let own = 1\n" "member code"
        if alsoConflictInCode then do! commit repo "Core/Json.fs" "let a = 2 // member\n" "member edits shared code"
        let! memberTip = git repo [ "rev-parse"; "HEAD" ]
        let! _ = git repo [ "checkout"; "--quiet"; "main" ]
        do! commit repo "docs/roadmap.md" "main's page\n" "main page"
        if alsoConflictInCode then do! commit repo "Core/Json.fs" "let a = 3 // main\n" "main edits shared code"
        let! mainTip = git repo [ "rev-parse"; "HEAD" ]
        return repo, mainTip, memberTip
      }

    let rebaseInProgress (repo: string) =
      Directory.Exists(Path.Combine(repo, ".git", "rebase-merge")) || Directory.Exists(Path.Combine(repo, ".git", "rebase-apply"))

    yield! [
      testAsync "a conflict only in a generated page is settled by taking the integration head's page, and the member's other work lands" {
        let! repo, mainTip, memberTip = conflictingRepo false
        try
          let! result = CohortGit.rebaseCommitsOntoSettling ClaimExempt.conflictIsOnlyGeneratedPages repo mainTip [ memberTip ]
          match result with
          | Error files -> failtestf "expected the page conflict to settle, got %A" files
          | Ok head ->
            let! page = git repo [ "show"; sprintf "%s:docs/roadmap.md" head ]
            page |> Expect.equal "the head's page, not the member's" "main's page"
            let! own = git repo [ "show"; sprintf "%s:Core/Own.fs" head ]
            own |> Expect.stringContains "the member's own file came through" "let own = 1"
            let! isDescendant = git repo [ "merge-base"; "--is-ancestor"; mainTip; head ] |> Async.Catch
            isDescendant |> function Choice1Of2 _ -> () | Choice2Of2 (e: exn) -> failtestf "the result is not on top of the head: %s" e.Message
            rebaseInProgress repo |> Expect.isFalse "no rebase is left in progress"
        finally Directory.Delete(repo, true)
      }

      testAsync "a conflict in a file somebody wrote is still refused, naming it, and nothing is left half done" {
        let! repo, mainTip, memberTip = conflictingRepo true
        try
          let! result = CohortGit.rebaseCommitsOntoSettling ClaimExempt.conflictIsOnlyGeneratedPages repo mainTip [ memberTip ]
          match result with
          | Ok head -> failtestf "expected a refusal, landed at %s" head
          | Error files ->
            files |> Expect.contains "names the file somebody wrote" "Core/Json.fs"
            rebaseInProgress repo |> Expect.isFalse "the rebase was aborted"
        finally Directory.Delete(repo, true)
      }

      testAsync "without the settle rule the same page conflict is a refusal (the old behaviour)" {
        let! repo, mainTip, memberTip = conflictingRepo false
        try
          let! result = CohortGit.rebaseCommitsOnto repo mainTip [ memberTip ]
          match result with
          | Ok head -> failtestf "expected a refusal, landed at %s" head
          | Error files -> files |> Expect.contains "names the page" "docs/roadmap.md"
        finally Directory.Delete(repo, true)
      }

      testAsync "isAncestor tells a commit in the head's history from one outside it" {
        let! repo, mainTip, memberTip = conflictingRepo false
        try
          let! baseSha = git repo [ "merge-base"; mainTip; memberTip ]
          let! inside = CohortGit.isAncestor repo baseSha mainTip
          inside |> Expect.isTrue "the merge base is in main's history"
          let! outside = CohortGit.isAncestor repo memberTip mainTip
          outside |> Expect.isFalse "the member's tip is not"
        finally Directory.Delete(repo, true)
      }

      testAsync "commitPaths commits what changed and says when nothing did" {
        let! repo, mainTip, _ = conflictingRepo false
        try
          let! _ = git repo [ "checkout"; "--quiet"; "--detach"; mainTip ]
          let! nothing = CohortGit.commitPaths repo [ "docs/roadmap.md" ] "regenerate"
          nothing |> function Ok None -> () | other -> failtestf "expected nothing to commit, got %A" other
          write repo "docs/roadmap.md" "regenerated\n"
          let! committed = CohortGit.commitPaths repo [ "docs/roadmap.md" ] "regenerate"
          match committed with
          | Ok (Some sha) ->
            let! shown = git repo [ "show"; sprintf "%s:docs/roadmap.md" sha ]
            shown |> Expect.equal "the new text is committed" "regenerated"
          | other -> failtestf "expected a commit, got %A" other
        finally Directory.Delete(repo, true)
      }
    ]
  ]
