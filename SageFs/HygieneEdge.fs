/// The hands of workspace hygiene: the real effects `Executor.run` is given. `Recheck` looks at ONE target
/// again (a fresh scan, so the process table and git state are current); `Perform` carries out one operation
/// the domain built from a path that already passed the path gate.
///
/// Each operation that could lose something checks again itself, because the window between the executor's
/// recheck and the operation is real: `git worktree remove` without force refuses a worktree with changes,
/// a forced removal is only done after the status shows nothing but build output, a `-D` branch delete only
/// after the merge evidence still holds, and a process is only stopped if it is the process that was looked at.
module SageFs.HygieneEdge

open System
open System.IO
open System.Threading.Tasks
open SageFs
open SageFs.Utils
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.HygieneLedger

/// Follow every symlink in a path, so the gate sees where a removal would really land (`HygieneFs`).
let resolvePath (path: string) : Result<string, ResolveFailure> = HygieneFs.resolvePath path


type EdgeContext =
  { MakeScan: unit -> Scan
    Git: Git
    IsAlive: int -> int64 option -> bool }

let private gitStep (git: Git) (dir: string) (args: string list) : Task<Result<unit, string>> =
  task {
    match! git dir args with
    | GitResult.Output _ -> return Result.Ok()
    | GitResult.Exit(_, stderr) -> return Result.Error(sprintf "git %s: %s" (String.Join(" ", args)) stderr)
    | GitResult.Unavailable detail -> return Result.Error detail
  }

/// Where the edge writes down what it did. A line is written after each success, with where the tip was, so what the daemon
/// tidied can be put back (`HygieneLedger`).
type Journal = LedgerLine -> Result<unit, string>

/// For a caller that keeps no record (a test that is not about the record).
let noJournal : Journal = fun _ -> Result.Ok()

/// Write one line. A line that could not be written is said in the daemon's log and never dropped quietly: the removal it records
/// has happened, and the ledger now lacks it (the sweep's own record of what vanished covers the gap).
let private record (journal: Journal) (event: LedgerEvent) : unit =
  match journal { At = DateTime.UtcNow; Event = event } with
  | Result.Ok() -> ()
  | Result.Error why -> Log.warn "[hygiene] could not write a ledger line for %A: %s" (pathOf event) why

/// The commit a revision names, or that it names none.
let tipOf (git: Git) (dir: string) (revision: string) : Task<Tip> =
  task {
    match! git dir [ "rev-parse"; "--verify"; "--quiet"; revision ] with
    | GitResult.Output out when out.Trim() <> "" -> return Tip.AtTip(out.Trim())
    | GitResult.Output _
    | GitResult.Exit _
    | GitResult.Unavailable _ -> return Tip.TipUnknown
  }

/// The branch the base is judged by (`master`, else `main`), when the repository has one.
let private findBase (ctx: EdgeContext) (repo: string) : Task<string option> = baseBranch ctx.Git repo

/// How a revision reached the base right now, for the record and for deciding whether a restore point is needed.
let mergeStateAt (ctx: EdgeContext) (repo: string) (revision: string) : Task<MergeState> =
  task {
    match! findBase ctx repo with
    | None -> return MergeState.Undecided
    | Some b ->
      match! mergeEvidence ctx.Git repo b revision with
      | MergeEvidence.MergedInto how -> return MergeState.MergedBy how
      | MergeEvidence.UnmergedCommits _ -> return MergeState.NotMerged
      // Git could not say. That is not "not merged" and it is not "merged": nothing may be assumed either way.
      | MergeEvidence.MergeNotDecidable _ -> return MergeState.Undecided
  }

/// The ref that keeps a removed tip reachable. A name already taken by another commit gets the commit's own start added,
/// so a second removal under one name never moves the first one's restore point.
let private restoreRefFor (git: Git) (repo: string) (name: string) (sha: string) : Task<string> =
  task {
    let plain = "refs/sagefs/removed/" + name
    match! git repo [ "rev-parse"; "--verify"; "--quiet"; plain ] with
    | GitResult.Output out when out.Trim() <> sha -> return sprintf "%s-%s" plain (sha.Substring(0, min 8 sha.Length))
    | GitResult.Output _
    | GitResult.Exit _
    | GitResult.Unavailable _ -> return plain
  }

/// Make a commit reachable by a ref of our own before the only ref that names it goes, unless the base's history already contains
/// it (an ancestor) or there is nothing of anybody's in it. That covers a commit the base only has by content (a rebase, a squash,
/// an identical tree) and, because the edge fails closed, one git could not place at all. Nothing is removed unless this
/// succeeds, and a tip that cannot be read cannot be kept, so it is a refusal too.
let keepRestorePoint (ctx: EdgeContext) (repo: string) (name: string) (tip: Tip) (merged: MergeState) : Task<Result<unit, string>> =
  task {
    match merged, tip with
    | MergeState.MergedBy MergeHow.Ancestor, _
    | MergeState.NotAboutWork, _ -> return Result.Ok()
    | MergeState.MergedBy MergeHow.PatchEquivalent, Tip.AtTip sha
    | MergeState.MergedBy MergeHow.TreeEqual, Tip.AtTip sha
    | MergeState.NotMerged, Tip.AtTip sha
    | MergeState.Undecided, Tip.AtTip sha ->
      let! restoreRef = restoreRefFor ctx.Git repo name sha
      match! gitStep ctx.Git repo [ "update-ref"; restoreRef; sha ] with
      | Result.Ok() -> return Result.Ok()
      | Result.Error why -> return Result.Error(sprintf "no restore point could be made (%s), so nothing was removed" why)
    | MergeState.MergedBy MergeHow.PatchEquivalent, Tip.TipUnknown
    | MergeState.MergedBy MergeHow.TreeEqual, Tip.TipUnknown
    | MergeState.NotMerged, Tip.TipUnknown
    | MergeState.Undecided, Tip.TipUnknown ->
      return Result.Error "the commit it points at could not be read, so no restore point could be made and nothing was removed"
  }

let private removeWorktreeWith (journal: Journal) (ctx: EdgeContext) (path: string) (repo: string) (force: ForceNeed) : Task<Outcome> =
  task {
    match Directory.Exists path with
    | false ->
      let! _ = gitStep ctx.Git repo [ "worktree"; "prune" ]
      return Outcome.Done 0L
    | true ->
      let size = directorySize path
      // Read before anything is removed: afterwards there is nothing to ask.
      let! tip = tipOf ctx.Git path "HEAD"
      let! merged =
        task {
          match tip with
          | Tip.AtTip sha -> return! mergeStateAt ctx repo sha
          | Tip.TipUnknown -> return MergeState.Undecided
        }
      let! branch =
        task {
          match! ctx.Git path [ "rev-parse"; "--abbrev-ref"; "HEAD" ] with
          | GitResult.Output out when out.Trim() <> "" && out.Trim() <> "HEAD" -> return BranchLabel.OnBranch(out.Trim())
          | GitResult.Output _
          | GitResult.Exit _
          | GitResult.Unavailable _ ->
            match tip with
            | Tip.AtTip sha -> return BranchLabel.DetachedAt sha
            | Tip.TipUnknown -> return BranchLabel.NoBranch
        }
      // The executor judged this worktree merged from a look a moment ago. If git cannot confirm that now, nothing is assumed and
      // nothing is removed: a worktree that stays costs disk, one removed on a guess can cost a commit.
      let! restorePoint =
        match merged with
        | MergeState.Undecided -> Task.FromResult(Result.Error "git could not say whether the work has reached the base branch, so the worktree was not removed")
        | MergeState.MergedBy _
        | MergeState.NotMerged
        | MergeState.NotAboutWork -> keepRestorePoint ctx repo (Path.GetFileName(path.TrimEnd('/', '\\'))) tip merged
      // Only the harness's own lock (`claude agent <name> (pid N start M)`) is ours to release, and only when its process is
      // gone. Any other lock is a person's `git worktree lock`, git's own "do not prune this", and is never overridden.
      let! lockOutcome =
        task {
          match! ctx.Git repo [ "worktree"; "list"; "--porcelain" ] with
          | GitResult.Output text ->
            match Parse.worktreeList text |> List.tryFind (fun e -> Guard.normalize e.Path = Guard.normalize path) with
            | Some { Lock = LockState.LockedWith reason } ->
              match Parse.lockMaker reason with
              | LockMaker.Agent pid when ctx.IsAlive pid None -> return Result.Error(sprintf "locked by a live process (pid %d): %s" pid reason)
              | LockMaker.Agent _ -> return! gitStep ctx.Git repo [ "worktree"; "unlock"; path ]
              | LockMaker.Person ->
                return Result.Error(sprintf "locked by a person (%s): `git worktree unlock %s` releases it" reason path)
            | _ -> return Result.Ok()
          | _ -> return Result.Ok()
        }
      match restorePoint, lockOutcome with
      | Result.Error why, _
      | _, Result.Error why -> return Outcome.Failed why
      | Result.Ok(), Result.Ok() ->
        let! forceArgs =
          task {
            match force with
            | ForceNeed.NoForce -> return Result.Ok []
            | ForceNeed.DisposableCheckout -> return Result.Ok [ "--force" ]
            | ForceNeed.OnlyGeneratedFilesDiffer ->
              match! changedFiles ctx.Git path with
              | Result.Error why -> return Result.Error(UnknownReason.describe why)
              | Result.Ok files ->
                match files |> List.forall (fun f -> f.Origin <> ChangeOrigin.Real) with
                | true -> return Result.Ok [ "--force" ]
                | false -> return Result.Error "real changes appeared after the plan was made"
          }
        match forceArgs with
        | Result.Error why -> return Outcome.Failed why
        | Result.Ok flags ->
          match! gitStep ctx.Git repo ([ "worktree"; "remove" ] @ flags @ [ path ]) with
          | Result.Ok() ->
            record
              journal
              (LedgerEvent.Removed
                { Kind = LeftoverKind.AgentWorktree
                  Target = path
                  Branch = branch
                  Tip = tip
                  Merged = merged
                  Why = "a finished worktree, merged into the base, removed by hygiene" })
            return Outcome.Done size
          | Result.Error why -> return Outcome.Failed why
  }

let private deleteBranchWith (journal: Journal) (ctx: EdgeContext) (repo: string) (name: string) (how: BranchDeletion) : Task<Outcome> =
  task {
    let! tip = tipOf ctx.Git repo ("refs/heads/" + name)
    let removed (merged: MergeState) =
      record
        journal
        (LedgerEvent.Removed
          { Kind = LeftoverKind.StaleBranch
            Target = Target.describe (Target.GitBranch(repo, name))
            Branch = BranchLabel.OnBranch name
            Tip = tip
            Merged = merged
            Why = "a branch merged into the base, deleted by hygiene" })
    match how with
    | BranchDeletion.IfMerged ->
      match! gitStep ctx.Git repo [ "branch"; "-d"; name ] with
      | Result.Ok() ->
        removed (MergeState.MergedBy MergeHow.Ancestor)
        return Outcome.Done 0L
      | Result.Error why -> return Outcome.Failed why
    | BranchDeletion.BecauseMergedBy _ ->
      // `-D` does not check anything, so this does, right now.
      let! merged = mergeStateAt ctx repo name
      match merged with
      | MergeState.NotMerged
      | MergeState.NotAboutWork -> return Outcome.Failed "the branch is no longer merged"
      | MergeState.Undecided -> return Outcome.Failed "git could not say whether the branch is merged, so it was not deleted"
      | MergeState.MergedBy _ ->
        match! keepRestorePoint ctx repo name tip merged with
        | Result.Error why -> return Outcome.Failed why
        | Result.Ok() ->
          match! gitStep ctx.Git repo [ "branch"; "-D"; name ] with
          | Result.Ok() ->
            removed merged
            return Outcome.Done 0L
          | Result.Error why -> return Outcome.Failed why
  }

let private terminate (pid: int) (startTicks: int64) : Outcome =
  match OwnerMonitor.getProcessById pid with
  | None -> Outcome.Done 0L
  | Some p ->
    try
      match OwnerMonitor.fenceMatches startTicks (OwnerMonitor.startTimeTicksOf p) with
      | false -> Outcome.Failed "the pid is a different process now"
      | true ->
        p.Kill()
        Outcome.Done 0L
    with ex -> Outcome.Failed ex.Message

/// What a removed file or directory was, from the root it was under and where in it: a worker log, a registry entry, a temp
/// run, a cache entry, a gate checkout.
let private kindUnder (accepted: Accepted) : LeftoverKind =
  match Guard.root accepted with
  | RootKind.AgentWorktrees -> LeftoverKind.AgentWorktree
  | RootKind.GateState -> LeftoverKind.GateCheckout
  | RootKind.HostCache -> LeftoverKind.HostCacheEntry
  | RootKind.TempRuns -> LeftoverKind.TempRunDir
  | RootKind.DataDir ->
    match (Guard.path accepted).Contains "/spawned/" with
    | true -> LeftoverKind.SpawnedRegistryEntry
    | false -> LeftoverKind.WorkerLogFile

let private removedPath (journal: Journal) (accepted: Accepted) : unit =
  record
    journal
    (LedgerEvent.Removed
      { Kind = kindUnder accepted
        Target = Guard.path accepted
        Branch = BranchLabel.NoBranch
        Tip = Tip.TipUnknown
        Merged = MergeState.NotAboutWork
        Why = "left behind by a run that is over, removed by hygiene" })

let performWith (journal: Journal) (ctx: EdgeContext) (operation: Operation) : Task<Outcome> =
  task {
    try
      match operation with
      | Operation.RemoveWorktree(accepted, repo, force) -> return! removeWorktreeWith journal ctx (Guard.path accepted) repo force
      | Operation.RemoveTree accepted ->
        let path = Guard.path accepted
        let size = directorySize path
        Directory.Delete(path, true)
        removedPath journal accepted
        return Outcome.Done size
      | Operation.RemoveFile accepted ->
        let path = Guard.path accepted
        let size = (try FileInfo(path).Length with _ -> 0L)
        File.Delete path
        removedPath journal accepted
        return Outcome.Done size
      | Operation.DeleteBranch(repo, name, how) -> return! deleteBranchWith journal ctx repo name how
      | Operation.Terminate(pid, ticks) -> return terminate pid ticks
    with ex -> return Outcome.Failed ex.Message
  }

let perform (ctx: EdgeContext) (operation: Operation) : Task<Outcome> = performWith noJournal ctx operation

/// The effects the executor runs against the real machine, awaited, each success written to `journal`. Every recheck is a
/// fresh scan.
let effectsWith (journal: Journal) (ctx: EdgeContext) : AsyncEffects =
  { Recheck =
      fun target ->
        task {
          try
            match! gather (ctx.MakeScan()) (Some target) with
            | [] -> return Rechecked.Gone
            | leftover :: _ -> return Rechecked.Fresh leftover
          with ex -> return Rechecked.CouldNotLook ex.Message
        }
    Resolve = resolvePath
    Perform = performWith journal ctx }

/// The effects with no record kept.
let effects (ctx: EdgeContext) : AsyncEffects = effectsWith noJournal ctx

let realContext (loc: Locations) (live: unit -> LiveFacts) : EdgeContext =
  { MakeScan = fun () -> realScan loc (live ())
    Git = runGit
    IsAlive = fun pid ticks -> OwnerMonitor.isAlive OwnerMonitor.getProcessById { Pid = pid; StartTimeTicks = ticks } }
