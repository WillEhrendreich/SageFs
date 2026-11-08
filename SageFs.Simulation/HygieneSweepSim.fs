namespace SageFs.Simulation

open System
open System.IO
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneLedger

/// Deterministic Simulation Testing for the hygiene SWEEP (docs/plans/hygiene-that-runs-itself.md, step 7): seeded worlds of
/// agent worktrees that appear, get busy, commit, collect uncommitted and ignored files, get locked by a person or by the harness,
/// get merged by history, by patch or by content, get removed by hand and made again, and sit while the clock moves, with a
/// sweep landing between any two of those.
///
/// The subject is the REAL decision code the sweep runs: `Ledger.transitions`, `Ledger.vanished`, `Ledger.holdBack`,
/// `HygieneAuto.plan`, `Confirmation.safeOnly` and `Executor.run`. The operations those decisions produce are carried out by a
/// `Performer` the caller supplies. This project cannot see the daemon's edge, so the test project hands in the REAL
/// `HygieneEdge` driven over a fake git that answers from the model world and can fail on a schedule (`HygieneSweepSimEdge`).
/// Nothing here re-implements what the edge checks: its fail-open, its restore points and its lock rule are the product's.
///
/// The invariant the plan names: the sweep never loses a commit, an uncommitted byte or an ignored file that is not
/// regenerated, and never overrides a lock a person made. Twins reintroduce one bug each, so a green run means the invariants
/// have teeth.
module HygieneSweepSim =

  /// What `git worktree lock` says about a worktree, by who made it and whether what it names is alive.
  [<RequireQualifiedAccess>]
  type Lock =
    | NoLock
    /// The harness's own format, its process gone: SageFs may release it.
    | AgentDead
    /// The harness's own format, its process alive: a use.
    | AgentLive
    /// A person's `--reason keep`, no pid.
    | PersonNoPid
    /// A person's reason that only looks like it names a pid.
    | PersonPidLike

  /// A unit of somebody's work: a commit on a branch, or an uncommitted change in a worktree.
  type Worktree =
    { Id: int
      Busy: HygieneSim.Use
      Lock: Lock
      /// When the worktree was made: a ledger record older than this is a record of another worktree.
      Born: DateTime
      /// Uncommitted work, by unit id. Real work, not build output.
      Dirty: Set<int>
      /// Ignored files no rule says are regenerated (a local `.env`, a `*.db`), by unit id. `git worktree remove` deletes them
      /// without a word.
      Ignored: Set<int>
      /// Build output only, which a forced removal drops by design.
      HasGenerated: bool }

  type World =
    { Worktrees: Map<int, Worktree>
      /// Agent branches by name, with the commits on them.
      Branches: Map<string, int list>
      /// How each commit reached the base. A commit merged by patch or content is NOT in the base's history: only its content is.
      Base: Map<int, MergeHow>
      /// Uncommitted units saved by a salvage, by salvage ref.
      Salvaged: Map<string, Set<int>>
      /// Commits kept by `refs/sagefs/removed/...`.
      Restore: Set<int>
      /// Every commit and every uncommitted unit ever made: what must never be lost.
      CommitsMade: Set<int>
      DirtMade: Set<int>
      IgnoredMade: Set<int>
      /// Worktrees a person removed by hand that no sweep has noticed yet, and those one has.
      Vacated: Set<int>
      VacatedSeen: Set<int>
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
    | IgnoreFile of nth: int
    | LockIt of nth: int * Lock
    | Merge of nth: int * MergeHow
    /// A person removes a worktree with `git worktree remove` (its branch stays).
    | RemoveByHand of nth: int
    /// An agent makes a worktree at the path of one removed by hand, after a sweep noticed it was gone.
    | Reuse of nth: int
    | Wait of hours: int
    | Sweep

  type Scenario = { Seed: int; Ops: Op list }

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// The real edge while git fails on a schedule (the commands that decide whether work reached the base, the one that reads a tip).
    | GitFaults
    /// TWIN: removes a worktree that has uncommitted work (a forced removal) and has not saved it first.
    | ForcesAndSkipsSalvage
    /// TWIN: a restore point that reports success and keeps nothing, so a commit the base only has by content is dropped.
    | NoRestorePoint
    /// TWIN: believes git's age and ignores the ledger: a worktree never seen before is tidied at once.
    | IgnoresLedger
    /// TWIN: reads every lock that names no live pid as stale and releases it (the old rule), so a person's lock is overridden.
    | UnlocksAnyLock
    /// TWIN: does not look at ignored files (the old status), so a worktree with a `.env` reads clean and is removed unsaved.
    | RemovesIgnoredFiles
    /// TWIN: never writes a `Vanished` line, so a worktree made at the path of one removed by hand inherits its quiet record.
    | ForgetsVanished

  /// One worktree removal the sweep made, with what the ledger knew when the sweep started and the lock it had.
  type Removal =
    { WorktreeId: int
      Known: WorktreeRecord option
      At: DateTime
      LockAtRemoval: Lock
      Born: DateTime }

  type Step = { Op: Op; After: World; Removals: Removal list }

  type Trace = { Scenario: Scenario; Steps: Step list }

  /// What the edge did with one operation over the model world it was handed: the outcome, the world afterwards, and the worktree
  /// it removed, if it removed one.
  type EdgeResult =
    { Outcome: Outcome
      World: World
      Removed: int option }

  /// Carries one operation out against the model. The test project supplies the REAL edge over a fake git (`HygieneSweepSimEdge`).
  type Performer = Behavior -> World -> Operation -> EdgeResult

  /// The grace a finished worktree is held for in the sim: a day, spelled here so the sim never reads it from the environment.
  let private graceHours = 24
  let grace : TimeSpan = TimeSpan.FromHours(float graceHours)

  let private start = DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)

  let initial : World =
    { Worktrees = Map.empty
      Branches = Map.empty
      Base = Map.empty
      Salvaged = Map.empty
      Restore = Set.empty
      CommitsMade = Set.empty
      DirtMade = Set.empty
      IgnoredMade = Set.empty
      Vacated = Set.empty
      VacatedSeen = Set.empty
      Lines = []
      Clock = start
      NextId = 1 }

  /// Real directories: the edge asks the file system whether a worktree is there, so the model's worktrees live in a temp area.
  let private scratch : Lazy<string> = lazy (Path.Combine(Path.GetTempPath(), sprintf "sagefs-sweepsim-%s" (Guid.NewGuid().ToString "N")))
  let worktreeRoot () : string = Path.Combine(scratch.Value, ".claude", "worktrees")
  let pathOf (id: int) : string = Path.Combine(worktreeRoot (), sprintf "agent-%d" id)
  let repoPath () : string = Path.Combine(scratch.Value, "repo")
  let roots () : Roots = { Entries = [ RootKind.AgentWorktrees, worktreeRoot () ]; NamePrefixes = [] }

  let private nth (m: Map<'k, 'v>) (n: int) : 'v option =
    match Map.count m with
    | 0 -> None
    | count -> m |> Map.toList |> List.item (n % count) |> snd |> Some

  let private nthOf (items: Set<int>) (n: int) : int option =
    match Set.count items with
    | 0 -> None
    | count -> items |> Set.toList |> List.item (n % count) |> Some

  let private salvageRef (id: int) : string = sprintf "refs/sagefs/salvage/agent-%d" id

  let private branchOf (id: int) : string = HygieneSim.branchOf id

  // ─── What gathering would say ────────────────────────────────────────

  let private unmerged (world: World) (commits: int list) : int list =
    commits |> List.filter (fun c -> not (Map.containsKey c world.Base))

  /// How a branch's commits have reached the base, from the model: the same reading the fake git answers from.
  let evidenceOf (world: World) (commits: int list) : MergeEvidence =
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
      Repo = RepoLink.InRepo(repoPath ())
      Branch = BranchLabel.NoBranch
      Display = ""
      Uses = []
      Lineage = Lineage.NoOwnerRecorded
      Retention = Retention.NeverExpires
      Git = GitEvidence.NoGitEvidence }

  let private worktreeSubject (behavior: Behavior) (world: World) (wt: Worktree) : Subject =
    let name = branchOf wt.Id
    let commits = Map.tryFind name world.Branches |> Option.defaultValue []
    // The old status never listed ignored files; the twin reads the world that way.
    let ignoredCounts =
      match behavior with
      | Behavior.RemovesIgnoredFiles -> false
      | Behavior.Real
      | Behavior.GitFaults
      | Behavior.ForcesAndSkipsSalvage
      | Behavior.NoRestorePoint
      | Behavior.IgnoresLedger
      | Behavior.UnlocksAnyLock
      | Behavior.ForgetsVanished -> true
    let changes =
      [ if not (Set.isEmpty wt.Dirty) then { Path = "src/work.fs"; Origin = ChangeOrigin.Real }
        if ignoredCounts && not (Set.isEmpty wt.Ignored) then { Path = ".env"; Origin = ChangeOrigin.Real }
        if wt.HasGenerated then { Path = "packages.lock.json"; Origin = ChangeOrigin.Generated "restore lock file" } ]
    // The old rule: only a lock naming a live pid is a use. The twin keeps it.
    let lockUse =
      match wt.Lock, behavior with
      | Lock.AgentLive, _ -> [ InUseReason.LockedByLiveProcess(1, "claude agent agent (pid 1 start 1)") ]
      | Lock.PersonNoPid, Behavior.UnlocksAnyLock
      | Lock.PersonPidLike, Behavior.UnlocksAnyLock -> []
      | Lock.PersonNoPid, _
      | Lock.PersonPidLike, _ -> [ InUseReason.LockedByPerson "keep" ]
      | Lock.AgentDead, _
      | Lock.NoLock, _ -> []
    { baseSubject world (Target.Directory(pathOf wt.Id)) LeftoverKind.AgentWorktree with
        Branch = BranchLabel.OnBranch name
        Retention = Retention.KeepFor grace
        Uses =
          (match wt.Busy with
           | HygieneSim.Use.Busy -> [ InUseReason.LiveSession(sprintf "s%d" wt.Id) ]
           | HygieneSim.Use.Idle -> [])
          @ lockUse
        Git = GitEvidence.Worktree(evidenceOf world commits, changes) }

  let private branchSubject (world: World) (name: string) (commits: int list) : Subject =
    { baseSubject world (Target.GitBranch(repoPath (), name)) LeftoverKind.StaleBranch with
        Branch = BranchLabel.OnBranch name
        Git = GitEvidence.Branch(evidenceOf world commits) }

  let private gather (behavior: Behavior) (world: World) : Leftover list =
    [ for wt in world.Worktrees |> Map.toList |> List.map snd -> classify world.Clock (worktreeSubject behavior world wt)
      for KeyValue(name, commits) in world.Branches -> classify world.Clock (branchSubject world name commits) ]

  let private subjectOfTarget (behavior: Behavior) (world: World) (target: Target) : Subject option =
    match target with
    | Target.Directory path ->
      world.Worktrees |> Map.toList |> List.tryPick (fun (id, wt) -> if pathOf id = path then Some(worktreeSubject behavior world wt) else None)
    | Target.GitBranch(_, name) -> Map.tryFind name world.Branches |> Option.map (branchSubject world name)
    | Target.File _
    | Target.RunningProcess _ -> None

  // ─── The world changing ──────────────────────────────────────────────

  let private updateWorktree (world: World) (n: int) (f: Worktree -> Worktree) : World =
    match nth world.Worktrees n with
    | None -> world
    | Some wt -> { world with Worktrees = Map.add wt.Id (f wt) world.Worktrees }

  let private freshWorktree (id: int) (born: DateTime) : Worktree =
    { Id = id
      Busy = HygieneSim.Use.Idle
      Lock = Lock.NoLock
      Born = born
      Dirty = Set.empty
      Ignored = Set.empty
      HasGenerated = false }

  let applyOp (world: World) (op: Op) : World =
    match op with
    | Op.Appear ->
      let id = world.NextId
      let commit = id * 1000
      { world with
          Worktrees = Map.add id (freshWorktree id world.Clock) world.Worktrees
          Branches = Map.add (branchOf id) [ commit ] world.Branches
          CommitsMade = Set.add commit world.CommitsMade
          NextId = id + 1 }
    | Op.BecomeBusy n -> updateWorktree world n (fun w -> { w with Busy = HygieneSim.Use.Busy })
    | Op.BecomeIdle n -> updateWorktree world n (fun w -> { w with Busy = HygieneSim.Use.Idle })
    | Op.Commit n ->
      match nth world.Worktrees n with
      | None -> world
      | Some wt ->
        let name = branchOf wt.Id
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
    | Op.IgnoreFile n ->
      match nth world.Worktrees n with
      | None -> world
      | Some wt ->
        let unit = wt.Id * 1000 + 700 + Set.count wt.Ignored
        { world with
            Worktrees = Map.add wt.Id { wt with Ignored = Set.add unit wt.Ignored } world.Worktrees
            IgnoredMade = Set.add unit world.IgnoredMade }
    | Op.LockIt(n, lock) -> updateWorktree world n (fun w -> { w with Lock = lock })
    | Op.Merge(n, how) ->
      match nth world.Branches n with
      | None -> world
      | Some commits -> { world with Base = commits |> List.fold (fun acc c -> Map.add c how acc) world.Base }
    | Op.RemoveByHand n ->
      // A person removes a worktree that holds nothing of value: git itself refuses one with changes, so only a clean one goes.
      match nth world.Worktrees n with
      | Some wt when Set.isEmpty wt.Dirty && Set.isEmpty wt.Ignored && wt.Lock = Lock.NoLock && wt.Busy = HygieneSim.Use.Idle ->
        { world with Worktrees = Map.remove wt.Id world.Worktrees; Vacated = Set.add wt.Id world.Vacated }
      | Some _
      | None -> world
    | Op.Reuse n ->
      match nthOf world.VacatedSeen n with
      | None -> world
      | Some id ->
        // The new worktree checks out the branch that is still there, as it was; a branch that is gone is made again.
        let name = branchOf id
        let commit = id * 1000 + 900
        let branches, made =
          match Map.tryFind name world.Branches with
          | Some _ -> world.Branches, world.CommitsMade
          | None -> Map.add name [ commit ] world.Branches, Set.add commit world.CommitsMade
        { world with
            Worktrees = Map.add id (freshWorktree id world.Clock) world.Worktrees
            Branches = branches
            CommitsMade = made
            Vacated = Set.remove id world.Vacated
            VacatedSeen = Set.remove id world.VacatedSeen }
    | Op.Wait hours -> { world with Clock = world.Clock + TimeSpan.FromHours(float hours) }
    | Op.Sweep -> world

  // ─── The sweep: the real decisions over the modelled world ───────────

  let private observed (world: World) : Observed list =
    world.Worktrees
    |> Map.toList
    |> List.map (fun (id, wt) ->
      { Path = pathOf id
        Branch = BranchLabel.OnBranch(branchOf id)
        Tip = sprintf "tip%d" id
        Maker = Maker.HarnessAgent(string id)
        Activity =
          (match wt.Busy with
           | HygieneSim.Use.Busy -> Activity.InUse
           | HygieneSim.Use.Idle -> Activity.Quiet) })

  /// TWIN support: a tidy that takes a worktree with uncommitted work for a clean one.
  let private believeDirtyIsClean (leftover: Leftover) : Leftover =
    match leftover with
    | Leftover.AgentWorktree(entry, detail) when Standing.hasUncommittedWork entry.Standing ->
      Leftover.AgentWorktree({ entry with Standing = Standing.Merged MergeHow.Ancestor }, detail)
    | other -> other

  let private sweepWith (performer: Performer) (behavior: Behavior) (world: World) : World * Removal list =
    let known = Ledger.fold world.Lines
    let seen = observed world
    // A worktree the ledger had on disk that is not among the ones just seen was removed by hand; the twin never says so.
    let vanished =
      match behavior with
      | Behavior.ForgetsVanished -> []
      | Behavior.Real
      | Behavior.GitFaults
      | Behavior.ForcesAndSkipsSalvage
      | Behavior.NoRestorePoint
      | Behavior.IgnoresLedger
      | Behavior.UnlocksAnyLock
      | Behavior.RemovesIgnoredFiles -> Ledger.vanished world.Clock known seen
    let lines = Ledger.transitions world.Clock known seen @ vanished
    let afterObserving = { world with Lines = world.Lines @ lines; VacatedSeen = Set.union world.VacatedSeen world.Vacated }
    // Salvage: uncommitted work of a worktree nobody is using goes to a ref of its own.
    let afterSalvage =
      match behavior with
      | Behavior.ForcesAndSkipsSalvage -> afterObserving
      | Behavior.Real
      | Behavior.GitFaults
      | Behavior.NoRestorePoint
      | Behavior.IgnoresLedger
      | Behavior.UnlocksAnyLock
      | Behavior.RemovesIgnoredFiles
      | Behavior.ForgetsVanished ->
        afterObserving.Worktrees
        |> Map.fold (fun (w: World) id wt ->
          // The twin that does not look at ignored files does not save them either.
          let toSave =
            match behavior with
            | Behavior.RemovesIgnoredFiles -> wt.Dirty
            | Behavior.Real
            | Behavior.GitFaults
            | Behavior.ForcesAndSkipsSalvage
            | Behavior.NoRestorePoint
            | Behavior.IgnoresLedger
            | Behavior.UnlocksAnyLock
            | Behavior.ForgetsVanished -> Set.union wt.Dirty wt.Ignored
          match wt.Busy, Set.isEmpty toSave with
          | HygieneSim.Use.Idle, false ->
            let saved = Map.tryFind (salvageRef id) w.Salvaged |> Option.defaultValue Set.empty
            { w with Salvaged = Map.add (salvageRef id) (Set.union saved toSave) w.Salvaged }
          | HygieneSim.Use.Idle, true
          | HygieneSim.Use.Busy, _ -> w) afterObserving
    // Tidy: the plan the daemon makes over what the ledger has cleared, run through the real executor and the real edge.
    let leftovers = gather behavior afterSalvage
    let held =
      match behavior with
      | Behavior.IgnoresLedger -> leftovers
      | Behavior.ForcesAndSkipsSalvage -> Ledger.holdBack grace world.Clock known leftovers |> List.map believeDirtyIsClean
      | Behavior.Real
      | Behavior.GitFaults
      | Behavior.NoRestorePoint
      | Behavior.UnlocksAnyLock
      | Behavior.RemovesIgnoredFiles
      | Behavior.ForgetsVanished -> Ledger.holdBack grace world.Clock known leftovers
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
              match subjectOfTarget behavior current target with
              | Some subject ->
                let fresh = classify current.Clock subject
                match behavior with
                | Behavior.ForcesAndSkipsSalvage -> Rechecked.Fresh(believeDirtyIsClean fresh)
                | Behavior.Real
                | Behavior.GitFaults
                | Behavior.NoRestorePoint
                | Behavior.IgnoresLedger
                | Behavior.UnlocksAnyLock
                | Behavior.RemovesIgnoredFiles
                | Behavior.ForgetsVanished -> Rechecked.Fresh fresh
              | None -> Rechecked.Gone
          Resolve = Result.Ok
          Perform =
            fun operation ->
              let current = cell.Value
              let result = performer behavior current operation
              cell.Value <- result.World
              match result.Removed with
              | Some id ->
                let before = Map.tryFind id current.Worktrees
                removals.Add
                  { WorktreeId = id
                    Known = Map.tryFind (pathOf id) known
                    At = current.Clock
                    LockAtRemoval = (match before with | Some wt -> wt.Lock | None -> Lock.NoLock)
                    Born = (match before with | Some wt -> wt.Born | None -> current.Clock) }
              | None -> ()
              result.Outcome }
      Executor.run effects (roots ()) confirmation plan |> ignore
      cell.Value, List.ofSeq removals

  // ─── Running a scenario ──────────────────────────────────────────────

  let step (performer: Performer) (behavior: Behavior) (world: World) (op: Op) : Step * World =
    match op with
    | Op.Sweep ->
      let after, removals = sweepWith performer behavior world
      { Op = op; After = after; Removals = removals }, after
    | other ->
      let after = applyOp world other
      { Op = op; After = after; Removals = [] }, after

  let trace (performer: Performer) (behavior: Behavior) (scenario: Scenario) : Trace =
    let steps, _ =
      scenario.Ops
      |> List.fold (fun (acc: Step list, world: World) op ->
        let s, next = step performer behavior world op
        s :: acc, next) ([], initial)
    { Scenario = scenario; Steps = List.rev steps }

  let private randomOp (rng: Random) : Op =
    let how () = [| MergeHow.Ancestor; MergeHow.PatchEquivalent; MergeHow.TreeEqual |].[rng.Next 3]
    let lock () = [| Lock.AgentDead; Lock.AgentLive; Lock.PersonNoPid; Lock.PersonPidLike |].[rng.Next 4]
    match rng.Next 28 with
    | 0 | 1 | 2 -> Op.Appear
    | 3 -> Op.BecomeBusy(rng.Next 8)
    | 4 | 5 -> Op.BecomeIdle(rng.Next 8)
    | 6 | 7 -> Op.Commit(rng.Next 8)
    | 8 | 9 -> Op.Dirty(rng.Next 8)
    | 10 | 11 | 12 -> Op.Merge(rng.Next 8, how ())
    | 13 | 14 -> Op.Wait(1 + rng.Next 30)
    | 15 | 16 -> Op.IgnoreFile(rng.Next 8)
    | 17 -> Op.LockIt(rng.Next 8, lock ())
    | 18 | 19 -> Op.LockIt(rng.Next 8, Lock.NoLock)
    | 20 | 21 -> Op.RemoveByHand(rng.Next 8)
    | 22 | 23 -> Op.Reuse(rng.Next 4)
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
  /// a salvage ref.
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
      | false -> Some { Index = i; Why = sprintf "uncommitted work %A is in no worktree and on no salvage ref" (Set.toList lost) })

  /// NEVER-LOSE-AN-IGNORED-FILE: after every step, every ignored file that no rule says is regenerated (a local `.env`) is in a
  /// worktree that still exists or on a salvage ref. `git worktree remove` deletes ignored files without a word.
  let neverLoseAnIgnoredFile (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.choose (fun (i, step) ->
      let w = step.After
      let inWorktrees = w.Worktrees |> Map.toList |> List.collect (fun (_, wt) -> Set.toList wt.Ignored) |> Set.ofList
      let saved = w.Salvaged |> Map.toList |> List.collect (fun (_, s) -> Set.toList s) |> Set.ofList
      let lost = w.IgnoredMade |> Set.filter (fun u -> not (Set.contains u inWorktrees) && not (Set.contains u saved))
      match Set.isEmpty lost with
      | true -> None
      | false -> Some { Index = i; Why = sprintf "ignored files %A are in no worktree and on no salvage ref" (Set.toList lost) })

  /// NEVER-OVERRIDE-A-PERSONS-LOCK: a worktree a person locked was never released or removed by the sweep.
  let neverOverrideAPersonsLock (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.collect (fun (i, step) ->
      step.Removals
      |> List.choose (fun r ->
        match r.LockAtRemoval with
        | Lock.PersonNoPid
        | Lock.PersonPidLike -> Some { Index = i; Why = sprintf "worktree %d had a lock a person made and the sweep released it and removed it" r.WorktreeId }
        | Lock.NoLock
        | Lock.AgentDead
        | Lock.AgentLive -> None))

  /// A WORKTREE IS TIDIED ONLY AFTER THE LEDGER HAS SEEN IT QUIET FOR THE GRACE: agent worktrees need a ledger entry, and the
  /// entry has to be about THIS worktree, not one that stood at the same path before.
  let removedOnlyAfterTheLedgerClearedIt (trace: Trace) : Violation list =
    trace.Steps
    |> List.indexed
    |> List.collect (fun (i, step) ->
      step.Removals
      |> List.choose (fun r ->
        match r.Known with
        | None -> Some { Index = i; Why = sprintf "worktree %d was removed before the ledger had ever seen it" r.WorktreeId }
        | Some record ->
          match record.FirstSeen < r.Born with
          | true -> Some { Index = i; Why = sprintf "worktree %d was removed on the strength of a record made before it existed (another worktree's)" r.WorktreeId }
          | false ->
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
