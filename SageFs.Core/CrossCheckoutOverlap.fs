namespace SageFs

open System

/// Cross-checkout file overlap advisory — Phase 0 item 5 of
/// sagefs-multiagent-vision.md §10: "impl-x touched LiveTestActivity.fs 2m
/// ago in worktree agent-77e1" surfaced in eval output, no claim type yet,
/// just an honest advisory. Distinct from
/// `SageFs.SessionOperations.FileOverlapAdvisory`, which compares AGENTS
/// sharing one SESSION's RecentFiles; this compares SESSIONS whose
/// `Checkout` roots are siblings under a common directory (the main
/// checkout and its git worktrees, or two worktrees of the same repo) — the
/// shape the dogfooding evidence found missing (sagefs-multiagent-vision.md
/// §2, S3/S4: "agents edited the same files; nothing said that is claimed").
module CrossCheckoutOverlap =

  /// One session's side of a detected overlap: what it touched, relative to
  /// ITS OWN checkout root so the comparison is meaningful across checkouts
  /// — a worktree and its main checkout share the same repo-relative file
  /// layout even though their absolute `WorkingDirectory`s differ.
  type SessionTouch = {
    SessionId: string
    Checkout: Checkout.Checkout
    RepoRelativeFiles: string list
    LastActivity: DateTime
  }

  /// A detected overlap between `Self` and `Other` over `Files`.
  type Overlap = {
    Other: SessionTouch
    Files: string list
  }

  /// Repo-relative path under `root`, or None when `path` is not under
  /// `root` at all. Pure path arithmetic, matching
  /// sagefs-multiagent-vision.md §5.1's "overlap is path arithmetic" rule.
  let repoRelative (root: string) (path: string) : string option =
    let normalize (p: string) = p.Replace('\\', '/').TrimEnd('/')
    let r = normalize root
    let p = normalize path
    match String.Equals(p, r, StringComparison.OrdinalIgnoreCase) with
    | true -> Some ""
    | false ->
      match p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) with
      | true -> Some(p.Substring(r.Length + 1))
      | false -> None

  /// Two checkout roots "share a common dir" when one is an ancestor of the
  /// other — the convention this repo's own worktrees follow
  /// (`<repo>/.claude/worktrees/<name>`), and the general case for any git
  /// worktree layout, without a `git` subprocess call to ask for the real
  /// common dir.
  let private shareCommonDir (rootA: string) (rootB: string) : bool =
    let normalize (p: string) = (p: string).Replace('\\', '/').TrimEnd('/')
    let a = normalize rootA
    let b = normalize rootB
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
    || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase)
    || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)

  /// Compute overlap advisories for `self` against every `other` session
  /// whose checkout shares a common dir with `self`'s. Pure, no IO. A
  /// session with no git checkout, or with no touched files, never overlaps.
  let compute (self: SessionTouch) (others: SessionTouch list) : Overlap list =
    match Checkout.root self.Checkout, self.RepoRelativeFiles with
    | None, _ | _, [] -> []
    | Some selfRoot, selfFiles ->
      let selfSet = Set.ofList selfFiles
      others
      |> List.filter (fun o -> o.SessionId <> self.SessionId)
      |> List.choose (fun o ->
        match Checkout.root o.Checkout with
        | Some otherRoot when shareCommonDir selfRoot otherRoot ->
          match Set.intersect selfSet (Set.ofList o.RepoRelativeFiles) |> Set.toList with
          | [] -> None
          | files -> Some { Other = o; Files = files }
        | _ -> None)

  let private relativeTime (now: DateTime) (past: DateTime) : string =
    let diff = now - past
    match diff.TotalSeconds < 60.0 with
    | true -> "just now"
    | false ->
      match diff.TotalMinutes < 60.0 with
      | true -> sprintf "%dm ago" (int diff.TotalMinutes)
      | false -> sprintf "%dh ago" (int diff.TotalHours)

  /// One line per advisory, e.g. "Note: session 77e1ab02 touched
  /// LiveTestActivity.fs 2m ago in agent-77e1 (worktree, branch xyz)".
  let format (now: DateTime) (overlap: Overlap) : string =
    let checkoutLabel = Checkout.describe overlap.Other.Checkout
    let fileList = overlap.Files |> String.concat ", "
    sprintf "Note: session %s touched %s %s in %s"
      overlap.Other.SessionId fileList (relativeTime now overlap.Other.LastActivity) checkoutLabel
