/// The sweep: what the daemon does about the workspace without anybody asking. One pass over a repository, in this order.
///
///  1. Observe. Every agent worktree the daemon can see gets a line in the hygiene ledger when it first appears and when it goes
///     quiet or is resumed (`HygieneLedger.Ledger.transitions`), with who made it. The ledger is what "finished" and "resumable"
///     are read from.
///  2. Salvage. A worktree that holds uncommitted files nowhere else has them committed to `salvage/<name>`, made with a
///     temporary index so the worktree itself is untouched (`HygieneSalvage`). A second sweep with nothing changed commits nothing.
///  3. Tidy, when `hygiene.autoTidy` is on. The plan is `HygieneAuto.plan` over what the ledger has cleared (`Ledger.holdBack`: a
///     merged agent worktree is the exception that needs a ledger entry, and waits out its grace from the time the ledger saw it
///     quiet), and it runs through the same executor as the manual tidy: the path gate, a second look right before each step, and
///     the edge's own refusal to lose a commit or a changed file. Each removal is written to the ledger with where its tip was.
///
/// The sweep never throws: a git that will not answer is an `Unknown`, and an `Unknown` is left alone.
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

type SweepReport =
  { Repo: string
    /// Ledger lines the observing wrote.
    LinesWritten: int
    Salvage: SalvageNote list
    Tidy: TidyHalf }

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
let private salvageAll (ctx: SweepContext) (loc: Locations) (now: DateTime) (leftovers: Leftover list) : Task<SalvageNote list> =
  let dataDir = loc.DataDir
  task {
    let notes = ResizeArray<SalvageNote>()
    for leftover in leftovers do
      match leftover with
      | Leftover.AgentWorktree(entry, _) ->
        // Only a worktree under the managed directory is the daemon's to write a branch for: one anywhere else is listed, and
        // its owner decides what happens to it.
        match Standing.hasUncommittedWork entry.Standing, entry.Target with
        | true, Target.Directory path when isManaged loc path ->
          match! HygieneSalvage.salvage ctx.GitEnv path with
          | HygieneSalvage.SalvageResult.Salvaged(branch, sha, files) ->
            HygieneLedgerStore.append dataDir [ { At = now; Event = LedgerEvent.Salvaged(path, branch, sha, files) } ]
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

/// One pass over one repository. See the top of this file for the order and what each part may do.
let sweep (ctx: SweepContext) : Task<SweepReport> =
  task {
    let scanWithLedger () : Scan =
      let scan = ctx.MakeScan()
      { scan with Live = { scan.Live with LastActive = HygieneLedgerStore.lastActive scan.Loc.DataDir scan.Now } }
    let scan = scanWithLedger ()
    let loc = canonicalLocations scan.Loc
    let now = scan.Now
    // What the ledger knew BEFORE this pass: a worktree first seen now is not cleared by being seen now.
    let known = HygieneLedgerStore.records loc.DataDir
    let! leftovers = gather scan None
    let! seen = observed ctx.Git loc leftovers
    let lines = Ledger.transitions now known seen
    HygieneLedgerStore.append loc.DataDir lines
    let! salvage = salvageAll ctx loc now leftovers
    match ctx.AutoTidy() with
    | Off ->
      return
        { Repo = loc.Repo
          LinesWritten = List.length lines
          Salvage = salvage
          Tidy = TidyHalf.SwitchedOff }
    | On ->
      let plan = HygieneAuto.plan (Ledger.holdBack loc.WorktreeGrace now known leftovers)
      match Confirmation.safeOnly plan plan.Id with
      | Result.Error _ ->
        // The confirmation is made from the plan it is run against, so this cannot differ; if it ever does, nothing runs.
        return
          { Repo = loc.Repo
            LinesWritten = List.length lines
            Salvage = salvage
            Tidy = TidyHalf.Ran { Executed = []; ReclaimedBytes = 0L } }
      | Result.Ok confirmation ->
        let edge : HygieneEdge.EdgeContext = { MakeScan = scanWithLedger; Git = ctx.Git; IsAlive = ctx.IsAlive }
        let journal : HygieneEdge.Journal = fun line -> HygieneLedgerStore.append loc.DataDir [ line ]
        let! report = Executor.runAsync (HygieneEdge.effectsWith journal edge) (rootsOf loc) confirmation plan
        return
          { Repo = loc.Repo
            LinesWritten = List.length lines
            Salvage = salvage
            Tidy = TidyHalf.Ran report }
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
  match removed, saved, failed, report.LinesWritten with
  | 0, 0, 0, 0 -> None
  | _ ->
    Some(
      sprintf
        "workspace sweep of %s: %d removed, %d worktree(s) salvaged, %d salvage(s) failed, %d ledger line(s) written"
        report.Repo removed saved failed report.LinesWritten)
