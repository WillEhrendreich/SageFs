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
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather

/// Follow every symlink in a path, so the gate sees where a removal would really land. A path that does not
/// exist resolves to itself; one that loops, or goes too deep, is an error.
let resolvePath (path: string) : Result<string, ResolveFailure> =
  // The kernel's own bound on symlink chains is 40 on Linux; a path that needs more is looping.
  let maxLinks = 40
  let rec walk (current: string) (remaining: string list) (links: int) : Result<string, ResolveFailure> =
    match remaining with
    | [] -> Result.Ok current
    | segment :: rest ->
      let next = (match current with | "/" -> "/" + segment | _ -> current + "/" + segment)
      let target =
        try
          match FileInfo(next).LinkTarget with
          | null -> None
          | t -> Some t
        with _ -> None
      match target with
      | None -> walk next rest links
      | Some t ->
        match links >= maxLinks with
        | true -> Result.Error(ResolveFailure.TooManyLinks path)
        | false ->
          let absolute = (match Path.IsPathRooted t with | true -> t | false -> Guard.normalize (current + "/" + t))
          let parts = Guard.normalize absolute |> fun p -> p.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
          walk "/" (parts @ rest) (links + 1)
  try
    let parts = Guard.normalize path |> fun p -> p.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    walk "/" parts 0
  with ex -> Result.Error(ResolveFailure.Unreadable(path, ex.Message))

type EdgeContext =
  { MakeScan: unit -> Scan
    Git: Git
    IsAlive: int -> int64 option -> bool }

let private gitStep (git: Git) (dir: string) (args: string list) : Result<unit, string> =
  match git dir args with
  | GitResult.Output _ -> Result.Ok()
  | GitResult.Exit(_, stderr) -> Result.Error(sprintf "git %s: %s" (String.Join(" ", args)) stderr)
  | GitResult.Unavailable detail -> Result.Error detail

let private removeWorktree (ctx: EdgeContext) (path: string) (repo: string) (force: ForceNeed) : Outcome =
  match Directory.Exists path with
  | false ->
    gitStep ctx.Git repo [ "worktree"; "prune" ] |> ignore
    Outcome.Done 0L
  | true ->
    let size = directorySize path
    // A lock that names a live process is a use; one whose process is gone is unlocked first.
    let lockOutcome =
      match ctx.Git repo [ "worktree"; "list"; "--porcelain" ] with
      | GitResult.Output text ->
        match Parse.worktreeList text |> List.tryFind (fun e -> Guard.normalize e.Path = Guard.normalize path) with
        | Some { Lock = LockState.LockedWith reason } ->
          match Parse.lockPid reason with
          | Some pid when ctx.IsAlive pid None -> Result.Error(sprintf "locked by a live process (pid %d): %s" pid reason)
          | _ -> gitStep ctx.Git repo [ "worktree"; "unlock"; path ]
        | _ -> Result.Ok()
      | _ -> Result.Ok()
    match lockOutcome with
    | Result.Error why -> Outcome.Failed why
    | Result.Ok() ->
      let forceArgs =
        match force with
        | ForceNeed.NoForce -> Result.Ok []
        | ForceNeed.DisposableCheckout -> Result.Ok [ "--force" ]
        | ForceNeed.OnlyGeneratedFilesDiffer ->
          match changedFiles ctx.Git path with
          | Result.Error why -> Result.Error(UnknownReason.describe why)
          | Result.Ok files ->
            match files |> List.forall (fun f -> f.Origin <> ChangeOrigin.Real) with
            | true -> Result.Ok [ "--force" ]
            | false -> Result.Error "real changes appeared after the plan was made"
      match forceArgs with
      | Result.Error why -> Outcome.Failed why
      | Result.Ok flags ->
        match gitStep ctx.Git repo ([ "worktree"; "remove" ] @ flags @ [ path ]) with
        | Result.Ok() -> Outcome.Done size
        | Result.Error why -> Outcome.Failed why

let private deleteBranch (ctx: EdgeContext) (repo: string) (name: string) (how: BranchDeletion) : Outcome =
  match how with
  | BranchDeletion.IfMerged ->
    match gitStep ctx.Git repo [ "branch"; "-d"; name ] with
    | Result.Ok() -> Outcome.Done 0L
    | Result.Error why -> Outcome.Failed why
  | BranchDeletion.BecauseMergedBy _ ->
    // `-D` does not check anything, so this does, right now.
    let stillMerged =
      match baseBranchCandidates |> List.tryFind (fun b -> match ctx.Git repo [ "rev-parse"; "--verify"; "--quiet"; "refs/heads/" + b ] with | GitResult.Output _ -> true | _ -> false) with
      | None -> false
      | Some b ->
        match mergeEvidence ctx.Git repo b name with
        | MergeEvidence.MergedInto _ -> true
        | _ -> false
    match stillMerged with
    | false -> Outcome.Failed "the branch is no longer merged"
    | true ->
      match gitStep ctx.Git repo [ "branch"; "-D"; name ] with
      | Result.Ok() -> Outcome.Done 0L
      | Result.Error why -> Outcome.Failed why

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

let perform (ctx: EdgeContext) (operation: Operation) : Outcome =
  try
    match operation with
    | Operation.RemoveWorktree(accepted, repo, force) -> removeWorktree ctx (Guard.path accepted) repo force
    | Operation.RemoveTree accepted ->
      let path = Guard.path accepted
      let size = directorySize path
      Directory.Delete(path, true)
      Outcome.Done size
    | Operation.RemoveFile accepted ->
      let path = Guard.path accepted
      let size = (try FileInfo(path).Length with _ -> 0L)
      File.Delete path
      Outcome.Done size
    | Operation.DeleteBranch(repo, name, how) -> deleteBranch ctx repo name how
    | Operation.Terminate(pid, ticks) -> terminate pid ticks
  with ex -> Outcome.Failed ex.Message

/// The effects the executor runs against the real machine. Every recheck is a fresh scan.
let effects (ctx: EdgeContext) : Effects =
  { Recheck =
      fun target ->
        try
          match gather (ctx.MakeScan()) (Some target) with
          | [] -> Rechecked.Gone
          | leftover :: _ -> Rechecked.Fresh leftover
        with ex -> Rechecked.CouldNotLook ex.Message
    Resolve = resolvePath
    Perform = perform ctx }

let realContext (loc: Locations) (live: unit -> LiveFacts) : EdgeContext =
  { MakeScan = fun () -> realScan loc (live ())
    Git = runGit
    IsAlive = fun pid ticks -> OwnerMonitor.isAlive OwnerMonitor.getProcessById { Pid = pid; StartTimeTicks = ticks } }
