/// The sweep: what the daemon does about the workspace without anybody asking. One pass over a repository, in this order.
///
///  1. Observe. Every agent worktree the daemon can see gets a line in the hygiene ledger when it first appears and when it goes
///     quiet or is resumed (`HygieneLedger.Ledger.transitions`), with who made it, and one that was on disk and is not any more
///     (removed by hand) gets a `Vanished` line, so a new worktree at the same path starts over. The ledger is what "finished"
///     and "resumable" are read from.
///  2. Salvage. A worktree that holds uncommitted files nowhere else (ignored files too: a local `.env` is work) has them committed
///     to `refs/sagefs/salvage/<name>`, made with a temporary index so the worktree itself is untouched (`HygieneSalvage`). A
///     second sweep with nothing changed commits nothing. The ref is not a branch, so `git push --all` does not publish it.
///  3. Tidy, when `hygiene.autoTidy` is on. The plan is `HygieneAuto.plan` over what the ledger has cleared (`Ledger.holdBack`: a
///     merged agent worktree is the exception that needs a ledger entry, and waits out its grace from the time the ledger saw it
///     quiet), and it runs through the same executor as the manual tidy: the path gate, a second look right before each step, and
///     the edge's own refusal to lose a commit or a changed file. Each removal is written to the ledger with where its tip was.
///  4. Reap salvage refs whose worktree is gone and whose content the base has, or that outlived `DataRetention.salvageRetention`
///     (`SalvageRefs.decide`), when tidy is on.
///
/// The sweep never throws: a git that will not answer is an `Unknown`, and an `Unknown` is left alone. A ledger line that could not
/// be written is in the report, not dropped.
module SageFs.HygieneSweep

open System
open System.IO
open System.Threading.Tasks
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.HygieneLedger

[<RequireQualifiedAccess>]
type TidyHalf =
  /// `hygiene.autoTidy` is off: the sweep looked and salvaged and removed nothing.
  | SwitchedOff
  | Ran of Report

[<RequireQualifiedAccess>]
type SalvageNote =
  | Saved of path: string * branch: string * sha: string * files: int
  | CouldNotSave of path: string * why: string

[<RequireQualifiedAccess>]
type ReapNote =
  | Reaped of salvageRef: string * why: string
  | CouldNotReap of salvageRef: string * why: string

type SweepReport =
  { Repo: string
    /// Ledger lines the observing wrote.
    LinesWritten: int
    Salvage: SalvageNote list
    Tidy: TidyHalf
    /// What became of the salvage refs whose worktrees are gone (only when tidy is on).
    Reaped: ReapNote list
    /// Ledger lines that could not be written, with why. A line lost here is a fact the next sweep does not have.
    LedgerFailures: string list }

/// What a sweep needs from the world. `MakeScan` is the one source of the facts (the real machine, or a test's sandbox); the
/// sweep adds the ledger's own record of when each worktree was last in use.
type SweepContext =
  { MakeScan: unit -> Scan
    Git: Git
    GitEnv: GitWithEnv
    IsAlive: int -> int64 option -> bool
    AutoTidy: unit -> Toggle }

let private managedRoot (loc: Locations) : string = Guard.normalize (Path.Combine(loc.Repo, ".claude", "worktrees"))

let private isManaged (loc: Locations) (path: string) : bool =
  (Guard.normalize path).StartsWith(managedRoot loc + "/", StringComparison.Ordinal)

/// Who made a worktree, as far as anything recorded: the harness's name in the directory, else the session's owner.
let private makerOf (path: string) (owner: Owner) : Maker =
  match Maker.ofName (Path.GetFileName(path.TrimEnd('/', '\\'))) with
  | Maker.HarnessAgent id -> Maker.HarnessAgent id
  | Maker.SessionOwner _
  | Maker.MakerUnrecorded ->
    match owner with
    | Owner.CreatedByAgent(name, connection) -> Maker.SessionOwner(name, connection)
    | Owner.OwnedByProcess _
    | Owner.OwnerUnrecorded -> Maker.MakerUnrecorded

/// Whether git answered the worktree list. When it did not, the scan carries one placeholder leftover for the repository itself
/// and no worktree can be said to be gone.
let private listingAnswered (loc: Locations) (leftovers: Leftover list) : bool =
  let repoTarget = Target.Directory(Guard.normalize loc.Repo)
  leftovers
  |> List.exists (fun l ->
    match l with
    | Leftover.AgentWorktree(entry, _) -> entry.Target = repoTarget
    | Leftover.GateCheckout _
    | Leftover.GateTierClone _
    | Leftover.GatePassRecord _
    | Leftover.HostCacheEntry _
    | Leftover.WorkerLogFile _
    | Leftover.TempRunDir _
    | Leftover.OrphanProcess _
    | Leftover.StaleBranch _
    | Leftover.SpawnedRegistryEntry _ -> false)
  |> not

/// Write ledger lines, and keep what could not be written so the report says so.
let private write (failures: ResizeArray<string>) (dataDir: string) (lines: LedgerLine list) : unit =
  match HygieneLedgerStore.tryAppend dataDir lines with
  | Result.Ok() -> ()
  | Result.Error why -> failures.Add why

/// What the scan saw of each managed agent worktree. A worktree somebody is using is in use; every other standing is quiet.
let private observed (git: Git) (loc: Locations) (leftovers: Leftover list) : Task<Observed list> =
  task {
    let seen = ResizeArray<Observed>()
    for leftover in leftovers do
      match leftover with
      | Leftover.AgentWorktree(entry, detail) ->
        match entry.Target with
        | Target.Directory path when isManaged loc path ->
          let! tip = git path [ "rev-parse"; "--verify"; "--quiet"; "HEAD" ]
          let tipText =
            match tip with
            | GitResult.Output out -> out.Trim()
            | GitResult.Exit _
            | GitResult.Unavailable _ -> ""
          let activity =
            match Standing.isInUse entry.Standing with
            | true -> Activity.InUse
            | false -> Activity.Quiet
          seen.Add
            { Path = path
              Branch = detail.Branch
              Tip = tipText
              Maker = makerOf path entry.Owner
              Activity = activity }
        | Target.Directory _
        | Target.File _
        | Target.GitBranch _
        | Target.RunningProcess _ -> ()
      | Leftover.GateCheckout _
      | Leftover.GateTierClone _
      | Leftover.GatePassRecord _
      | Leftover.HostCacheEntry _
      | Leftover.WorkerLogFile _
      | Leftover.TempRunDir _
      | Leftover.OrphanProcess _
      | Leftover.StaleBranch _
      | Leftover.SpawnedRegistryEntry _ -> ()
    return List.ofSeq seen
  }

/// Save the uncommitted files of every worktree that holds them and that nobody is using, and write each save down.
let private salvageAll
  (ctx: SweepContext)
  (loc: Locations)
  (now: DateTime)
  (failures: ResizeArray<string>)
  (leftovers: Leftover list)
  : Task<SalvageNote list> =
  let dataDir = loc.DataDir
  task {
    let notes = ResizeArray<SalvageNote>()
    for leftover in leftovers do
      match leftover with
      | Leftover.AgentWorktree(entry, _) ->
        // Only a worktree under the managed directory is the daemon's to write a ref for: one anywhere else is listed, and
        // its owner decides what happens to it.
        match Standing.hasUncommittedWork entry.Standing, entry.Target with
        | true, Target.Directory path when isManaged loc path ->
          match! HygieneSalvage.salvage ctx.GitEnv path with
          | HygieneSalvage.SalvageResult.Salvaged(branch, sha, files) ->
            write failures dataDir [ { At = now; Event = LedgerEvent.Salvaged(path, branch, sha, files) } ]
            notes.Add(SalvageNote.Saved(path, branch, sha, files))
          | HygieneSalvage.SalvageResult.CouldNotSalvage why -> notes.Add(SalvageNote.CouldNotSave(path, why))
          | HygieneSalvage.SalvageResult.NothingToSalvage
          | HygieneSalvage.SalvageResult.AlreadySalvaged _ -> ()
        | true, Target.Directory _
        | true, Target.File _
        | true, Target.GitBranch _
        | true, Target.RunningProcess _
        | false, Target.Directory _
        | false, Target.File _
        | false, Target.GitBranch _
        | false, Target.RunningProcess _ -> ()
      | Leftover.GateCheckout _
      | Leftover.GateTierClone _
      | Leftover.GatePassRecord _
      | Leftover.HostCacheEntry _
      | Leftover.WorkerLogFile _
      | Leftover.TempRunDir _
      | Leftover.OrphanProcess _
      | Leftover.StaleBranch _
      | Leftover.SpawnedRegistryEntry _ -> ()
    return List.ofSeq notes
  }

/// Reap the salvage refs that are finished with (`SalvageRefs.decide`): the worktree they came from is gone and the base has
/// everything on them, or it has been gone for the retention. Each reap is written to the ledger with the commit it pointed at.
let private reapSalvage
  (ctx: SweepContext)
  (loc: Locations)
  (now: DateTime)
  (failures: ResizeArray<string>)
  : Task<ReapNote list> =
  task {
    let notes = ResizeArray<ReapNote>()
    match! HygieneSalvage.listRefs ctx.Git loc.Repo with
    | SalvageRefs.Listing.NotListed _ -> ()
    | SalvageRefs.Listing.Listed refs ->
      let! names =
        task {
          match! ctx.Git loc.Repo [ "worktree"; "list"; "--porcelain" ] with
          | GitResult.Output text ->
            return Some(Parse.worktreeList text |> List.map (fun e -> Path.GetFileName(e.Path.TrimEnd('/', '\\'))) |> Set.ofList)
          | GitResult.Exit _
          | GitResult.Unavailable _ -> return None
        }
      let! baseRef = baseBranch ctx.Git loc.Repo
      for salvageRef in refs do
        let worktree =
          match names with
          | None -> SalvageRefs.WorktreeOfIt.NotListed
          | Some set ->
            match Set.contains salvageRef.Name set with
            | true -> SalvageRefs.WorktreeOfIt.StillThere
            | false -> SalvageRefs.WorktreeOfIt.Gone
        let! holds =
          task {
            match worktree, baseRef with
            | SalvageRefs.WorktreeOfIt.Gone, Some b ->
              match! HygieneRescue.baseHas ctx.Git loc.Repo b salvageRef.Sha with
              | HygieneRescue.BaseHas.AllByHistory
              | HygieneRescue.BaseHas.AllByPatch
              | HygieneRescue.BaseHas.AllByContent -> return SalvageRefs.BaseHolds.EverythingOnIt
              | HygieneRescue.BaseHas.NotAll _ -> return SalvageRefs.BaseHolds.NotEverything
              | HygieneRescue.BaseHas.CouldNotTell _ -> return SalvageRefs.BaseHolds.CouldNotTell
            | SalvageRefs.WorktreeOfIt.Gone, None
            | SalvageRefs.WorktreeOfIt.StillThere, _
            | SalvageRefs.WorktreeOfIt.NotListed, _ -> return SalvageRefs.BaseHolds.CouldNotTell
          }
        match SalvageRefs.decide now DataRetention.salvageRetention worktree holds salvageRef with
        | SalvageRefs.Verdict.Keep _ -> ()
        | SalvageRefs.Verdict.Reap why ->
          // The old value is named, so a ref that moved since it was listed is not deleted.
          match! ctx.Git loc.Repo [ "update-ref"; "-d"; salvageRef.Ref; salvageRef.Sha ] with
          | GitResult.Output _ ->
            write failures loc.DataDir [ { At = now; Event = LedgerEvent.SalvageReaped(salvageRef.Ref, salvageRef.Sha, why) } ]
            notes.Add(ReapNote.Reaped(salvageRef.Ref, why))
          | GitResult.Exit(code, stderr) -> notes.Add(ReapNote.CouldNotReap(salvageRef.Ref, sprintf "git exited %d: %s" code stderr))
          | GitResult.Unavailable detail -> notes.Add(ReapNote.CouldNotReap(salvageRef.Ref, detail))
    return List.ofSeq notes
  }

/// One pass over one repository. See the top of this file for the order and what each part may do.
let sweep (ctx: SweepContext) : Task<SweepReport> =
  task {
    let scanWithLedger () : Scan =
      let scan = ctx.MakeScan()
      { scan with Live = { scan.Live with LastActive = HygieneLedgerStore.lastActive scan.Loc.DataDir scan.Now } }
    let scan = scanWithLedger ()
    let loc = canonicalLocations scan.Loc
    let now = scan.Now
    let failures = ResizeArray<string>()
    // What the ledger knew BEFORE this pass: a worktree first seen now is not cleared by being seen now.
    let known = HygieneLedgerStore.records loc.DataDir
    let! leftovers = gather scan None
    let! seen = observed ctx.Git loc leftovers
    // A worktree on disk in the ledger and missing from a listing git answered was removed by hand: say so, so that a new one
    // at the same path starts over. Without an answered listing nothing is said to be gone.
    let vanished =
      match listingAnswered loc leftovers with
      | true -> Ledger.vanished now known seen
      | false -> []
    let lines = Ledger.transitions now known seen @ vanished
    write failures loc.DataDir lines
    let! salvage = salvageAll ctx loc now failures leftovers
    match ctx.AutoTidy() with
    | Off ->
      return
        { Repo = loc.Repo
          LinesWritten = List.length lines
          Salvage = salvage
          Tidy = TidyHalf.SwitchedOff
          Reaped = []
          LedgerFailures = List.ofSeq failures }
    | On ->
      let plan = HygieneAuto.plan (Ledger.holdBack loc.WorktreeGrace now known leftovers)
      match Confirmation.safeOnly plan plan.Id with
      | Result.Error _ ->
        // The confirmation is made from the plan it is run against, so this cannot differ; if it ever does, nothing runs.
        return
          { Repo = loc.Repo
            LinesWritten = List.length lines
            Salvage = salvage
            Tidy = TidyHalf.Ran { Executed = []; ReclaimedBytes = 0L }
            Reaped = []
            LedgerFailures = List.ofSeq failures }
      | Result.Ok confirmation ->
        let edge : HygieneEdge.EdgeContext = { MakeScan = scanWithLedger; Git = ctx.Git; IsAlive = ctx.IsAlive }
        let journal : HygieneEdge.Journal =
          fun line ->
            let written = HygieneLedgerStore.tryAppend loc.DataDir [ line ]
            match written with
            | Result.Ok() -> ()
            | Result.Error why -> failures.Add why
            written
        let! report = Executor.runAsync (HygieneEdge.effectsWith journal edge) (rootsOf loc) confirmation plan
        let! reaped = reapSalvage ctx loc now failures
        return
          { Repo = loc.Repo
            LinesWritten = List.length lines
            Salvage = salvage
            Tidy = TidyHalf.Ran report
            Reaped = reaped
            LedgerFailures = List.ofSeq failures }
  }

/// One pass over each repository, one after another. A repository whose pass throws is reported with its exception and the rest
/// still run: one bad repository must not end the daemon's care of every other.
let sweepRepositories
  (repos: string list)
  (sweepOne: string -> Task<SweepReport>)
  : Task<(string * Result<SweepReport, exn>) list> =
  task {
    let results = ResizeArray<string * Result<SweepReport, exn>>()
    for repo in repos do
      try
        let! report = sweepOne repo
        results.Add((repo, Result.Ok report))
      with ex when not (ex :? OutOfMemoryException) ->
        results.Add((repo, Result.Error ex))
    return List.ofSeq results
  }

/// The sweep against the real machine.
let realContext (loc: Locations) (live: unit -> LiveFacts) : SweepContext =
  { MakeScan = fun () -> realScan loc (live ())
    Git = runGit
    GitEnv = runGitEnv
    IsAlive = fun pid ticks -> OwnerMonitor.isAlive OwnerMonitor.getProcessById { Pid = pid; StartTimeTicks = ticks }
    AutoTidy = HygieneSwitch.autoTidyNow }

/// What a sweep did, in a line for the daemon's log; `None` is a sweep that changed nothing and so says nothing.
let describe (report: SweepReport) : string option =
  let removed =
    match report.Tidy with
    | TidyHalf.SwitchedOff -> 0
    | TidyHalf.Ran r -> (HygieneService.summarizeReport r).Removed
  let saved = report.Salvage |> List.filter (fun n -> match n with | SalvageNote.Saved _ -> true | SalvageNote.CouldNotSave _ -> false) |> List.length
  let failed = List.length report.Salvage - saved
  let reaped = report.Reaped |> List.filter (fun n -> match n with | ReapNote.Reaped _ -> true | ReapNote.CouldNotReap _ -> false) |> List.length
  let lost = List.length report.LedgerFailures
  match removed, saved, failed, report.LinesWritten, reaped, lost with
  | 0, 0, 0, 0, 0, 0 -> None
  | _ ->
    Some(
      sprintf
        "workspace sweep of %s: %d removed, %d worktree(s) salvaged, %d salvage(s) failed, %d salvage ref(s) reaped, %d ledger line(s) written, %d ledger write(s) failed"
        report.Repo removed saved failed reaped report.LinesWritten lost)
