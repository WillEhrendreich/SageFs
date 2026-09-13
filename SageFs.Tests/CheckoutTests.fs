module SageFs.Tests.CheckoutTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// RED tests for sagefs-multiagent-vision.md §3.2 / §10 Phase 0 item 3:
/// `Checkout.classify` must tell a git worktree apart from its main
/// checkout — a worktree's `.git` is a FILE, and the fix must not treat a
/// bare `Directory.Exists ".git"` check as sufficient (that was the old bug:
/// it walked straight past the worktree's `.git` file to the main
/// checkout's `.git` directory further up).
[<Tests>]
let tests = testList "Checkout" [

  testCase "WHY — a plain repository is a MainCheckout" <| fun () ->
    let tmp = Directory.CreateTempSubdirectory("sagefs-main-").FullName
    try
      Directory.CreateDirectory(Path.Combine(tmp, ".git")) |> ignore
      Checkout.classify tmp
      |> Expect.equal "should classify as MainCheckout" (Checkout.Checkout.MainCheckout tmp)
    finally Directory.Delete(tmp, true)

  testCase "WHY — a directory with no .git anywhere up the tree is NotAGitCheckout" <| fun () ->
    let tmp = Directory.CreateTempSubdirectory("sagefs-none-").FullName
    try
      Checkout.classify tmp
      |> Expect.equal "should classify as NotAGitCheckout" Checkout.Checkout.NotAGitCheckout
    finally Directory.Delete(tmp, true)

  testCase "WHY — a git worktree's .git FILE classifies as Worktree with its OWN branch, not the main checkout's" <| fun () ->
    let tmp = Directory.CreateTempSubdirectory("sagefs-wt-").FullName
    try
      let mainGitDir = Path.Combine(tmp, ".git")
      let worktreesAdminDir = Path.Combine(mainGitDir, "worktrees", "agent-x")
      Directory.CreateDirectory(worktreesAdminDir) |> ignore
      File.WriteAllText(Path.Combine(worktreesAdminDir, "HEAD"), "ref: refs/heads/worktree-agent-x\n")
      // The MAIN checkout is on a DIFFERENT branch — proves classify reads
      // the worktree's own admin-dir HEAD, not the main checkout's.
      File.WriteAllText(Path.Combine(mainGitDir, "HEAD"), "ref: refs/heads/master\n")
      let worktreeRoot = Path.Combine(tmp, "wt")
      Directory.CreateDirectory(worktreeRoot) |> ignore
      File.WriteAllText(Path.Combine(worktreeRoot, ".git"), sprintf "gitdir: %s" worktreesAdminDir)
      Checkout.classify worktreeRoot
      |> Expect.equal "should be Worktree with the worktree's own branch" (Checkout.Checkout.Worktree(worktreeRoot, "worktree-agent-x"))
    finally Directory.Delete(tmp, true)

  testCase "WHY — classify walks UP from a subdirectory to find the worktree root, not just the exact dir" <| fun () ->
    let tmp = Directory.CreateTempSubdirectory("sagefs-wt-sub-").FullName
    try
      let worktreesAdminDir = Path.Combine(tmp, ".git", "worktrees", "agent-y")
      Directory.CreateDirectory(worktreesAdminDir) |> ignore
      File.WriteAllText(Path.Combine(worktreesAdminDir, "HEAD"), "ref: refs/heads/feature-y\n")
      let worktreeRoot = Path.Combine(tmp, "wt")
      let nested = Path.Combine(worktreeRoot, "SageFs", "Sub")
      Directory.CreateDirectory(nested) |> ignore
      File.WriteAllText(Path.Combine(worktreeRoot, ".git"), sprintf "gitdir: %s" worktreesAdminDir)
      Checkout.classify nested
      |> Expect.equal "should find the worktree root above the subdirectory" (Checkout.Checkout.Worktree(worktreeRoot, "feature-y"))
    finally Directory.Delete(tmp, true)

  testCase "WHY — detached HEAD in a worktree reports the raw commit, not a crash" <| fun () ->
    let tmp = Directory.CreateTempSubdirectory("sagefs-wt-detached-").FullName
    try
      let worktreesAdminDir = Path.Combine(tmp, ".git", "worktrees", "agent-z")
      Directory.CreateDirectory(worktreesAdminDir) |> ignore
      File.WriteAllText(Path.Combine(worktreesAdminDir, "HEAD"), "abc123deadbeef\n")
      let worktreeRoot = Path.Combine(tmp, "wt")
      Directory.CreateDirectory(worktreeRoot) |> ignore
      File.WriteAllText(Path.Combine(worktreeRoot, ".git"), sprintf "gitdir: %s" worktreesAdminDir)
      match Checkout.classify worktreeRoot with
      | Checkout.Checkout.Worktree(root, branch) ->
        root |> Expect.equal "root" worktreeRoot
        branch |> Expect.equal "detached HEAD reports the raw ref text" "abc123deadbeef"
      | other -> failwithf "expected Worktree, got %A" other
    finally Directory.Delete(tmp, true)

  testCase "WHY — hasCheckoutMarker recognizes a .git FILE, not only a .git directory" <| fun () ->
    let tmp = Directory.CreateTempSubdirectory("sagefs-marker-").FullName
    try
      File.WriteAllText(Path.Combine(tmp, ".git"), "gitdir: /elsewhere")
      Checkout.hasCheckoutMarker tmp |> Expect.isTrue "a .git file is a checkout marker"
    finally Directory.Delete(tmp, true)

  testCase "WHY — root returns the checkout root for both MainCheckout and Worktree, None for NotAGitCheckout" <| fun () ->
    Checkout.root (Checkout.Checkout.MainCheckout "/repo") |> Expect.equal "MainCheckout" (Some "/repo")
    Checkout.root (Checkout.Checkout.Worktree("/repo/wt", "b")) |> Expect.equal "Worktree" (Some "/repo/wt")
    Checkout.root Checkout.Checkout.NotAGitCheckout |> Expect.equal "NotAGitCheckout" None
]
