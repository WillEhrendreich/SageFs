/// `sagefs hygiene --rescue <name>`: put the work of a worktree on a branch of its own, on top of the base branch, so it can be
/// merged, pushed or looked at without the worktree.
///
/// What it makes is `rescue/<name>`. Its commits are the worktree's own commits that the base lacks (the ones `git cherry` marks
/// `+`), replayed onto the base in a temporary detached worktree of ours, and then the worktree's uncommitted files as one more
/// commit (the salvage commit, `HygieneSalvage`), merged in memory. The worktree the person named is never touched: salvage uses
/// a temporary index and the replay happens in another worktree. A replay that conflicts is stopped and the branch is left on
/// the worktree's own tip, not rebased, with the commit that conflicted named: the work is on a branch either way.
///
/// It also says whether the base already has the change, because a rescue branch that adds nothing is only noise: by history
/// (the work is an ancestor of the base), by patch id (every commit has an equivalent on the base, a rebase or squash) or by
/// content (merging the work into the base changes nothing).
module SageFs.HygieneRescue

open System
open System.IO
open System.Threading.Tasks
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather

/// What the base branch already has of the work.
[<RequireQualifiedAccess>]
type BaseHas =
  /// The work is an ancestor of the base: it was merged or fast-forwarded.
  | AllByHistory
  /// Every commit has a patch-equivalent on the base (a rebase or a squash).
  | AllByPatch
  /// Merging the work into the base changes nothing: the base has its content, whatever its history.
  | AllByContent
  /// The base lacks this many commits' worth of the work.
  | NotAll of commits: int
  | CouldNotTell of why: string

[<RequireQualifiedAccess>]
type Replay =
  /// The work was replayed onto the base.
  | OntoBase of baseRef: string
  /// A step conflicted with the base: the branch is on the worktree's own tip, and this is what conflicted (a commit, or the
  /// uncommitted files).
  | ConflictedAt of what: string
  /// Nothing needed replaying: the base already has every commit and there are no uncommitted files.
  | NothingToReplay

/// What became of the temporary worktree the replay ran in. One that could not be removed is said, with where it is, because it
/// is a directory the person now has to know about.
[<RequireQualifiedAccess>]
type TempWorktree =
  | NoneNeeded
  | Removed
  | Left of path: string * why: string

[<RequireQualifiedAccess>]
type RescueResult =
  | Rescued of branch: string * sha: string * Replay * BaseHas * TempWorktree
  | NoSuchWorktree of name: string * known: string list
  | CouldNotRescue of why: string

/// What a rescue needs from the world: git, with and without an environment.
type RescueContext =
  { Git: Git
    GitEnv: GitWithEnv
    Repo: string
    /// A directory for the temporary worktree the replay runs in.
    ScratchDir: string }

let private identity : (string * string) list =
  [ "GIT_AUTHOR_NAME", "SageFs hygiene"
    "GIT_AUTHOR_EMAIL", "hygiene@sagefs.local"
    "GIT_COMMITTER_NAME", "SageFs hygiene"
    "GIT_COMMITTER_EMAIL", "hygiene@sagefs.local" ]

let private text (result: GitResult) : Result<string, string> =
  match result with
  | GitResult.Output out -> Result.Ok(out.Trim())
  | GitResult.Exit(code, stderr) -> Result.Error(sprintf "git exited %d: %s" code stderr)
  | GitResult.Unavailable detail -> Result.Error detail

let private branchName (name: string) : string = "rescue/" + name

let private leaf (path: string) : string = Path.GetFileName(path.TrimEnd('/', '\\'))

let private findBase (git: Git) (repo: string) : Task<Result<string, string>> =
  task {
    match! baseBranch git repo with
    | Some b -> return Result.Ok b
    | None -> return Result.Error "the repository has no master or main branch to rebase onto"
  }

/// What the base has of `tip`: by history, by patch, by content, or not all of it.
let baseHas (git: Git) (repo: string) (baseRef: string) (tip: string) : Task<BaseHas> =
  task {
    match! mergeEvidence git repo baseRef tip with
    | MergeEvidence.MergedInto MergeHow.Ancestor -> return BaseHas.AllByHistory
    | MergeEvidence.MergedInto MergeHow.PatchEquivalent -> return BaseHas.AllByPatch
    | MergeEvidence.MergedInto MergeHow.TreeEqual -> return BaseHas.AllByContent
    | MergeEvidence.MergeNotDecidable why -> return BaseHas.CouldNotTell(UnknownReason.describe why)
    | MergeEvidence.UnmergedCommits commits ->
      // The commits are not equivalent one by one; the work can still change nothing when merged (a squash that also took
      // other changes, a hand copy). Merging in memory says so: a merge that yields the base's own tree adds nothing.
      match! git repo [ "merge-tree"; "--write-tree"; baseRef; tip ] with
      | GitResult.Output merged ->
        match! git repo [ "rev-parse"; baseRef + "^{tree}" ] with
        | GitResult.Output baseTree when baseTree.Trim() = (merged.Trim().Split('\n')).[0].Trim() -> return BaseHas.AllByContent
        | GitResult.Output _
        | GitResult.Exit _
        | GitResult.Unavailable _ -> return BaseHas.NotAll(NonEmpty.length commits)
      | GitResult.Exit _
      | GitResult.Unavailable _ -> return BaseHas.NotAll(NonEmpty.length commits)
  }

let private find (entries: WorktreeEntry list) (name: string) : WorktreeEntry option =
  entries |> List.tryFind (fun e -> leaf e.Path = name || Guard.normalize e.Path = Guard.normalize name)

/// The commits of `tip` that the base lacks, oldest first.
let private commitsToReplay (git: Git) (repo: string) (baseRef: string) (tip: string) : Task<Result<string list, string>> =
  task {
    match! git repo [ "cherry"; baseRef; tip ] with
    | GitResult.Output out ->
      return
        out.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
        |> List.choose (fun line -> match line.Split(' ') with | [| "+"; sha |] -> Some sha | _ -> None)
        |> Result.Ok
    | GitResult.Exit(code, stderr) -> return Result.Error(sprintf "git cherry exited %d: %s" code stderr)
    | GitResult.Unavailable detail -> return Result.Error detail
  }

/// What replaying commits onto the base came to.
[<RequireQualifiedAccess>]
type private Replayed =
  | Tip of sha: string
  | Conflicted of sha: string
  | Failed of why: string

/// Replay `shas` onto `baseRef` in a worktree of our own and say where it ended, or which commit conflicted.
let private replay (ctx: RescueContext) (baseRef: string) (shas: string list) : Task<Replayed * TempWorktree> =
  task {
    let temp = Path.Combine(ctx.ScratchDir, sprintf "sagefs-rescue-%s" (Guid.NewGuid().ToString "N"))
    let step args = ctx.GitEnv temp identity args
    match! ctx.Git ctx.Repo [ "worktree"; "add"; "--detach"; temp; baseRef ] with
    | GitResult.Exit(code, stderr) ->
      return Replayed.Failed(sprintf "could not make a temporary worktree (git exited %d: %s)" code stderr), TempWorktree.NoneNeeded
    | GitResult.Unavailable detail -> return Replayed.Failed detail, TempWorktree.NoneNeeded
    | GitResult.Output _ ->
      // Each pick runs only when every pick before it went through; the first one that does not ends the replay.
      let! outcome =
        shas
        |> List.fold
          (fun (stopped: Task<Replayed option>) sha ->
            task {
              match! stopped with
              | Some _ as halted -> return halted
              | None ->
                match! step [ "cherry-pick"; "--keep-redundant-commits"; sha ] with
                | GitResult.Output _ -> return None
                | GitResult.Exit _ ->
                  let! _ = step [ "cherry-pick"; "--abort" ]
                  return Some(Replayed.Conflicted sha)
                | GitResult.Unavailable detail -> return Some(Replayed.Failed detail)
            })
          (Task.FromResult None)
      let! finished =
        task {
          match outcome with
          | Some stopped -> return stopped
          | None ->
            match! step [ "rev-parse"; "HEAD" ] with
            | GitResult.Output out -> return Replayed.Tip(out.Trim())
            | GitResult.Exit(code, stderr) -> return Replayed.Failed(sprintf "git exited %d: %s" code stderr)
            | GitResult.Unavailable detail -> return Replayed.Failed detail
        }
      // The temporary worktree is ours alone, and its removal is forced.
      let! removal = ctx.Git ctx.Repo [ "worktree"; "remove"; "--force"; temp ]
      let cleanup =
        match removal with
        | GitResult.Output _ -> TempWorktree.Removed
        | GitResult.Exit(code, stderr) -> TempWorktree.Left(temp, sprintf "git exited %d: %s" code stderr)
        | GitResult.Unavailable detail -> TempWorktree.Left(temp, detail)
      return finished, cleanup
  }

/// Point the rescue branch at `sha`. A rescue branch that already exists somewhere else is never moved: what is on it may be
/// work somebody has since added.
let private setBranch (ctx: RescueContext) (branch: string) (sha: string) : Task<Result<unit, string>> =
  task {
    match! ctx.Git ctx.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/heads/" + branch ] with
    | GitResult.Output existing when existing.Trim() <> sha ->
      return
        Result.Error(
          sprintf "%s already exists at %s. Look at it, then delete or rename it and run the rescue again." branch (existing.Trim().Substring(0, min 10 (existing.Trim().Length))))
    | GitResult.Output _
    | GitResult.Exit _
    | GitResult.Unavailable _ ->
      match! ctx.Git ctx.Repo [ "update-ref"; "refs/heads/" + branch; sha ] with
      | GitResult.Output _ -> return Result.Ok()
      | GitResult.Exit(_, stderr) -> return Result.Error(sprintf "%s could not be made: %s" branch stderr)
      | GitResult.Unavailable detail -> return Result.Error detail
  }

/// Add the uncommitted files (the salvage commit's changes against the worktree's HEAD) on top of `tip`, as one commit, by a
/// merge made in memory. A conflict is `None` and nothing is committed.
let private addUncommitted (ctx: RescueContext) (tip: string) (headSha: string) (salvage: string) (name: string) : Task<Result<string option, string>> =
  task {
    match! ctx.Git ctx.Repo [ "merge-tree"; "--write-tree"; "--merge-base=" + headSha; tip; salvage ] with
    | GitResult.Exit(1, _) -> return Result.Ok None
    | GitResult.Exit(code, stderr) -> return Result.Error(sprintf "git merge-tree exited %d: %s" code stderr)
    | GitResult.Unavailable detail -> return Result.Error detail
    | GitResult.Output out ->
      let tree = (out.Trim().Split('\n')).[0].Trim()
      let message = sprintf "salvage: the uncommitted files of %s" name
      match! ctx.GitEnv ctx.Repo identity [ "commit-tree"; tree; "-p"; tip; "-m"; message ] with
      | GitResult.Output sha -> return Result.Ok(Some(sha.Trim()))
      | GitResult.Exit(code, stderr) -> return Result.Error(sprintf "git commit-tree exited %d: %s" code stderr)
      | GitResult.Unavailable detail -> return Result.Error detail
  }

/// The sentence about a temporary worktree that is still on disk, or nothing when it is gone.
let private leftNote (cleanup: TempWorktree) : string =
  match cleanup with
  | TempWorktree.NoneNeeded
  | TempWorktree.Removed -> ""
  | TempWorktree.Left(path, why) -> sprintf " A temporary worktree is still at %s (%s); `git worktree remove --force` it." path why

/// Make `rescue/<name>` for the worktree called `name`.
let rescue (ctx: RescueContext) (name: string) : Task<RescueResult> =
  task {
    match! ctx.Git ctx.Repo [ "worktree"; "list"; "--porcelain" ] with
    | GitResult.Exit(code, stderr) -> return RescueResult.CouldNotRescue(sprintf "git worktree list exited %d: %s" code stderr)
    | GitResult.Unavailable detail -> return RescueResult.CouldNotRescue detail
    | GitResult.Output listing ->
      let entries = Parse.worktreeList listing
      match find entries name with
      | None -> return RescueResult.NoSuchWorktree(name, entries |> List.map (fun e -> leaf e.Path))
      | Some entry ->
        let path = entry.Path
        let branch = branchName (leaf path)
        match! findBase ctx.Git ctx.Repo with
        | Result.Error why -> return RescueResult.CouldNotRescue why
        | Result.Ok baseRef ->
          // Save the uncommitted files first: they are part of the work, and this makes them a commit.
          let! salvaged = HygieneSalvage.salvage ctx.GitEnv path
          let! head = ctx.Git path [ "rev-parse"; "HEAD" ]
          match text head, salvaged with
          | Result.Error why, _ -> return RescueResult.CouldNotRescue why
          | _, HygieneSalvage.SalvageResult.CouldNotSalvage why ->
            return RescueResult.CouldNotRescue(sprintf "the uncommitted files could not be saved first: %s" why)
          | Result.Ok headSha, _ ->
            let uncommitted =
              match salvaged with
              | HygieneSalvage.SalvageResult.Salvaged(_, sha, _)
              | HygieneSalvage.SalvageResult.AlreadySalvaged(_, sha) -> Some sha
              | HygieneSalvage.SalvageResult.NothingToSalvage
              | HygieneSalvage.SalvageResult.CouldNotSalvage _ -> None
            let whole = defaultArg uncommitted headSha
            let! has = baseHas ctx.Git ctx.Repo baseRef whole
            match! commitsToReplay ctx.Git ctx.Repo baseRef headSha with
            | Result.Error why -> return RescueResult.CouldNotRescue why
            | Result.Ok [] when uncommitted.IsNone ->
              // The base has every commit and nothing is uncommitted: the branch is the worktree's own tip.
              match! setBranch ctx branch headSha with
              | Result.Ok() -> return RescueResult.Rescued(branch, headSha, Replay.NothingToReplay, has, TempWorktree.NoneNeeded)
              | Result.Error why -> return RescueResult.CouldNotRescue why
            | Result.Ok shas ->
              let! (replayed, cleanup) = replay ctx baseRef shas
              match replayed with
              | Replayed.Failed why -> return RescueResult.CouldNotRescue(why + leftNote cleanup)
              | Replayed.Conflicted commit ->
                match! setBranch ctx branch whole with
                | Result.Ok() -> return RescueResult.Rescued(branch, whole, Replay.ConflictedAt commit, has, cleanup)
                | Result.Error why -> return RescueResult.CouldNotRescue(why + leftNote cleanup)
              | Replayed.Tip tip ->
                match uncommitted with
                | None ->
                  match! setBranch ctx branch tip with
                  | Result.Ok() -> return RescueResult.Rescued(branch, tip, Replay.OntoBase baseRef, has, cleanup)
                  | Result.Error why -> return RescueResult.CouldNotRescue(why + leftNote cleanup)
                | Some salvage ->
                  match! addUncommitted ctx tip headSha salvage (leaf path) with
                  | Result.Error why -> return RescueResult.CouldNotRescue(why + leftNote cleanup)
                  | Result.Ok None ->
                    match! setBranch ctx branch whole with
                    | Result.Ok() -> return RescueResult.Rescued(branch, whole, Replay.ConflictedAt "the uncommitted files", has, cleanup)
                    | Result.Error why -> return RescueResult.CouldNotRescue(why + leftNote cleanup)
                  | Result.Ok(Some withFiles) ->
                    match! setBranch ctx branch withFiles with
                    | Result.Ok() -> return RescueResult.Rescued(branch, withFiles, Replay.OntoBase baseRef, has, cleanup)
                    | Result.Error why -> return RescueResult.CouldNotRescue(why + leftNote cleanup)
  }

let private describeHas (has: BaseHas) (baseRef: string) : string =
  match has with
  | BaseHas.AllByHistory -> sprintf "%s already has all of it: it is an ancestor of %s." baseRef baseRef
  | BaseHas.AllByPatch -> sprintf "%s already has all of it by patch id: every commit has an equivalent there (a rebase or squash)." baseRef
  | BaseHas.AllByContent -> sprintf "%s already has all of it by content: merging it changes nothing." baseRef
  | BaseHas.NotAll commits -> sprintf "%s does not have all of it: %d commit(s) are new." baseRef commits
  | BaseHas.CouldNotTell why -> sprintf "Whether %s has it could not be told: %s" baseRef why

/// What `sagefs hygiene --rescue` prints.
let describe (result: RescueResult) : string =
  match result with
  | RescueResult.Rescued(branch, sha, replay, has, cleanup) ->
    let baseRef, how =
      match replay with
      | Replay.OntoBase b -> b, sprintf "Replayed onto %s." b
      | Replay.ConflictedAt what ->
        "the base",
        sprintf "Replaying %s onto the base conflicted, so the branch is on the worktree's own tip, not rebased. Rebase it by hand: git rebase <base> %s" what branch
      | Replay.NothingToReplay -> "the base", "Nothing needed replaying."
    String.Join("\n", [ sprintf "Rescued to %s at %s. %s%s" branch (sha.Substring(0, min 10 sha.Length)) how (leftNote cleanup); describeHas has baseRef ])
  | RescueResult.NoSuchWorktree(name, known) ->
    sprintf "No worktree is called %s. This repository has: %s." name (match known with | [] -> "none" | names -> String.Join(", ", names))
  | RescueResult.CouldNotRescue why -> sprintf "Could not rescue: %s" why
