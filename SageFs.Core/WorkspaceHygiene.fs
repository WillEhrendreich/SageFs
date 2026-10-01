/// Workspace hygiene: what agents and orchestrators leave behind, what each leftover's STANDING is
/// (why it is or is not safe to reclaim), and the plan that would tidy it.
///
/// Everything here is pure. The facts it decides over (git status, process tables, directory sizes) are
/// gathered at the edge by small interpreters (WorkspaceHygieneGather.fs) that tests replace with fakes.
/// Three rules shape it:
///
///   * Standing says WHY, never yes or no. `InUse`, `Merged`, `CleanButUnmerged`, `DirtyGenerated`,
///     `DirtyReal`, `Orphaned`, `Expired`, `WithinRetention` and `Unknown` each carry the evidence.
///   * A plan is a description. Nothing in this file touches the disk. Running a plan needs a
///     `Confirmation` that can only be built from the id of the plan the caller was shown, only runs
///     the Safe risk class, and looks at each target's standing again right before it acts.
///   * Fail closed. A path outside the roots SageFs knows, a symlink that leaves its root, a git that
///     will not answer, an owner whose liveness cannot be decided: all of them are `Unknown`, and an
///     `Unknown` is never removed.
module SageFs.WorkspaceHygiene

open System

// ─── Small closed types ───────────────────────────────────────────────

/// A list that cannot be empty. A `DirtyReal` with no files, or a `CleanButUnmerged` with no commits,
/// is not a state, so the type will not hold one.
type NonEmpty<'a> = { Head: 'a; Tail: 'a list }

module NonEmpty =
  let ofHeadTail (head: 'a) (tail: 'a list) : NonEmpty<'a> = { Head = head; Tail = tail }
  let tryOfList (items: 'a list) : NonEmpty<'a> option =
    match items with
    | [] -> None
    | head :: tail -> Some { Head = head; Tail = tail }
  let toList (ne: NonEmpty<'a>) : 'a list = ne.Head :: ne.Tail
  let length (ne: NonEmpty<'a>) : int = 1 + List.length ne.Tail

/// How a branch's work got into the base branch.
[<RequireQualifiedAccess>]
type MergeHow =
  /// The branch tip is an ancestor of the base: a plain merge or fast-forward.
  | Ancestor
  /// Every commit has a patch-equivalent on the base (`git cherry` shows no `+`): a rebase or squash.
  | PatchEquivalent
  /// The branch's tree is byte-identical to the base's, whatever the history.
  | TreeEqual

/// One commit the base does not have.
type Commit = { Sha: string; Subject: string }

/// Where an uncommitted change came from: something a build or a test run writes again, or work somebody did.
[<RequireQualifiedAccess>]
type ChangeOrigin =
  | Generated of rule: string
  | Real

type ChangedFile = { Path: string; Origin: ChangeOrigin }

/// Why SageFs could not decide, each one a reason to leave the thing alone.
[<RequireQualifiedAccess>]
type UnknownReason =
  | GitUnavailable of detail: string
  | StatusUnreadable of detail: string
  | MergeUndecidable of detail: string
  | PathOutsideKnownRoots of path: string
  | SymlinkEscapesRoot of path: string * target: string
  | OwnerLivenessUndecidable of detail: string
  | NotReadable of detail: string
  /// Nothing says how long this kind of thing is kept or who owns it.
  | NoRuleToJudgeBy

/// What proves a leftover is still being used.
[<RequireQualifiedAccess>]
type InUseReason =
  | LiveSession of sessionId: string
  | LeaseHeld of holder: string
  | ProcessWorkingDirectory of pid: int * name: string
  | GateRunning of pid: int
  | CheckedOutInWorktree of path: string
  | OwnerAlive of pid: int
  | UsedBySession of sessionId: string
  | CurrentSdkHost of sdk: string
  | LockedByLiveProcess of pid: int * reason: string

/// What the world says about who made a leftover and whether they are still around.
[<RequireQualifiedAccess>]
type Lineage =
  | OwnerGone of pid: int * since: DateTime
  | WorkingDirectoryGone of path: string * since: DateTime
  | OwnerAlive of pid: int
  | NoOwnerRecorded
  | LineageUndecidable of why: string

/// Who created a leftover, when SageFs recorded it. A connection id is the identity; an agent name is
/// only a label (two connections can declare the same name).
[<RequireQualifiedAccess>]
type Owner =
  | CreatedByAgent of agentName: string * connectionId: string
  | OwnedByProcess of pid: int
  | OwnerUnrecorded

/// The merge state of a branch, as gathered.
[<RequireQualifiedAccess>]
type MergeEvidence =
  | MergedInto of MergeHow
  | UnmergedCommits of NonEmpty<Commit>
  | MergeNotDecidable of UnknownReason

[<RequireQualifiedAccess>]
type GitEvidence =
  /// Nothing to ask git (a cache directory, a log file).
  | NoGitEvidence
  /// A branch: only its merge state matters.
  | Branch of MergeEvidence
  /// A worktree: its merge state and what differs in its tree.
  | Worktree of MergeEvidence * ChangedFile list
  | GitUnreadable of UnknownReason

/// What the thing is, in the world: a branch, a directory, a file, a process.
[<RequireQualifiedAccess>]
type Target =
  | Directory of path: string
  | File of path: string
  | GitBranch of repo: string * name: string
  | RunningProcess of pid: int * startTicks: int64

/// How long a thing that nobody owns is kept before it is `Expired`.
[<RequireQualifiedAccess>]
type Retention =
  | KeepFor of TimeSpan
  /// Never goes stale by age alone (a worktree: only its work decides).
  | NeverExpires

/// A kind of leftover, closed.
[<RequireQualifiedAccess>]
type LeftoverKind =
  | AgentWorktree
  | GateCheckout
  | GateTierClone
  | HostCacheEntry
  | WorkerLogFile
  | TempRunDir
  | OrphanProcess
  | StaleBranch
  | SpawnedRegistryEntry

[<RequireQualifiedAccess>]
type RepoLink =
  | InRepo of path: string
  | NoRepo

[<RequireQualifiedAccess>]
type BranchLabel =
  | OnBranch of name: string
  | DetachedAt of sha: string
  | NoBranch

/// Everything `classify` needs to know about one leftover.
type Subject =
  { Kind: LeftoverKind
    Target: Target
    SizeBytes: int64
    LastTouched: DateTime
    Owner: Owner
    Repo: RepoLink
    Branch: BranchLabel
    /// What a person would call it: a process's command line, a file name.
    Display: string
    Uses: InUseReason list
    Lineage: Lineage
    Retention: Retention
    Git: GitEvidence }

// ─── Standing ──────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type Standing =
  | InUse of first: InUseReason * more: InUseReason list
  | Merged of MergeHow
  | CleanButUnmerged of commits: NonEmpty<Commit>
  /// Merged, and the only files that differ are ones a build or test run writes again. The merge is
  /// carried because deleting the branch afterwards needs its evidence.
  | DirtyGenerated of files: NonEmpty<ChangedFile> * merged: MergeHow
  | DirtyReal of files: NonEmpty<ChangedFile>
  | Orphaned of since: DateTime
  /// Nobody has used it for longer than `after`.
  | Expired of lastUsed: DateTime * after: TimeSpan
  | WithinRetention of until: DateTime
  | Unknown of why: UnknownReason

/// What a standing allows. `Reclaimable` may be tidied after one confirm. `NeedsReview` is only ever
/// presented, with the command that saves the work first. `Untouchable` is left alone.
[<RequireQualifiedAccess>]
type Reclaimability =
  | Reclaimable
  | NeedsReview
  | Untouchable

type Entry =
  { Target: Target
    SizeBytes: int64
    Age: TimeSpan
    Owner: Owner
    Lineage: Lineage
    Standing: Standing }

type WorktreeDetail = { Repo: RepoLink; Branch: BranchLabel }

/// Whether a gate checkout is a worktree its repo still knows about.
[<RequireQualifiedAccess>]
type Registration =
  | WorktreeOf of repo: string
  | NotRegistered

type ProcessInfo = { CommandLine: string }

[<RequireQualifiedAccess>]
type Leftover =
  | AgentWorktree of Entry * WorktreeDetail
  | GateCheckout of Entry * Registration
  | GateTierClone of Entry
  | HostCacheEntry of Entry * sdk: string
  | WorkerLogFile of Entry
  | TempRunDir of Entry
  | OrphanProcess of Entry * ProcessInfo
  | StaleBranch of Entry * repo: string
  | SpawnedRegistryEntry of Entry

// ─── Plans ─────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type Risk =
  /// Nothing anyone made is lost: merged, generated, orphaned or expired.
  | Safe
  /// Commits would stop being reachable. Presented with the command that keeps them.
  | UnmergedCommits
  /// Uncommitted work would be lost. Presented with the command that saves a patch first.
  | UncommittedWork
  /// SageFs could not tell. Left alone.
  | Unverifiable
  /// Something is using it right now, or it is too young to judge.
  | Busy

/// What running a step is allowed to do.
[<RequireQualifiedAccess>]
type Execution =
  | RunsOnConfirm
  | PresentedOnly

[<RequireQualifiedAccess>]
type Action =
  /// Delete the target: a worktree, a directory, a file, a merged branch.
  | Remove
  /// Remove the worktree and delete its merged branch with it.
  | RemoveBranchToo of branch: string
  /// Save the diff to a patch file, then remove. Never run by tidy.
  | SaveDiffThenRemove of patchPath: string
  /// Look at it first. Never run by tidy.
  | KeepAndReview
  | StopProcess of pid: int
  /// Remove an entry of a cache that nobody has used for longer than this.
  | PruneOlderThan of TimeSpan
  | Nothing

type StepId = StepId of string

type Step =
  { Id: StepId
    Target: Leftover
    Action: Action
    /// The exact command that would run, or that a human should run.
    Command: string
    Reason: string
    ReclaimsBytes: int64
    Risk: Risk }

type PlanId = PlanId of string

type Plan =
  { Id: PlanId
    Steps: Step list
    /// Total bytes the Safe steps would give back.
    SafeBytes: int64
    /// Total bytes the steps that need a look would give back.
    ReviewBytes: int64 }

// ─── Roots and the one path gate ───────────────────────────────────────

[<RequireQualifiedAccess>]
type RootKind =
  | AgentWorktrees
  | GateState
  | HostCache
  | DataDir
  | TempRuns

type Roots = { Entries: (RootKind * string) list }

[<RequireQualifiedAccess>]
type Refusal =
  | OutsideKnownRoots of path: string
  | IsARoot of path: string
  | SymlinkEscapes of path: string * resolved: string
  | CannotResolve of path: string * why: string

/// A path that passed the gate. Only `Guard.accept` can make one.
type Accepted = private { AcceptedPath: string; AcceptedRoot: RootKind }

[<RequireQualifiedAccess>]
type ForceNeed =
  | NoForce
  | OnlyGeneratedFilesDiffer
  /// A gate checkout is reset hard before every run: nothing in it is anyone's work.
  | DisposableCheckout

[<RequireQualifiedAccess>]
type BranchDeletion =
  | IfMerged
  | BecauseMergedBy of MergeHow

[<RequireQualifiedAccess>]
type Operation =
  | RemoveWorktree of Accepted * repo: string * ForceNeed
  | RemoveTree of Accepted
  | RemoveFile of Accepted
  | DeleteBranch of repo: string * name: string * BranchDeletion
  | Terminate of pid: int * startTicks: int64

/// What happened when an operation ran.
[<RequireQualifiedAccess>]
type Outcome =
  | Done of reclaimedBytes: int64
  | Failed of why: string

/// What looking again right before acting found.
[<RequireQualifiedAccess>]
type Rechecked =
  | Fresh of Leftover
  | Gone
  | CouldNotLook of why: string

type Effects =
  { /// Gather the facts about one target again and classify them.
    Recheck: Target -> Rechecked
    /// Resolve a path to its real location (symlinks followed).
    Resolve: string -> Result<string, string>
    Perform: Operation -> Outcome }

/// What a confirmation is for. Built from the id of the plan the caller was shown.
type Confirmation = private ConfirmedSafe of PlanId

[<RequireQualifiedAccess>]
type SkipReason =
  | StandingChanged of Standing
  | IdentityChanged
  | Refused of Refusal
  | LookFailed of why: string

[<RequireQualifiedAccess>]
type StepResult =
  | Ran of Outcome
  | AlreadyGone
  | Skipped of SkipReason
  | Presented

type Executed = { Step: Step; Result: StepResult }

type Report =
  { Executed: Executed list
    ReclaimedBytes: int64 }

[<RequireQualifiedAccess>]
type ConfirmError =
  /// The plan the caller was shown is not the plan that exists now.
  | PlanChanged of shown: PlanId * current: PlanId

// ─── Policy: the named numbers ─────────────────────────────────────────

module Thresholds =
  /// A repo with more leftover worktrees than this gets the one-line nudge in session replies. Two is a
  /// normal amount of parallel work; the machine this was written for had a hundred.
  let leftoverWorktreeNudge = 5
  /// The gate's state dir past this size gets the nudge. One checkout and its tier clones is a few GB; the
  /// 172 GB this was written for is what a missing reaper does.
  let gateDirNudgeBytes = 40L * 1024L * 1024L * 1024L
  /// How many files a standing or a step names before it says "and N more".
  let filesNamed = 5

// ─── Functions (RED: not written yet) ──────────────────────────────────

module Kind =
  let describe (kind: LeftoverKind) : string = failwith "RED: not implemented"

module Target =
  let describe (target: Target) : string = failwith "RED: not implemented"

module Generated =
  /// The rule that says a changed path is regenerated by a build or a test run, if one does.
  let ruleFor (path: string) : string option = failwith "RED: not implemented"

module Standing =
  let describe (standing: Standing) : string = failwith "RED: not implemented"
  let reclaimability (standing: Standing) : Reclaimability = failwith "RED: not implemented"

/// Decide a standing from the facts. Total and deterministic: the same subject and clock give the same answer.
let standingOf (now: DateTime) (subject: Subject) : Standing = failwith "RED: not implemented"

/// Classify gathered facts into a leftover of the subject's kind.
let classify (now: DateTime) (subject: Subject) : Leftover = failwith "RED: not implemented"

module Leftover =
  let entry (leftover: Leftover) : Entry = failwith "RED: not implemented"
  let kind (leftover: Leftover) : LeftoverKind = failwith "RED: not implemented"
  let target (leftover: Leftover) : Target = failwith "RED: not implemented"
  /// What makes this leftover the same one across two looks: its kind, target and branch.
  let identity (leftover: Leftover) : string = failwith "RED: not implemented"

module Guard =
  let accept (roots: Roots) (resolve: string -> Result<string, string>) (path: string) : Result<Accepted, Refusal> =
    failwith "RED: not implemented"
  let path (accepted: Accepted) : string = failwith "RED: not implemented"
  let root (accepted: Accepted) : RootKind = failwith "RED: not implemented"

module Planner =
  let plan (leftovers: Leftover list) : Plan = failwith "RED: not implemented"
  let execution (risk: Risk) : Execution = failwith "RED: not implemented"

module Confirmation =
  /// A confirmation for `shown`, only while it is still the plan that exists.
  let safeOnly (current: Plan) (shown: PlanId) : Result<Confirmation, ConfirmError> = failwith "RED: not implemented"

module Executor =
  let run (effects: Effects) (roots: Roots) (confirmation: Confirmation) (plan: Plan) : Report =
    failwith "RED: not implemented"
