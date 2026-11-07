namespace SageFs.Simulation

open System
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneLedger

/// Deterministic Simulation Testing for the hygiene SWEEP (docs/plans/hygiene-that-runs-itself.md, step 7): seeded worlds of
/// agent worktrees that appear, get busy, commit, collect uncommitted work, get merged by history, by patch or by content, and
/// sit while the clock moves, with a sweep landing between any two of those. The subject is the REAL decision code the sweep
/// runs: `Ledger.transitions`, `Ledger.holdBack`, `HygieneAuto.plan`, `Confirmation.safeOnly` and `Executor.run`. The world around
/// it models what git would answer and what the edge's own checks do (git refusing a worktree with changes, the edge keeping a
/// restore point before it drops a commit the base only has by content, the salvage commit).
///
/// The invariant the plan names: the sweep never loses a commit or an uncommitted byte. Twins reintroduce one bug each, so a green
/// run means the invariant has teeth.
module HygieneSweepSim =

  /// A unit of somebody's work: a commit on a branch, or an uncommitted change in a worktree.
  type Worktree =
    { Id: int
      Busy: HygieneSim.Use
      /// Uncommitted work, by unit id. Real work, not build output.
      Dirty: Set<int>
      /// Build output only, which a forced removal drops by design.
      HasGenerated: bool }

  type World =
    { Worktrees: Map<int, Worktree>
      /// Agent branches by name, with the commits on them.
      Branches: Map<string, int list>
      /// How each commit reached the base. A commit merged by patch or content is NOT in the base's history: only its content is.
      Base: Map<int, MergeHow>
      /// Uncommitted units saved by a salvage, by salvage branch.
      Salvaged: Map<string, Set<int>>
      /// Commits kept by `refs/sagefs/removed/...`.
      Restore: Set<int>
      /// Every commit and every uncommitted unit ever made: what must never be lost.
      CommitsMade: Set<int>
      DirtMade: Set<int>
      Lines: LedgerLine list
      Clock: DateTime
      NextId: int }

  [<RequireQualifiedAccess>]
  type Op =
    | Appear
    | BecomeBusy of nth: int
    | BecomeIdle of nth: int
    | Commit of nth: int
    | Dirty of nth: int
    | Merge of nth: int * MergeHow
    | Wait of hours: int
    | Sweep

  type Scenario = { Seed: int; Ops: Op list }

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// TWIN: removes a worktree that has uncommitted work (a forced removal) and has not saved it first.
    | ForcesAndSkipsSalvage
    /// TWIN: deletes a branch merged only by patch or content without keeping its commits reachable.
    | NoRestorePoint
    /// TWIN: believes git's age and ignores the ledger: a worktree never seen before is tidied at once.
    | IgnoresLedger

  /// One worktree removal the sweep made, with what the ledger knew when the sweep started.
  type Removal = { WorktreeId: int; Known: WorktreeRecord option; At: DateTime }

  type Step = { Op: Op; After: World; Removals: Removal list }

  type Trace = { Scenario: Scenario; Steps: Step list }

  /// The grace the product holds a finished worktree for, so the sim's waits are laid out against the real number.
  let grace = DataRetention.finishedWorktreeGrace

  let private start = DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)

  let initial : World =
    { Worktrees = Map.empty
      Branches = Map.empty
      Base = Map.empty
      Salvaged = Map.empty
      Restore = Set.empty
      CommitsMade = Set.empty
      DirtMade = Set.empty
      Lines = []
      Clock = start
      NextId = 1 }

  let private nth (m: Map<'k, 'v>) (n: int) : 'v option =
    match Map.count m with
    | 0 -> None
    | count -> m |> Map.toList |> List.item (n % count) |> snd |> Some

  let private salvageBranch (id: int) : string = sprintf "salvage/agent-%d" id

  // ─── What gathering would say ────────────────────────────────────────

  let private unmerged (world: World) (commits: int list) : int list =
    commits |> List.filter (fun c -> not (Map.containsKey c world.Base))

  let private evidenceOf (world: World) (commits: int list) : MergeEvidence =
    match unmerged world commits with
    | [] ->
      match commits with
      | [] -> MergeEvidence.MergedInto MergeHow.Ancestor
      | c :: _ -> MergeEvidence.MergedInto world.Base.[c]
    | open1 :: rest ->
      let commit (c: int) : Commit = { Sha = sprintf "c%d" c; Subject = sprintf "commit %d" c }
      MergeEvidence.UnmergedCommits(NonEmpty.ofHeadTail (commit open1) (rest |> List.map commit))

  let private baseSubject (world: World) (target: Target) (kind: LeftoverKind) : Subject =
    { Kind = kind
      Target = target
      SizeBytes = 100L
      // Git says it is old: the ledger's rule is what has to hold a worktree back.
      LastTouched = world.Clock - grace * 3.0
      Owner = Owner.OwnerUnrecorded
      Repo = RepoLink.InRepo HygieneSim.repo
      Branch = BranchLabel.NoBranch
      Display = ""
      Uses = []
      Lineage = Lineage.NoOwnerRecorded
      Retention = Retention.NeverExpires
      Git = GitEvidence.NoGitEvidence }

  let private worktreeSubject (world: World) (wt: Worktree) : Subject =
    let name = HygieneSim.branchOf wt.Id
    let commits = Map.tryFind name world.Branches |> Option.defaultValue []
    let changes =
      [ if not (Set.isEmpty wt.Dirty) then { Path = "src/work.fs"; Origin = ChangeOrigin.Real }
        if wt.HasGenerated then { Path = "packages.lock.json"; Origin = ChangeOrigin.Generated "restore lock file" } ]
    { baseSubject world (Target.Directory(HygieneSim.pathOf wt.Id)) LeftoverKind.AgentWorktree with
        Branch = BranchLabel.OnBranch name
        Retention = Retention.KeepFor grace
        Uses =
          (match wt.Busy with
           | HygieneSim.Use.Busy -> [ InUseReason.LiveSession(sprintf "s%d" wt.Id) ]
           | HygieneSim.Use.Idle -> [])
        Git = GitEvidence.Worktree(evidenceOf world commits, changes) }

  let private branchSubject (world: World) (name: string) (commits: int list) : Subject =
    { baseSubject world (Target.GitBranch(HygieneSim.repo, name)) LeftoverKind.StaleBranch with
        Branch = BranchLabel.OnBranch name
        Git = GitEvidence.Branch(evidenceOf world commits) }

  let private gather (world: World) : Leftover list =
    [ for wt in world.Worktrees |> Map.toList |> List.map snd -> classify world.Clock (worktreeSubject world wt)
      for KeyValue(name, commits) in world.Branches -> classify world.Clock (branchSubject world name commits) ]

  let private subjectOfTarget (world: World) (target: Target) : Subject option =
    match target with
    | Target.Directory path ->
      world.Worktrees |> Map.toList |> List.tryPick (fun (id, wt) -> if HygieneSim.pathOf id = path then Some(worktreeSubject world wt) else None)
    | Target.GitBranch(_, name) -> Map.tryFind name world.Branches |> Option.map (branchSubject world name)
    | Target.File _
    | Target.RunningProcess _ -> None

  // ─── The world changing ──────────────────────────────────────────────

  let private updateWorktree (world: World) (n: int) (f: Worktree -> Worktree) : World =
    match nth world.Worktrees n with
    | None -> world
    | Some wt -> { world with Worktrees = Map.add wt.Id (f wt) world.Worktrees }

  let applyOp (world: World) (op: Op) : World =
    match op with
    | Op.Appear ->
      let id = world.NextId
      let commit = id * 1000
      { world with
          Worktrees = Map.add id { Id = id; Busy = HygieneSim.Use.Idle; Dirty = Set.empty; HasGenerated = false } world.Worktrees
          Branches = Map.add (HygieneSim.branchOf id) [ commit ] world.Branches
          CommitsMade = Set.add commit world.CommitsMade
          NextId = id + 1 }
    | Op.BecomeBusy n -> updateWorktree world n (fun w -> { w with Busy = HygieneSim.Use.Busy })
    | Op.BecomeIdle n -> updateWorktree world n (fun w -> { w with Busy = HygieneSim.Use.Idle })
    | Op.Commit n ->
      match nth world.Worktrees n with
      | None -> world
      | Some wt ->
        let name = HygieneSim.branchOf wt.Id
        let commits = Map.tryFind name world.Branches |> Option.defaultValue []
        let c = wt.Id * 1000 + List.length commits
        { world with Branches = Map.add name (commits @ [ c ]) world.Branches; CommitsMade = Set.add c world.CommitsMade }
    | Op.Dirty n ->
      match nth world.Worktrees n with
      | None -> world
      | Some wt ->
        let unit = wt.Id * 1000 + 500 + Set.count wt.Dirty
        { world with
            Worktrees = Map.add wt.Id { wt with Dirty = Set.add unit wt.Dirty; HasGenerated = true } world.Worktrees
            DirtMade = Set.add unit world.DirtMade }
    | Op.Merge(n, how) ->
      match nth world.Branches n with
      | None -> world
      | Some commits -> { world with Base = commits |> List.fold (fun acc c -> Map.add c how acc) world.Base }
    | Op.Wait hours -> { world with Clock = world.Clock + TimeSpan.FromHours(float hours) }
    | Op.Sweep -> world

  // ─── The sweep: the real decisions over the modelled world ───────────

  let private observed (world: World) : Observed list =
    world.Worktrees
    |> Map.toList
    |> List.map (fun (id, wt) ->
      { Path = HygieneSim.pathOf id
        Branch = BranchLabel.OnBranch(HygieneSim.branchOf id)
        Tip = sprintf "tip%d" id
        Maker = Maker.HarnessAgent(string id)
        Activity =
          (match wt.Busy with
           | HygieneSim.Use.Busy -> Activity.InUse
           | HygieneSim.Use.Idle -> Activity.Quiet) })

  let private commitsOfBranch (world: World) (name: string) : int list = Map.tryFind name world.Branches |> Option.defaultValue []

  /// Whether a branch's commits are all in the base's history (an ancestor), so dropping the ref loses nothing.
  let private allAncestors (world: World) (commits: int list) : bool =
    commits |> List.forall (fun c -> Map.tryFind c world.Base = Some MergeHow.Ancestor)

  /// TWIN support: a tidy that takes a worktree with uncommitted work for a clean one.
  let private believeDirtyIsClean (leftover: Leftover) : Leftover =
    match leftover with
    | Leftover.AgentWorktree(entry, detail) when Standing.hasUncommittedWork entry.Standing ->
      Leftover.AgentWorktree({ entry with Standing = Standing.Merged MergeHow.Ancestor }, detail)
    | other -> other

  let private sweepWith (behavior: Behavior) (world: World) : World * Removal list =
    let known = Ledger.fold world.Lines
    let lines = Ledger.transitions world.Clock known (observed world)
    let afterObserving = { world with Lines = world.Lines @ lines }
    // Salvage: uncommitted work of a worktree nobody is using goes to a branch of its own.
    let afterSalvage =
      match behavior with
      | Behavior.ForcesAndSkipsSalvage -> afterObserving
      | Behavior.Real
      | Behavior.NoRestorePoint
      | Behavior.IgnoresLedger ->
        afterObserving.Worktrees
        |> Map.fold (fun (w: World) id wt ->
          match wt.Busy, Set.isEmpty wt.Dirty with
          | HygieneSim.Use.Idle, false ->
            let saved = Map.tryFind (salvageBranch id) w.Salvaged |> Option.defaultValue Set.empty
            { w with Salvaged = Map.add (salvageBranch id) (Set.union saved wt.Dirty) w.Salvaged }
          | HygieneSim.Use.Idle, true
          | HygieneSim.Use.Busy, _ -> w) afterObserving
    // Tidy: the plan the daemon makes over what the ledger has cleared, run through the real executor.
    let leftovers = gather afterSalvage
    let held =
      match behavior with
      | Behavior.IgnoresLedger -> leftovers
      | Behavior.ForcesAndSkipsSalvage -> Ledger.holdBack grace world.Clock known leftovers |> List.map believeDirtyIsClean
      | Behavior.Real
      | Behavior.NoRestorePoint -> Ledger.holdBack grace world.Clock known leftovers
    let plan = HygieneAuto.plan held
    match Confirmation.safeOnly plan plan.Id with
    | Result.Error _ -> afterSalvage, []
    | Result.Ok confirmation ->
      let cell = ref afterSalvage
      let removals = ResizeArray<Removal>()
      let effects : Effects =
        { Recheck =
            fun target ->
              let current = cell.Value
              match subjectOfTarget current target with
              | Some subject ->
                let fresh = classify current.Clock subject
                match behavior with
                | Behavior.ForcesAndSkipsSalvage -> Rechecked.Fresh(believeDirtyIsClean fresh)
                | Behavior.Real
                | Behavior.NoRestorePoint
                | Behavior.IgnoresLedger -> Rechecked.Fresh fresh
              | None -> Rechecked.Gone
          Resolve = Result.Ok
          Perform =
            fun operation ->
              let current = cell.Value
              match operation with
              | Operation.RemoveWorktree(accepted, _, force) ->
                let path = Guard.path accepted
                match current.Worktrees |> Map.toList |> List.tryFind (fun (id, _) -> HygieneSim.pathOf id = path) with
                | None -> Outcome.Done 0L
                | Some(id, wt) ->
                  // git worktree remove refuses a worktree with changes unless forced; the edge only forces when only build
                  // output differs.
                  let refuses =
                    match behavior, force, Set.isEmpty wt.Dirty with
                    | Behavior.ForcesAndSkipsSalvage, _, _ -> false
                    | _, _, true -> false
                    | _, _, false -> true
                  match refuses with
                  | true -> Outcome.Failed "git refuses: the worktree has uncommitted changes"
                  | false ->
                    let name = HygieneSim.branchOf id
                    let commits = commitsOfBranch current name
                    // The edge keeps a commit the base only has by content reachable before it lets go of the worktree.
                    let restore =
                      match behavior, allAncestors current commits with
                      | Behavior.NoRestorePoint, _ -> current.Restore
                      | _, true -> current.Restore
                      | _, false -> Set.union current.Restore (Set.ofList commits)
                    removals.Add { WorktreeId = id; Known = Map.tryFind (HygieneSim.pathOf id) known; At = current.Clock }
                    cell.Value <- { current with Worktrees = Map.remove id current.Worktrees; Restore = restore }
                    Outcome.Done 100L
              | Operation.DeleteBranch(_, name, how) ->
                match Map.tryFind name current.Branches with
                | None -> Outcome.Done 0L
                | Some commits ->
                  let checkedOut = current.Worktrees |> Map.exists (fun id _ -> HygieneSim.branchOf id = name)
                  let merged = List.isEmpty (unmerged current commits)
                  match checkedOut, merged with
                  | true, _ -> Outcome.Failed "branch is checked out in a worktree"
                  | _, false -> Outcome.Failed "the branch is no longer merged"
                  | false, true ->
                    let restore =
                      match behavior, how with
                      | Behavior.NoRestorePoint, _ -> current.Restore
                      | _, BranchDeletion.IfMerged -> current.Restore
                      | _, BranchDeletion.BecauseMergedBy _ -> Set.union current.Restore (Set.ofList commits)
                    cell.Value <- { current with Branches = Map.remove name current.Branches; Restore = restore }
                    Outcome.Done 0L
              | Operation.RemoveTree _
              | Operation.RemoveFile _
              | Operation.Terminate _ -> Outcome.Done 0L }
      Executor.run effects HygieneSim.roots confirmation plan |> ignore
      cell.Value, List.ofSeq removals

  // ─── Running a scenario ──────────────────────────────────────────────

  let step (behavior: Behavior) (world: World) (op: Op) : Step * World =
    match op with
    | Op.Sweep ->
      let after, removals = sweepWith behavior world
      { Op = op; After = after; Removals = removals }, after
    | other ->
      let after = applyOp world other
      { Op = op; After = after; Removals = [] }, after

  let trace (behavior: Behavior) (scenario: Scenario) : Trace =
    let steps, _ =
      scenario.Ops
      |> List.fold (fun (acc: Step list, world: World) op ->
        let s, next = step behavior world op
        s :: acc, next) ([], initial)
    { Scenario = scenario; Steps = List.rev steps }

  let private randomOp (rng: Random) : Op =
    let how () = [| MergeHow.Ancestor; MergeHow.PatchEquivalent; MergeHow.TreeEqual |].[rng.Next 3]
    match rng.Next 18 with
    | 0 | 1 | 2 -> Op.Appear
    | 3 -> Op.BecomeBusy(rng.Next 8)
    | 4 | 5 -> Op.BecomeIdle(rng.Next 8)
    | 6 | 7 -> Op.Commit(rng.Next 8)
    | 8 | 9 -> Op.Dirty(rng.Next 8)
    | 10 | 11 | 12 -> Op.Merge(rng.Next 8, how ())
    | 13 | 14 -> Op.Wait(1 + rng.Next 30)
    | _ -> Op.Sweep

  /// A pure function of `seed`: replaying a seed gives the identical trace. Every scenario ends with enough sweeps and waiting
  /// that the tidy half has been given its chance to do everything it may.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 30 + rng.Next 50
    let ops = [ for _ in 1 .. n -> randomOp rng ]
    { Seed = seed
      Ops = ops @ [ Op.Sweep; Op.Wait 30; Op.Sweep; Op.Wait 30; Op.Sweep; Op.Sweep ] }

  // ─── Invariants ──────────────────────────────────────────────────────

  type Violation = { Index: int; Why: string }

  /// NEVER-LOSE-A-COMMIT: after every step, every commit ever made is in the base's history, on a branch, or kept by a restore ref.
  let neverLoseACommit (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.choose (fun (i, step) ->
      let w = step.After
      let held = w.Branches |> Map.toList |> List.collect snd |> Set.ofList
      let lost =
        w.CommitsMade
        |> Set.filter (fun c -> Map.tryFind c w.Base <> Some MergeHow.Ancestor && not (Set.contains c held) && not (Set.contains c w.Restore))
      match Set.isEmpty lost with
      | true -> None
      | false -> Some { Index = i; Why = sprintf "commits %A are reachable from nothing" (Set.toList lost) })

  /// NEVER-LOSE-AN-UNCOMMITTED-BYTE: after every step, every uncommitted unit ever made is in a worktree that still exists or on
  /// a salvage branch.
  let neverLoseAnUncommittedByte (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.choose (fun (i, step) ->
      let w = step.After
      let inWorktrees = w.Worktrees |> Map.toList |> List.collect (fun (_, wt) -> Set.toList wt.Dirty) |> Set.ofList
      let saved = w.Salvaged |> Map.toList |> List.collect (fun (_, s) -> Set.toList s) |> Set.ofList
      let lost = w.DirtMade |> Set.filter (fun u -> not (Set.contains u inWorktrees) && not (Set.contains u saved))
      match Set.isEmpty lost with
      | true -> None
      | false -> Some { Index = i; Why = sprintf "uncommitted work %A is in no worktree and on no salvage branch" (Set.toList lost) })

  /// A WORKTREE IS TIDIED ONLY AFTER THE LEDGER HAS SEEN IT QUIET FOR THE GRACE: agent worktrees need a ledger entry.
  let removedOnlyAfterTheLedgerClearedIt (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.collect (fun (i, step) ->
      step.Removals
      |> List.choose (fun r ->
        match r.Known with
        | None -> Some { Index = i; Why = sprintf "worktree %d was removed before the ledger had ever seen it" r.WorktreeId }
        | Some record ->
          match record.Activity with
          | RecordedActivity.RecordedInUse -> Some { Index = i; Why = sprintf "worktree %d was removed while the ledger had it in use" r.WorktreeId }
          | RecordedActivity.RecordedQuietSince since ->
            match r.At - since >= grace with
            | true -> None
            | false -> Some { Index = i; Why = sprintf "worktree %d was removed %s after the ledger saw it go quiet, inside the grace" r.WorktreeId (string (r.At - since)) }))

  /// NEVER-REMOVE-IN-USE: nothing a session is using was removed.
  let neverRemoveInUse (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.pairwise
    |> List.collect (fun ((_, before), (i, step)) ->
      step.Removals
      |> List.choose (fun r ->
        match Map.tryFind r.WorktreeId before.After.Worktrees with
        | Some wt when wt.Busy = HygieneSim.Use.Busy -> Some { Index = i; Why = sprintf "worktree %d was removed with a session in it" r.WorktreeId }
        | Some _
        | None -> None))

  /// The scenarios really remove worktrees, so a green run is not vacuous.
  let removalsHappened (traces: Trace list) : int =
    traces |> List.sumBy (fun t -> t.Steps |> List.sumBy (fun s -> List.length s.Removals))
