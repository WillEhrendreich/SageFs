/// Salvage refs: where a worktree's uncommitted files are kept (`HygieneSalvage`), and when they may go.
///
/// They live under `refs/sagefs/salvage/<name>`, not on a branch: `git push --all` sends `refs/heads` and would publish a `.env`
/// or a `*.db` that a salvage committed, and nothing a person did asked for that. (`git push --mirror` sends every ref; that one is
/// a person's explicit choice.)
///
/// Without an end they would only pile up, so the sweep lists them and reaps the ones that are finished with: the worktree they
/// came from is gone and the base branch has everything on them, or it has been gone for the retention and nobody took the work.
/// A salvage whose worktree is still there is never reaped, and neither is one the base does not have in full before its
/// retention. Everything here is pure; the edge lists the refs and asks git.
module SageFs.SalvageRefs

open System

let refPrefix : string = "refs/sagefs/salvage/"

/// One salvage ref: the worktree directory name it is named for, the commit it points at, and when that commit was made.
type SalvageRef =
  { Name: string
    Ref: string
    Sha: string
    At: DateTime }

/// Whether the worktree a salvage ref came from is still on disk.
[<RequireQualifiedAccess>]
type WorktreeOfIt =
  | StillThere
  | Gone
  /// git would not list the worktrees, so nothing can be said.
  | NotListed

/// Whether the base branch has everything on the salvage commit, by history, patch id or content.
[<RequireQualifiedAccess>]
type BaseHolds =
  | EverythingOnIt
  | NotEverything
  | CouldNotTell

[<RequireQualifiedAccess>]
type Verdict =
  | Keep of reason: string
  | Reap of reason: string

let private days (span: TimeSpan) : string = sprintf "%.0f day(s)" span.TotalDays

/// What becomes of one salvage ref. Total, and it only ever reaps when the worktree is known to be gone.
let decide (now: DateTime) (retention: TimeSpan) (worktree: WorktreeOfIt) (holds: BaseHolds) (salvage: SalvageRef) : Verdict =
  match worktree with
  | WorktreeOfIt.StillThere -> Verdict.Keep "its worktree is still there"
  | WorktreeOfIt.NotListed -> Verdict.Keep "git would not list the worktrees, so it is not known to be finished with"
  | WorktreeOfIt.Gone ->
    match holds with
    | BaseHolds.EverythingOnIt -> Verdict.Reap "its worktree is gone and the base branch has everything on it"
    | BaseHolds.NotEverything
    | BaseHolds.CouldNotTell ->
      let age = now - salvage.At
      match age >= retention with
      | true -> Verdict.Reap(sprintf "its worktree has been gone and nobody took the work for %s" (days age))
      | false ->
        Verdict.Keep(sprintf "its worktree is gone but the base branch does not have all of it; look with `git log %s`, it is reaped after %s" salvage.Ref (days retention))

/// What asking git for the salvage refs came to: the refs, or why they could not be listed (which is not "there are none").
[<RequireQualifiedAccess>]
type Listing =
  | Listed of SalvageRef list
  | NotListed of why: string

/// The listing every surface shows: how many salvage refs there are, how old the oldest is, and what keeps them from being published.
let describe (now: DateTime) (listing: Listing) : string option =
  match listing with
  | Listing.NotListed why -> Some(sprintf "The salvage refs under %s could not be listed (%s)." refPrefix why)
  | Listing.Listed [] -> None
  | Listing.Listed refs ->
    let oldest = refs |> List.map (fun r -> now - r.At) |> List.max
    Some(
      sprintf
        "%d salvage ref(s) hold uncommitted files saved from worktrees, oldest %s (`git for-each-ref %s`): they are under refs/sagefs, which `git push --all` does not send, and the sweep reaps one once its worktree is gone and the base has it."
        (List.length refs) (days oldest) refPrefix)
