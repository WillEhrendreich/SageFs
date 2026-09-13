module SageFs.Tests.CrossCheckoutOverlapTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.CrossCheckoutOverlap

/// RED tests for sagefs-multiagent-vision.md §10 Phase 0 item 5: a
/// cross-checkout overlap advisory — "impl-x touched LiveTestActivity.fs
/// 2m ago in worktree agent-77e1" — computed from repo-relative paths
/// across sessions whose Checkout roots share a common dir. No claim type
/// yet, just an honest, cheap advisory.
[<Tests>]
let tests = testList "CrossCheckoutOverlap" [

  testCase "WHY — repoRelative strips the checkout root, or is None outside it" <| fun () ->
    repoRelative "/repo" "/repo/SageFs/Dashboard.fs"
    |> Expect.equal "should strip the root" (Some "SageFs/Dashboard.fs")
    repoRelative "/repo" "/repo"
    |> Expect.equal "the root itself is the empty relative path" (Some "")
    repoRelative "/repo" "/elsewhere/SageFs/Dashboard.fs"
    |> Expect.equal "a path outside the root is None" None

  testCase "WHY — two sessions in the SAME repo (main checkout + worktree) that touched the same file overlap" <| fun () ->
    let now = DateTime(2026, 3, 14, 22, 0, 0, DateTimeKind.Utc)
    let self = {
      SessionId = "main0001"
      Checkout = Checkout.Checkout.MainCheckout "/repo"
      RepoRelativeFiles = [ "SageFs.Core/Features/LiveTestActivity.fs" ]
      LastActivity = now
    }
    let other = {
      SessionId = "wt000077"
      Checkout = Checkout.Checkout.Worktree("/repo/.claude/worktrees/agent-77e1", "agent-77e1")
      RepoRelativeFiles = [ "SageFs.Core/Features/LiveTestActivity.fs" ]
      LastActivity = now - TimeSpan.FromMinutes 2.0
    }
    compute self [ other ]
    |> Expect.equal "should find one overlap on the shared file"
      [ { Other = other; Files = [ "SageFs.Core/Features/LiveTestActivity.fs" ] } ]

  testCase "WHY — sessions in UNRELATED repos never overlap even on identically-named files" <| fun () ->
    let now = DateTime.UtcNow
    let self = {
      SessionId = "a"
      Checkout = Checkout.Checkout.MainCheckout "/repo-one"
      RepoRelativeFiles = [ "Program.fs" ]
      LastActivity = now
    }
    let other = {
      SessionId = "b"
      Checkout = Checkout.Checkout.MainCheckout "/repo-two"
      RepoRelativeFiles = [ "Program.fs" ]
      LastActivity = now
    }
    compute self [ other ]
    |> Expect.isEmpty "unrelated repos sharing a common file NAME must not advise overlap"

  testCase "WHY — a session never overlaps with itself" <| fun () ->
    let now = DateTime.UtcNow
    let self = {
      SessionId = "same"
      Checkout = Checkout.Checkout.MainCheckout "/repo"
      RepoRelativeFiles = [ "A.fs" ]
      LastActivity = now
    }
    compute self [ self ]
    |> Expect.isEmpty "self-comparison must never produce an advisory"

  testCase "WHY — a session with no git checkout never produces or receives an advisory" <| fun () ->
    let now = DateTime.UtcNow
    let self = {
      SessionId = "a"
      Checkout = Checkout.Checkout.NotAGitCheckout
      RepoRelativeFiles = [ "A.fs" ]
      LastActivity = now
    }
    let other = {
      SessionId = "b"
      Checkout = Checkout.Checkout.MainCheckout "/repo"
      RepoRelativeFiles = [ "A.fs" ]
      LastActivity = now
    }
    compute self [ other ] |> Expect.isEmpty "self has no checkout root to compare from"

  testCase "WHY — format names the OTHER session, the files, and where it ran" <| fun () ->
    let now = DateTime(2026, 3, 14, 22, 0, 0, DateTimeKind.Utc)
    let overlap = {
      Other = {
        SessionId = "wt000077"
        Checkout = Checkout.Checkout.Worktree("/repo/.claude/worktrees/agent-77e1", "agent-77e1")
        RepoRelativeFiles = []
        LastActivity = now - TimeSpan.FromMinutes 2.0
      }
      Files = [ "LiveTestActivity.fs" ]
    }
    let line = format now overlap
    line |> Expect.stringContains "mentions the session id" "wt000077"
    line |> Expect.stringContains "mentions the file" "LiveTestActivity.fs"
    line |> Expect.stringContains "mentions the branch" "agent-77e1"
]
