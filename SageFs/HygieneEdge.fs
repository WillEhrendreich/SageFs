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
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather

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

let private removeWorktree (ctx: EdgeContext) (path: string) (repo: string) (force: ForceNeed) : Task<Outcome> =
  task {
    match Directory.Exists path with
    | false ->
      let! _ = gitStep ctx.Git repo [ "worktree"; "prune" ]
      return Outcome.Done 0L
    | true ->
      let size = directorySize path
      // A lock that names a live process is a use; one whose process is gone is unlocked first.
      let! lockOutcome =
        task {
          match! ctx.Git repo [ "worktree"; "list"; "--porcelain" ] with
          | GitResult.Output text ->
            match Parse.worktreeList text |> List.tryFind (fun e -> Guard.normalize e.Path = Guard.normalize path) with
            | Some { Lock = LockState.LockedWith reason } ->
              match Parse.lockPid reason with
              | Some pid when ctx.IsAlive pid None -> return Result.Error(sprintf "locked by a live process (pid %d): %s" pid reason)
              | _ -> return! gitStep ctx.Git repo [ "worktree"; "unlock"; path ]
            | _ -> return Result.Ok()
          | _ -> return Result.Ok()
        }
      match lockOutcome with
      | Result.Error why -> return Outcome.Failed why
      | Result.Ok() ->
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
          | Result.Ok() -> return Outcome.Done size
          | Result.Error why -> return Outcome.Failed why
  }

let private deleteBranch (ctx: EdgeContext) (repo: string) (name: string) (how: BranchDeletion) : Task<Outcome> =
  task {
    match how with
    | BranchDeletion.IfMerged ->
      match! gitStep ctx.Git repo [ "branch"; "-d"; name ] with
      | Result.Ok() -> return Outcome.Done 0L
      | Result.Error why -> return Outcome.Failed why
    | BranchDeletion.BecauseMergedBy _ ->
      // `-D` does not check anything, so this does, right now.
      let mutable baseFound = None
      for b in baseBranchCandidates do
        match baseFound with
        | Some _ -> ()
        | None ->
          match! ctx.Git repo [ "rev-parse"; "--verify"; "--quiet"; "refs/heads/" + b ] with
          | GitResult.Output _ -> baseFound <- Some b
          | _ -> ()
      let! stillMerged =
        task {
          match baseFound with
          | None -> return false
          | Some b ->
            match! mergeEvidence ctx.Git repo b name with
            | MergeEvidence.MergedInto _ -> return true
            | _ -> return false
        }
      match stillMerged with
      | false -> return Outcome.Failed "the branch is no longer merged"
      | true ->
        match! gitStep ctx.Git repo [ "branch"; "-D"; name ] with
        | Result.Ok() -> return Outcome.Done 0L
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

let perform (ctx: EdgeContext) (operation: Operation) : Task<Outcome> =
  task {
    try
      match operation with
      | Operation.RemoveWorktree(accepted, repo, force) -> return! removeWorktree ctx (Guard.path accepted) repo force
      | Operation.RemoveTree accepted ->
        let path = Guard.path accepted
        let size = directorySize path
        Directory.Delete(path, true)
        return Outcome.Done size
      | Operation.RemoveFile accepted ->
        let path = Guard.path accepted
        let size = (try FileInfo(path).Length with _ -> 0L)
        File.Delete path
        return Outcome.Done size
      | Operation.DeleteBranch(repo, name, how) -> return! deleteBranch ctx repo name how
      | Operation.Terminate(pid, ticks) -> return terminate pid ticks
    with ex -> return Outcome.Failed ex.Message
  }

/// The effects the executor runs against the real machine, awaited. Every recheck is a fresh scan.
let effects (ctx: EdgeContext) : AsyncEffects =
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
    Perform = perform ctx }

let realContext (loc: Locations) (live: unit -> LiveFacts) : EdgeContext =
  { MakeScan = fun () -> realScan loc (live ())
    Git = runGit
    IsAlive = fun pid ticks -> OwnerMonitor.isAlive OwnerMonitor.getProcessById { Pid = pid; StartTimeTicks = ticks } }
