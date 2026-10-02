/// The git the trunk checkout is moved with, against a real repository: what changed between two commits as files a save pipeline
/// can be handed, a detached worktree, and a move of it.
module SageFs.Tests.TrunkGitTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.TrunkFollow

let private git (dir: string) (args: string list) : Task<string> =
  task {
    let psi = ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir)
    for a in args do psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let! stdout = proc.StandardOutput.ReadToEndAsync()
    let! stderr = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync()
    match proc.ExitCode with
    | 0 -> return stdout.Trim()
    | code -> return failwithf "git %s failed (%d): %s" (String.concat " " args) code stderr
  }

let private commitAll (dir: string) (message: string) : Task<string> =
  task {
    let! _ = git dir [ "add"; "-A" ]
    let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
    return! git dir [ "rev-parse"; "HEAD" ]
  }

[<Tests>]
let trunkGitTests =
  testList "Trunk git" [
    testCase "a name-status diff becomes the files a save pipeline is handed, as absolute paths" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "trunk-root")
      let text = "M\tsrc/Handlers.fs\nA\tsrc/New.fs\nD\tOld.fs\nT\tLink.fs\n"
      CohortGit.savedFilesOfNameStatus root text
      |> Expect.equal
        "changed, created, deleted, and a type change is a change"
        [ { Path = Path.Combine(root, "src", "Handlers.fs"); Kind = SaveKind.Changed }
          { Path = Path.Combine(root, "src", "New.fs"); Kind = SaveKind.Created }
          { Path = Path.Combine(root, "Old.fs"); Kind = SaveKind.Deleted }
          { Path = Path.Combine(root, "Link.fs"); Kind = SaveKind.Changed } ]

    testCase "a line that is not a status and a path is dropped, not guessed at" <| fun _ ->
      CohortGit.savedFilesOfNameStatus (Path.GetTempPath()) "warning: something\n\nM\n" |> Expect.isEmpty "nothing"

    testTask "a detached worktree is moved to a commit, and the diff names what changed under it" {
      let repo = Directory.CreateTempSubdirectory("trunk-git-repo-").FullName
      let trunk = Path.Combine(Directory.CreateTempSubdirectory("trunk-git-wt-").FullName, "cohort-trunk")
      try
        let! _ = git repo [ "init"; "--quiet"; "-b"; "main" ]
        let! _ = git repo [ "config"; "user.email"; "trunk-git@example.com" ]
        let! _ = git repo [ "config"; "user.name"; "Trunk Git Test" ]
        File.WriteAllText(Path.Combine(repo, "Keep.fs"), "let a = 1\n")
        File.WriteAllText(Path.Combine(repo, "Gone.fs"), "let gone = 1\n")
        let! first = commitAll repo "first"
        File.WriteAllText(Path.Combine(repo, "Keep.fs"), "let a = 2\n")
        File.WriteAllText(Path.Combine(repo, "Added.fs"), "let added = 1\n")
        File.Delete(Path.Combine(repo, "Gone.fs"))
        let! second = commitAll repo "second"

        let! added = CohortGit.addDetachedWorktree repo trunk first |> Async.StartAsTask
        added |> Expect.isOk "the worktree is added"
        let! headBefore = CohortGit.currentHead trunk |> Async.StartAsTask
        headBefore |> Expect.equal "the trunk starts at the first commit" (Result.Ok first)
        let! branch = git trunk [ "branch"; "--show-current" ]
        branch |> Expect.equal "detached: no branch is held, so the integration branch stays free to move" ""

        let! diff = CohortGit.diffSavedFiles trunk first second |> Async.StartAsTask
        let files = (match diff with Result.Ok f -> f | Result.Error e -> failtestf "diff failed: %s" e)
        files
        |> List.map (fun f -> Path.GetFileName f.Path, f.Kind)
        |> List.sort
        |> Expect.equal "what changed" [ "Added.fs", SaveKind.Created; "Gone.fs", SaveKind.Deleted; "Keep.fs", SaveKind.Changed ]
        files |> List.forall (fun f -> f.Path.StartsWith trunk) |> Expect.isTrue "paths are in the trunk checkout"

        let! moved = CohortGit.moveCheckoutTo trunk second |> Async.StartAsTask
        moved |> Expect.isOk "the checkout moves"
        File.ReadAllText(Path.Combine(trunk, "Keep.fs")) |> Expect.equal "the file changed on disk" "let a = 2\n"
        File.Exists(Path.Combine(trunk, "Added.fs")) |> Expect.isTrue "the added file is there"
        File.Exists(Path.Combine(trunk, "Gone.fs")) |> Expect.isFalse "the deleted file is gone"
        let! headAfter = CohortGit.currentHead trunk |> Async.StartAsTask
        headAfter |> Expect.equal "and the trunk is at the second commit" (Result.Ok second)
      finally
        (try Directory.Delete(repo, true) with _ -> ())
        (try Directory.Delete(Path.GetDirectoryName trunk, true) with _ -> ())
    }

    testTask "moving to a commit that does not exist is an error naming git's reason, not a half-moved checkout" {
      let repo = Directory.CreateTempSubdirectory("trunk-git-bad-").FullName
      try
        let! _ = git repo [ "init"; "--quiet"; "-b"; "main" ]
        let! _ = git repo [ "config"; "user.email"; "trunk-git@example.com" ]
        let! _ = git repo [ "config"; "user.name"; "Trunk Git Test" ]
        File.WriteAllText(Path.Combine(repo, "A.fs"), "let a = 1\n")
        let! _ = commitAll repo "first"
        let! moved = CohortGit.moveCheckoutTo repo (String('0', 40)) |> Async.StartAsTask
        match moved with
        | Result.Error reason -> reason |> Expect.isNotEmpty "git says why"
        | Result.Ok () -> failtest "moving to a missing commit must fail"
        File.ReadAllText(Path.Combine(repo, "A.fs")) |> Expect.equal "and the checkout is as it was" "let a = 1\n"
      finally
        (try Directory.Delete(repo, true) with _ -> ())
    }
  ]
