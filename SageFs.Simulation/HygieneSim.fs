namespace SageFs.Simulation

open System
open SageFs.WorkspaceHygiene

/// Deterministic Simulation Testing for workspace hygiene: seeded worlds of agent worktrees, their branches
/// and orphaned processes that appear, get busy, collect commits and dirt, get merged, lose their owners and
/// get removed by someone else, all while plans are made and run. The subject is the REAL `classify`,
/// `Planner.plan`, `Confirmation.safeOnly` and `Executor.run`. The world around them is a model of what git
/// and the process table would answer, and of what the edge's own checks (git refusing a dirty worktree,
/// `branch -d` refusing an unmerged one, a pid whose start time changed) would do.
///
/// Same design rules as the other harnesses here:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace. The op order is the scheduler.
///   * Ground truth is kept by the world itself and never read from the decision under test.
///   * A window between looking and acting is modelled: `Late` ops land right after a step's recheck and
///     before it performs, which is the only moment a plan can be wrong about the world.
///   * Twins reintroduce the bugs the invariants exist to catch, so a green run means something.
module HygieneSim =

  [<RequireQualifiedAccess>]
  type Dirt =
    | NoDirt
    | GeneratedDirt
    | RealDirt

  [<RequireQualifiedAccess>]
  type Use =
    | Idle
    | Busy

  type Worktree = { Id: int; Use: Use; Dirt: Dirt }

  /// A branch ref: the commits on it and, once merged, how.
  type BranchRef = { Name: string; Commits: int list }

  type Process = { Pid: int; StartTicks: int64; OwnerAlive: bool; Reused: bool }

  type World =
    { Worktrees: Map<int, Worktree>
      Branches: Map<string, BranchRef>
      /// Commits that are in the base branch, with how they got there.
      Base: Map<int, MergeHow>
      /// Every commit ever made that was not merged when it was made.
      Made: Set<int>
      Processes: Map<int, Process>
      NextCommit: int
      NextId: int }

  [<RequireQualifiedAccess>]
  type Op =
    | AppearWorktree
    | AppearOrphan
    | BecomeBusy of nth: int
    | BecomeIdle of nth: int
    | Commit of nth: int
    | Merge of nth: int * MergeHow
    | Dirty of nth: int * Dirt
    | RemoveExternally of nth: int
    | OwnerDies of nth: int
    | PidReused of nth: int
    /// Look at the world and make a plan. Must change nothing.
    | PlanNow
    /// Run the plan shown last, after the given ops land in the windows between looking and acting.
    | Execute of late: Op list
    /// Run the plan shown last a second time.
    | ExecuteAgain
    /// Try to run a plan that was shown earlier than the last one (the world has since changed its plan).
    | ExecuteStale

  type Scenario = { Seed: int; Ops: Op list }

  /// Which executor the world runs plans through. The twins reintroduce one bug each.
  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// TWIN: believes what the plan saw instead of looking again.
    | TrustsThePlan
    /// TWIN: deletes the branch with -D without checking it is merged, and forces every worktree removal.
    | ForcesEverything
    /// TWIN: kills a process by pid alone.
    | KillsByPid
    /// TWIN: planning also performs the Safe steps.
    | PlanningMutates
    /// TWIN: a second run counts a removal that is already gone as reclaimed again.
    | CountsGoneTwice

  /// What one removal looked like at the moment it was decided and the moment it was done.
  type Removal =
    { Operation: string
      DecidedOn: Worktree option
      DecidedBranch: BranchRef option
      DecidedProcess: Process option
      DoneOn: Worktree option
      /// What the process table held for the pid when it was killed.
      KilledStartTicks: int64 option }

  type Step =
    { Op: Op
      Before: World
      After: World
      /// Everything the executor performed during this op.
      Removals: Removal list
      /// The plan shown by a PlanNow, or run by an Execute.
      PlanId: PlanId option
      Reclaimed: int64 option
      PerformedDuringPlanning: int }

  type Trace = { Scenario: Scenario; Steps: Step list; Initial: World }

  let repo = "/w"
  let worktreeRoot = "/w/.claude/worktrees"
  let roots : Roots = { Entries = [ RootKind.AgentWorktrees, worktreeRoot ]; NamePrefixes = [] }
  let now = DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)

  let pathOf (id: int) : string = sprintf "%s/agent-%d" worktreeRoot id
  let branchOf (id: int) : string = sprintf "worktree-agent-%d" id

  let initial : World =
    { Worktrees = Map.empty
      Branches = Map.empty
      Base = Map.empty
      Made = Set.empty
      Processes = Map.empty
      NextCommit = 1
      NextId = 1 }

  // ─── Ground truth ────────────────────────────────────────────────────

  let private unmergedOf (world: World) (branch: BranchRef) : int list =
    branch.Commits |> List.filter (fun c -> not (Map.containsKey c world.Base))

  /// How a branch got merged, or None while any of its commits is not in the base.
  let mergeOf (world: World) (branch: BranchRef) : MergeHow option =
    match unmergedOf world branch with
    | [] ->
      // The branch's own first commit says how: a branch with no commits is trivially an ancestor.
      match branch.Commits with
      | [] -> Some MergeHow.Ancestor
      | c :: _ -> Map.tryFind c world.Base
    | _ -> None

  /// Commits no branch holds and the base does not have: work that is gone.
  let lostCommits (world: World) : int list =
    let held = world.Branches |> Map.toList |> List.collect (fun (_, b) -> b.Commits) |> Set.ofList
    world.Made
    |> Set.filter (fun c -> not (Map.containsKey c world.Base) && not (Set.contains c held))
    |> Set.toList

  let nth (m: Map<'k, 'v>) (n: int) : 'v option =
    match Map.count m with
    | 0 -> None
    | count -> m |> Map.toList |> List.item (n % count) |> snd |> Some

  // ─── What gathering would say ────────────────────────────────────────

  let private evidenceOf (world: World) (branch: BranchRef) : MergeEvidence =
    match mergeOf world branch with
    | Some how -> MergeEvidence.MergedInto how
    | None ->
      match unmergedOf world branch |> List.map (fun c -> { Sha = sprintf "c%d" c; Subject = sprintf "commit %d" c }) with
      | head :: tail -> MergeEvidence.UnmergedCommits(NonEmpty.ofHeadTail head tail)
      | [] -> MergeEvidence.MergeNotDecidable(UnknownReason.MergeUndecidable "no commits and not merged")

  let private changesOf (dirt: Dirt) : ChangedFile list =
    match dirt with
    | Dirt.NoDirt -> []
    | Dirt.GeneratedDirt -> [ { Path = "packages.lock.json"; Origin = ChangeOrigin.Generated "restore lock file" } ]
    | Dirt.RealDirt -> [ { Path = "src/work.fs"; Origin = ChangeOrigin.Real } ]

  let private baseSubject (target: Target) (kind: LeftoverKind) : Subject =
    { Kind = kind
      Target = target
      SizeBytes = 100L
      LastTouched = now - TimeSpan.FromDays 3.0
      Owner = Owner.OwnerUnrecorded
      Repo = RepoLink.InRepo repo
      Branch = BranchLabel.NoBranch
      Display = ""
      Uses = []
      Lineage = Lineage.NoOwnerRecorded
      Retention = Retention.NeverExpires
      Git = GitEvidence.NoGitEvidence }

  let worktreeSubject (world: World) (wt: Worktree) : Subject =
    let name = branchOf wt.Id
    let evidence =
      match Map.tryFind name world.Branches with
      | Some b -> evidenceOf world b
      | None -> MergeEvidence.MergeNotDecidable(UnknownReason.MergeUndecidable "its branch is gone")
    { baseSubject (Target.Directory(pathOf wt.Id)) LeftoverKind.AgentWorktree with
        Branch = BranchLabel.OnBranch name
        Uses =
          (match wt.Use with
           | Use.Busy -> [ InUseReason.LiveSession(sprintf "s%d" wt.Id) ]
           | Use.Idle -> [])
        Git = GitEvidence.Worktree(evidence, changesOf wt.Dirt) }

  let branchSubject (world: World) (b: BranchRef) : Subject =
    { baseSubject (Target.GitBranch(repo, b.Name)) LeftoverKind.StaleBranch with
        Branch = BranchLabel.OnBranch b.Name
        Git = GitEvidence.Branch(evidenceOf world b) }

  let processSubject (p: Process) : Subject =
    { baseSubject (Target.RunningProcess(p.Pid, p.StartTicks)) LeftoverKind.OrphanProcess with
        Repo = RepoLink.NoRepo
        Display = sprintf "SageFs.Host (pid %d)" p.Pid
        Uses = []
        Lineage =
          (match p.OwnerAlive with
           | true -> Lineage.OwnerAlive 1
           | false -> Lineage.OwnerGone(1, now - TimeSpan.FromDays 2.0)) }

  /// Every leftover the world holds, classified by the real `classify`.
  let gather (world: World) : Leftover list =
    [ for wt in world.Worktrees |> Map.toList |> List.map snd -> classify now (worktreeSubject world wt)
      for b in world.Branches |> Map.toList |> List.map snd -> classify now (branchSubject world b)
      for p in world.Processes |> Map.toList |> List.map snd -> classify now (processSubject p) ]

  let private subjectOfTarget (world: World) (target: Target) : Subject option =
    match target with
    | Target.Directory path ->
      world.Worktrees |> Map.toList |> List.tryPick (fun (id, wt) -> if pathOf id = path then Some(worktreeSubject world wt) else None)
    | Target.GitBranch(_, name) -> Map.tryFind name world.Branches |> Option.map (branchSubject world)
    | Target.RunningProcess(pid, ticks) ->
      match Map.tryFind pid world.Processes with
      | Some p when p.StartTicks = ticks -> Some(processSubject p)
      | Some _ -> None
      | None -> None
    | Target.File _ -> None

  // ─── The world changing ──────────────────────────────────────────────

  let private updateWorktree (world: World) (n: int) (f: Worktree -> Worktree) : World =
    match nth world.Worktrees n with
    | None -> world
    | Some wt -> { world with Worktrees = Map.add wt.Id (f wt) world.Worktrees }

  let applyOp (world: World) (op: Op) : World =
    match op with
    | Op.AppearWorktree ->
      let id = world.NextId
      let c = world.NextCommit
      { world with
          Worktrees = Map.add id { Id = id; Use = Use.Idle; Dirt = Dirt.NoDirt } world.Worktrees
          Branches = Map.add (branchOf id) { Name = branchOf id; Commits = [ c ] } world.Branches
          Made = Set.add c world.Made
          NextId = id + 1
          NextCommit = c + 1 }
    | Op.AppearOrphan ->
      let pid = 1000 + world.NextId
      { world with
          Processes = Map.add pid { Pid = pid; StartTicks = int64 pid * 7L; OwnerAlive = false; Reused = false } world.Processes
          NextId = world.NextId + 1 }
    | Op.BecomeBusy n -> updateWorktree world n (fun w -> { w with Use = Use.Busy })
    | Op.BecomeIdle n -> updateWorktree world n (fun w -> { w with Use = Use.Idle })
    | Op.Dirty(n, dirt) -> updateWorktree world n (fun w -> { w with Dirt = dirt })
    | Op.Commit n ->
      match nth world.Worktrees n with
      | None -> world
      | Some wt ->
        let name = branchOf wt.Id
        match Map.tryFind name world.Branches with
        | None -> world
        | Some b ->
          let c = world.NextCommit
          { world with
              Branches = Map.add name { b with Commits = b.Commits @ [ c ] } world.Branches
              Made = Set.add c world.Made
              NextCommit = c + 1 }
    | Op.Merge(n, how) ->
      match nth world.Branches n with
      | None -> world
      | Some b -> { world with Base = b.Commits |> List.fold (fun acc c -> Map.add c how acc) world.Base }
    | Op.RemoveExternally n ->
      match nth world.Worktrees n with
      | None -> world
      | Some wt -> { world with Worktrees = Map.remove wt.Id world.Worktrees }
    | Op.OwnerDies n ->
      match nth world.Processes n with
      | None -> world
      | Some p -> { world with Processes = Map.add p.Pid { p with OwnerAlive = false } world.Processes }
    | Op.PidReused n ->
      match nth world.Processes n with
      | None -> world
      | Some p ->
        // A new process now has the pid: a different start time, and its owner is alive.
        { world with Processes = Map.add p.Pid { p with StartTicks = p.StartTicks + 1L; OwnerAlive = true; Reused = true } world.Processes }
    | Op.PlanNow
    | Op.Execute _
    | Op.ExecuteAgain
    | Op.ExecuteStale -> world

  // ─── The effects the executor is given ───────────────────────────────

  /// A mutable cell the effects run against, with the log of what they did.
  type private Run(start: World, behavior: Behavior, late: Op list, planSeen: Leftover list) =
    let mutable world = start
    let mutable pendingLate = late
    let removals = ResizeArray<Removal>()
    let seen = planSeen |> List.map (fun l -> Leftover.target l, l) |> Map.ofList
    let mutable decided : (Target * Worktree option * BranchRef option * Process option) option = None

    member _.World = world
    member _.Removals = removals |> Seq.toList

    member _.Effects : Effects =
      { Recheck =
          fun target ->
            let truth =
              match target with
              | Target.Directory path -> world.Worktrees |> Map.toList |> List.tryPick (fun (id, w) -> if pathOf id = path then Some w else None)
              | _ -> None
            let branchTruth = match target with | Target.GitBranch(_, name) -> Map.tryFind name world.Branches | _ -> None
            let procTruth = match target with | Target.RunningProcess(pid, _) -> Map.tryFind pid world.Processes | _ -> None
            decided <- Some(target, truth, branchTruth, procTruth)
            let result =
              match behavior with
              | Behavior.TrustsThePlan ->
                // Believes the plan's own picture of the target, whatever has happened since.
                match Map.tryFind target seen with
                | Some l -> Rechecked.Fresh l
                | None -> Rechecked.Gone
              | Behavior.CountsGoneTwice ->
                match Map.tryFind target seen, subjectOfTarget world target with
                | Some l, None -> Rechecked.Fresh l
                | _, Some s -> Rechecked.Fresh(classify now s)
                | None, None -> Rechecked.Gone
              | _ ->
                match subjectOfTarget world target with
                | Some s -> Rechecked.Fresh(classify now s)
                | None -> Rechecked.Gone
            // The window between looking and acting: whatever lands now, the executor has already decided.
            match pendingLate with
            | op :: rest ->
              pendingLate <- rest
              world <- applyOp world op
            | [] -> ()
            result
        Resolve = Result.Ok
        Perform =
          fun op ->
            let decidedOn, decidedBranch, decidedProcess =
              match decided with
              | Some(_, w, b, p) -> w, b, p
              | None -> None, None, None
            let record operation doneOn killed =
              removals.Add
                { Operation = operation
                  DecidedOn = decidedOn
                  DecidedBranch = decidedBranch
                  DecidedProcess = decidedProcess
                  DoneOn = doneOn
                  KilledStartTicks = killed }
            match op with
            | Operation.RemoveWorktree(accepted, _, force) ->
              let path = Guard.path accepted
              match world.Worktrees |> Map.toList |> List.tryFind (fun (id, _) -> pathOf id = path) with
              | None ->
                match behavior with
                | Behavior.CountsGoneTwice -> Outcome.Done 100L
                | _ -> Outcome.Done 0L
              | Some(id, wt) ->
                // git worktree remove refuses a worktree with changes unless forced; the edge only forces
                // when only generated files differ, and checks that again itself.
                let refuses =
                  match behavior, force, wt.Dirt with
                  | Behavior.ForcesEverything, _, _ -> false
                  | _, _, Dirt.NoDirt -> false
                  | _, ForceNeed.OnlyGeneratedFilesDiffer, Dirt.GeneratedDirt -> false
                  | _, ForceNeed.DisposableCheckout, _ -> false
                  | _ -> true
                match refuses with
                | true -> Outcome.Failed "git refuses: the worktree has changes"
                | false ->
                  record (sprintf "remove worktree %d" id) (Some wt) None
                  world <- { world with Worktrees = Map.remove id world.Worktrees }
                  Outcome.Done 100L
            | Operation.DeleteBranch(_, name, how) ->
              match Map.tryFind name world.Branches with
              | None -> Outcome.Done 0L
              | Some b ->
                let verified =
                  match behavior, how with
                  | Behavior.ForcesEverything, _ -> true
                  | _, BranchDeletion.IfMerged -> (mergeOf world b = Some MergeHow.Ancestor)
                  | _, BranchDeletion.BecauseMergedBy _ -> (mergeOf world b |> Option.isSome)
                // A branch checked out in a worktree cannot be deleted at all.
                let checkedOut = world.Worktrees |> Map.exists (fun id _ -> branchOf id = name)
                match verified, checkedOut with
                | false, _ -> Outcome.Failed "branch is not fully merged"
                | _, true -> Outcome.Failed "branch is checked out in a worktree"
                | true, false ->
                  record (sprintf "delete branch %s" name) None None
                  world <- { world with Branches = Map.remove name world.Branches }
                  Outcome.Done 0L
            | Operation.Terminate(pid, ticks) ->
              match Map.tryFind pid world.Processes with
              | None -> Outcome.Done 0L
              | Some p ->
                let matches =
                  match behavior with
                  | Behavior.KillsByPid -> true
                  | _ -> p.StartTicks = ticks && not p.OwnerAlive
                match matches with
                | false -> Outcome.Failed "the pid is a different process now"
                | true ->
                  record (sprintf "stop pid %d" pid) None (Some p.StartTicks)
                  world <- { world with Processes = Map.remove pid world.Processes }
                  Outcome.Done 0L
            | Operation.RemoveTree _
            | Operation.RemoveFile _ -> Outcome.Done 0L }

  // ─── Running a scenario ──────────────────────────────────────────────

  type Memory ={ Shown: Plan option; Older: Plan option; Seen: Leftover list }

  /// Whether the confirmation is built against the plan that exists now (the tool's own path, where a plan
  /// that moved on is refused), or for the shown plan as it is (a caller that already holds a valid
  /// confirmation and runs the executor again: the executor's own second look must carry the safety).
  [<RequireQualifiedAccess>]
  type Currency =
    | AgainstCurrentPlan
    | ForShownPlan

  let private executeWith
    (behavior: Behavior)
    (currency: Currency)
    (world: World)
    (late: Op list)
    (shown: Plan)
    (shownLeftovers: Leftover list)
    : World * Removal list * int64 =
    let against =
      match currency with
      | Currency.AgainstCurrentPlan -> Planner.plan (gather world)
      | Currency.ForShownPlan -> shown
    match Confirmation.safeOnly against shown.Id with
    | Result.Error _ -> world, [], 0L
    | Result.Ok confirmation ->
      let run = Run(world, behavior, late, shownLeftovers)
      let report = Executor.run run.Effects roots confirmation shown
      run.World, run.Removals, report.ReclaimedBytes

  let step (behavior: Behavior) (world: World, memory: Memory) (op: Op) : Step * (World * Memory) =
    let before = world
    match op with
    | Op.PlanNow ->
      let leftovers = gather world
      let plan = Planner.plan leftovers
      let afterWorld, performed =
        match behavior with
        | Behavior.PlanningMutates ->
          // TWIN: planning runs the Safe steps as a side effect.
          let w, removals, _ = executeWith Behavior.Real Currency.ForShownPlan world [] plan leftovers
          w, removals
        | _ -> world, []
      let memory = { Shown = Some plan; Older = memory.Shown; Seen = leftovers }
      { Op = op; Before = before; After = afterWorld; Removals = performed; PlanId = Some plan.Id; Reclaimed = None; PerformedDuringPlanning = List.length performed },
      (afterWorld, memory)
    | Op.Execute late ->
      match memory.Shown with
      | None -> { Op = op; Before = before; After = world; Removals = []; PlanId = None; Reclaimed = None; PerformedDuringPlanning = 0 }, (world, memory)
      | Some plan ->
        let w, removals, reclaimed = executeWith behavior Currency.ForShownPlan world late plan memory.Seen
        { Op = op; Before = before; After = w; Removals = removals; PlanId = Some plan.Id; Reclaimed = Some reclaimed; PerformedDuringPlanning = 0 }, (w, memory)
    | Op.ExecuteAgain ->
      match memory.Shown with
      | None -> { Op = op; Before = before; After = world; Removals = []; PlanId = None; Reclaimed = None; PerformedDuringPlanning = 0 }, (world, memory)
      | Some plan ->
        let w, removals, reclaimed = executeWith behavior Currency.ForShownPlan world [] plan memory.Seen
        { Op = op; Before = before; After = w; Removals = removals; PlanId = Some plan.Id; Reclaimed = Some reclaimed; PerformedDuringPlanning = 0 }, (w, memory)
    | Op.ExecuteStale ->
      match memory.Older with
      | None -> { Op = op; Before = before; After = world; Removals = []; PlanId = None; Reclaimed = None; PerformedDuringPlanning = 0 }, (world, memory)
      | Some older ->
        let w, removals, reclaimed = executeWith behavior Currency.AgainstCurrentPlan world [] older memory.Seen
        { Op = op; Before = before; After = w; Removals = removals; PlanId = Some older.Id; Reclaimed = Some reclaimed; PerformedDuringPlanning = 0 }, (w, memory)
    | other ->
      let w = applyOp world other
      { Op = op; Before = before; After = w; Removals = []; PlanId = None; Reclaimed = None; PerformedDuringPlanning = 0 }, (w, memory)

  let trace (behavior: Behavior) (scenario: Scenario) : Trace =
    let folded =
      scenario.Ops
      |> List.fold (fun (acc: Step list, state: World * Memory) op ->
        let s, next = step behavior state op
        s :: acc, next) ([], (initial, { Shown = None; Older = None; Seen = [] }))
    { Scenario = scenario; Steps = fst folded |> List.rev; Initial = initial }

  // ─── Scenarios ───────────────────────────────────────────────────────

  let private randomOp (rng: Random) : Op =
    let how () = [| MergeHow.Ancestor; MergeHow.PatchEquivalent; MergeHow.TreeEqual |].[rng.Next 3]
    let dirt () = [| Dirt.NoDirt; Dirt.GeneratedDirt; Dirt.RealDirt |].[rng.Next 3]
    match rng.Next 20 with
    | 0 | 1 | 2 -> Op.AppearWorktree
    | 3 -> Op.AppearOrphan
    | 4 -> Op.BecomeBusy(rng.Next 8)
    | 5 -> Op.BecomeIdle(rng.Next 8)
    | 6 | 7 -> Op.Commit(rng.Next 8)
    | 8 | 9 | 10 -> Op.Merge(rng.Next 8, how ())
    | 11 | 12 -> Op.Dirty(rng.Next 8, dirt ())
    | 13 -> Op.RemoveExternally(rng.Next 8)
    | 14 -> Op.OwnerDies(rng.Next 4)
    | 15 -> Op.PidReused(rng.Next 4)
    | _ -> Op.PlanNow

  /// A pure function of `seed`: replaying a seed gives the identical trace.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 30 + rng.Next 50
    let lateOps () = [ for _ in 1 .. rng.Next 4 -> match randomOp rng with | Op.PlanNow -> Op.BecomeBusy(rng.Next 8) | other -> other ]
    let ops =
      [ for i in 1 .. n do
          match rng.Next 12 with
          | 0 -> yield Op.PlanNow
          | 1 ->
            // Plan and run back to back: the plan is current when it runs, so the steps really execute and
            // the window between looking and acting is where the world gets its say.
            yield Op.PlanNow
            yield Op.Execute(lateOps ())
          | 2 -> yield Op.ExecuteAgain
          | 3 -> yield Op.ExecuteStale
          | _ -> yield randomOp rng ]
    // Every scenario plans and runs at least once at the end, so the tail is always judged.
    { Seed = seed
      Ops = ops @ [ Op.PlanNow; Op.Execute []; Op.ExecuteAgain; Op.PlanNow; Op.Execute(lateOps ()); Op.ExecuteAgain ] }
