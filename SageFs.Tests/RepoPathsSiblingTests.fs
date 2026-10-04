module SageFs.Tests.RepoPathsSiblingTests

// A sibling repository (sagefs.nvim) sits NEXT TO the main SageFs checkout. From a git worktree the repo root is
// `<main>/.claude/worktrees/<name>`, whose parent is not the main checkout's parent, so "the parent of the repo
// root" found nothing there. The sibling is located from the MAIN checkout, which a worktree's `.git` file names.

open System
open System.IO
open Expecto
open Expecto.Flip

let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-repopaths-sibling-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally
    try Directory.Delete(dir, true) with _ -> ()

/// `<parent>/SageFs` as a main checkout (`.git` is a directory), with a worktree two levels down the way
/// `.claude/worktrees/<name>` lays one out (`.git` is a FILE naming the main checkout's admin dir).
let private layOut (parent: string) : string * string =
  let main = Path.Combine(parent, "SageFs")
  let adminDir = Path.Combine(main, ".git", "worktrees", "agent-x")
  Directory.CreateDirectory adminDir |> ignore
  let worktree = Path.Combine(main, ".claude", "worktrees", "agent-x")
  Directory.CreateDirectory worktree |> ignore
  File.WriteAllText(Path.Combine(worktree, ".git"), sprintf "gitdir: %s\n" adminDir)
  main, worktree

[<Tests>]
let tests =
  testList "RepoPaths sibling checkouts" [
    testCase "from the main checkout the sibling is a child of the main checkout's parent" <| fun _ ->
      withTempDir (fun parent ->
        let main, _ = layOut parent
        RepoPaths.siblingCheckoutDir main "sagefs.nvim"
        |> Expect.equal "the sibling sits next to the checkout" (Path.Combine(parent, "sagefs.nvim")))

    testCase "from a git worktree the sibling is still next to the MAIN checkout, not next to the worktree" <| fun _ ->
      withTempDir (fun parent ->
        let _, worktree = layOut parent
        RepoPaths.siblingCheckoutDir worktree "sagefs.nvim"
        |> Expect.equal "a worktree finds the same sibling its main checkout does" (Path.Combine(parent, "sagefs.nvim")))

    testCase "a worktree whose .git pointer is relative resolves the same main checkout" <| fun _ ->
      withTempDir (fun parent ->
        let main, worktree = layOut parent
        let adminDir = Path.Combine(main, ".git", "worktrees", "agent-x")
        File.WriteAllText(Path.Combine(worktree, ".git"), sprintf "gitdir: %s\n" (Path.GetRelativePath(worktree, adminDir)))
        RepoPaths.mainCheckoutRoot worktree
        |> Expect.equal "the relative pointer lands on the same main checkout" main)

    testCase "a root that is not a worktree is its own main checkout" <| fun _ ->
      withTempDir (fun parent ->
        let main, _ = layOut parent
        RepoPaths.mainCheckoutRoot main |> Expect.equal "a directory .git means this is the main checkout" main)

    testCase "the checkout this suite runs from has its sibling resolved from the main checkout" <| fun _ ->
      let root = RepoPaths.requireRepoRoot ()
      let main = RepoPaths.mainCheckoutRoot root
      RepoPaths.siblingCheckoutDir root "sagefs.nvim"
      |> Expect.equal
        "the same answer whether this is the main checkout or a worktree of it"
        (Path.Combine(Path.GetDirectoryName main, "sagefs.nvim"))
  ]
