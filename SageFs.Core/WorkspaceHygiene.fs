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
open System.Security.Cryptography
open System.Text

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
  /// A running process was started from it (a host's own copy of the files it runs).
  | RunsFrom of pid: int * name: string
  /// A gate run for this repo is about to use it.
  | InvokingGate of repo: string

/// What the world says about who made a leftover and whether they are still around.
[<RequireQualifiedAccess>]
type Lineage =
  | OwnerGone of pid: int * since: DateTime
  | WorkingDirectoryGone of path: string * since: DateTime
  | OwnerAlive of pid: int
  /// The MCP connection that created it has closed. A worktree's standing is its git state; this is only who to say it was.
  | AgentGone of agentName: string * since: DateTime
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

/// How long a thing that nobody owns is kept before it is `Expired`, or how many of its kind are kept.
[<RequireQualifiedAccess>]
type Retention =
  | KeepFor of TimeSpan
  /// The newest `keep` of its kind are kept and the rest are superseded. `rank` is this one's place, from 0.
  | KeepNewest of rank: int * keep: int
  /// Never goes stale by age alone (a worktree: only its work decides).
  | NeverExpires

/// A kind of leftover, closed.
[<RequireQualifiedAccess>]
type LeftoverKind =
  | AgentWorktree
  | GateCheckout
  | GateTierClone
  /// The local gate's record of a commit that passed: its trust report and release bundle.
  | GatePassRecord
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
  /// One of the newest `keep` of its kind, which is what is kept; `rank` counts from 0.
  | Newest of rank: int * keep: int
  /// Older than the newest `keep` of its kind.
  | Superseded of keep: int
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
  | GatePassRecord of Entry
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

/// The directories SageFs manages. A root with a prefix (the OS temp dir, which is shared) only counts for
/// entries directly inside it whose name starts with that prefix.
type Roots =
  { Entries: (RootKind * string) list
    NamePrefixes: (RootKind * string) list }

/// Why a path could not be followed to where it really is.
[<RequireQualifiedAccess>]
type ResolveFailure =
  | TooManyLinks of path: string
  | Unreadable of path: string * detail: string

[<RequireQualifiedAccess>]
type Refusal =
  | OutsideKnownRoots of path: string
  | IsARoot of path: string
  | SymlinkEscapes of path: string * resolved: string
  | CannotResolve of path: string * ResolveFailure

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
    Resolve: string -> Result<string, ResolveFailure>
    Perform: Operation -> Outcome }

/// The same hands for a caller whose looking and doing wait on something outside the process (git, another process):
/// each is a Task, so the run awaits it and no thread is held while it waits. `Resolve` only reads the file system.
type AsyncEffects =
  { Recheck: Target -> System.Threading.Tasks.Task<Rechecked>
    Resolve: string -> Result<string, ResolveFailure>
    Perform: Operation -> System.Threading.Tasks.Task<Outcome> }

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

// ─── Describing ────────────────────────────────────────────────────────

module Kind =
  let describe (kind: LeftoverKind) : string =
    match kind with
    | LeftoverKind.AgentWorktree -> "agent worktree"
    | LeftoverKind.GateCheckout -> "gate checkout"
    | LeftoverKind.GateTierClone -> "gate tier clone"
    | LeftoverKind.GatePassRecord -> "gate pass record"
    | LeftoverKind.HostCacheEntry -> "host cache entry"
    | LeftoverKind.WorkerLogFile -> "worker log"
    | LeftoverKind.TempRunDir -> "temp run"
    | LeftoverKind.OrphanProcess -> "orphan process"
    | LeftoverKind.StaleBranch -> "agent branch"
    | LeftoverKind.SpawnedRegistryEntry -> "spawned-daemon registry entry"

  let all : LeftoverKind list =
    [ LeftoverKind.AgentWorktree; LeftoverKind.GateCheckout; LeftoverKind.GateTierClone; LeftoverKind.GatePassRecord
      LeftoverKind.HostCacheEntry; LeftoverKind.WorkerLogFile; LeftoverKind.TempRunDir
      LeftoverKind.OrphanProcess; LeftoverKind.StaleBranch; LeftoverKind.SpawnedRegistryEntry ]

module Target =
  let describe (target: Target) : string =
    match target with
    | Target.Directory path -> path
    | Target.File path -> path
    | Target.GitBranch(repo, name) -> sprintf "%s [branch %s]" repo name
    | Target.RunningProcess(pid, _) -> sprintf "pid %d" pid

module UnknownReason =
  let describe (why: UnknownReason) : string =
    match why with
    | UnknownReason.GitUnavailable detail -> sprintf "git would not answer (%s)" detail
    | UnknownReason.StatusUnreadable detail -> sprintf "its status could not be read (%s)" detail
    | UnknownReason.MergeUndecidable detail -> sprintf "whether it is merged could not be decided (%s)" detail
    | UnknownReason.PathOutsideKnownRoots path -> sprintf "%s is outside every directory SageFs manages" path
    | UnknownReason.SymlinkEscapesRoot(path, target) -> sprintf "%s is a link that leaves its directory (to %s)" path target
    | UnknownReason.OwnerLivenessUndecidable detail -> sprintf "whether its owner is alive could not be decided (%s)" detail
    | UnknownReason.NotReadable detail -> sprintf "it could not be read (%s)" detail
    | UnknownReason.NoRuleToJudgeBy -> "nothing says how long it is kept or who owns it"

module InUseReason =
  let describe (reason: InUseReason) : string =
    match reason with
    | InUseReason.LiveSession id -> sprintf "session %s is running in it" id
    | InUseReason.LeaseHeld holder -> sprintf "%s holds a lease on it" holder
    | InUseReason.ProcessWorkingDirectory(pid, name) -> sprintf "%s (pid %d) has it as its working directory" name pid
    | InUseReason.GateRunning pid -> sprintf "a gate run (pid %d) is using it" pid
    | InUseReason.CheckedOutInWorktree path -> sprintf "it is checked out in %s" path
    | InUseReason.OwnerAlive pid -> sprintf "its owner (pid %d) is alive" pid
    | InUseReason.UsedBySession id -> sprintf "session %s uses it" id
    | InUseReason.CurrentSdkHost sdk -> sprintf "it is the host for the SDK the daemon resolves now (%s)" sdk
    | InUseReason.LockedByLiveProcess(pid, reason) -> sprintf "locked by a live process (pid %d): %s" pid reason
    | InUseReason.RunsFrom(pid, name) -> sprintf "%s (pid %d) is running from it" name pid
    | InUseReason.InvokingGate repo -> sprintf "the gate run for %s is about to use it" repo

module MergeHow =
  let describe (how: MergeHow) : string =
    match how with
    | MergeHow.Ancestor -> "an ancestor of the base branch"
    | MergeHow.PatchEquivalent -> "every commit has an equivalent on the base branch (rebased or squashed)"
    | MergeHow.TreeEqual -> "its tree is identical to the base branch's"

/// Which paths a build or a test run writes again, and so are not anyone's work.
module Generated =
  let private segments (path: string) : string list =
    path.Replace('\\', '/').Split([| '/' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList

  /// The rule that says a changed path is regenerated, if one does.
  let ruleFor (path: string) : string option =
    let parts = segments path
    let name = match List.tryLast parts with | Some n -> n | None -> ""
    let directories = match parts with | [] -> [] | _ -> List.take (parts.Length - 1) parts
    let inDirectory (dir: string) = directories |> List.contains dir
    match () with
    | _ when inDirectory "bin" || inDirectory "obj" -> Some "build output"
    | _ when name = "packages.lock.json" -> Some "restore lock file the build rewrites"
    | _ when name.Contains ".received." -> Some "snapshot output a failed Verify run leaves"
    | _ when inDirectory "node_modules" -> Some "installed packages"
    | _ when inDirectory "TestResults" || inDirectory "test-results" -> Some "test results"
    | _ when inDirectory ".fable" || inDirectory "fable_modules" -> Some "Fable output"
    | _ when inDirectory ".vs" || inDirectory ".ionide" -> Some "editor state"
    | _ when inDirectory ".SageFs" -> Some "SageFs's own warmup state"
    | _ -> None

module Standing =
  let private files (ne: NonEmpty<ChangedFile>) : string =
    let all = NonEmpty.toList ne |> List.map (fun f -> f.Path)
    let shown = all |> List.truncate Thresholds.filesNamed
    let more = all.Length - shown.Length
    match more with
    | 0 -> String.Join(", ", shown)
    | n -> sprintf "%s and %d more" (String.Join(", ", shown)) n

  let describe (standing: Standing) : string =
    match standing with
    | Standing.InUse(first, more) ->
      sprintf "in use: %s" (String.Join("; ", (first :: more) |> List.map InUseReason.describe))
    | Standing.Merged how -> sprintf "merged: %s" (MergeHow.describe how)
    | Standing.CleanButUnmerged commits ->
      sprintf "%d commit(s) the base branch lacks (%s)" (NonEmpty.length commits) commits.Head.Subject
    | Standing.DirtyGenerated(listed, how) ->
      sprintf "merged (%s); only build output differs: %s" (MergeHow.describe how) (files listed)
    | Standing.DirtyReal listed -> sprintf "uncommitted work: %s" (files listed)
    | Standing.Orphaned since -> sprintf "orphaned: its owner has been gone since %s" (since.ToString "yyyy-MM-dd")
    | Standing.Expired(lastUsed, after) ->
      sprintf "expired: last used %s, kept for %g day(s)" (lastUsed.ToString "yyyy-MM-dd") after.TotalDays
    | Standing.WithinRetention until -> sprintf "kept until %s" (until.ToString "yyyy-MM-dd")
    | Standing.Newest(rank, keep) -> sprintf "kept: number %d of the newest %d" (rank + 1) keep
    | Standing.Superseded keep -> sprintf "superseded: older than the newest %d" keep
    | Standing.Unknown why -> sprintf "unknown: %s" (UnknownReason.describe why)

  let reclaimability (standing: Standing) : Reclaimability =
    match standing with
    | Standing.Merged _
    | Standing.DirtyGenerated _
    | Standing.Orphaned _
    | Standing.Expired _
    | Standing.Superseded _ -> Reclaimability.Reclaimable
    | Standing.CleanButUnmerged _
    | Standing.DirtyReal _ -> Reclaimability.NeedsReview
    | Standing.InUse _
    | Standing.WithinRetention _
    | Standing.Newest _
    | Standing.Unknown _ -> Reclaimability.Untouchable

// ─── Classifying ───────────────────────────────────────────────────────

let private byGit (merge: MergeEvidence) (changes: ChangedFile list) : Standing =
  let real = changes |> List.filter (fun f -> f.Origin = ChangeOrigin.Real)
  let generated = changes |> List.filter (fun f -> f.Origin <> ChangeOrigin.Real)
  match NonEmpty.tryOfList real with
  | Some files -> Standing.DirtyReal files
  | None ->
    match merge with
    | MergeEvidence.UnmergedCommits commits -> Standing.CleanButUnmerged commits
    | MergeEvidence.MergeNotDecidable why -> Standing.Unknown why
    | MergeEvidence.MergedInto how ->
      match NonEmpty.tryOfList generated with
      | Some files -> Standing.DirtyGenerated(files, how)
      | None -> Standing.Merged how

let private byLineage (now: DateTime) (subject: Subject) : Standing =
  match subject.Lineage with
  | Lineage.OwnerGone(_, since) -> Standing.Orphaned since
  | Lineage.WorkingDirectoryGone(_, since) -> Standing.Orphaned since
  | Lineage.AgentGone(_, since) -> Standing.Orphaned since
  | Lineage.OwnerAlive pid -> Standing.InUse(InUseReason.OwnerAlive pid, [])
  | Lineage.LineageUndecidable why -> Standing.Unknown(UnknownReason.OwnerLivenessUndecidable why)
  | Lineage.NoOwnerRecorded ->
    match subject.Retention with
    | Retention.NeverExpires -> Standing.Unknown UnknownReason.NoRuleToJudgeBy
    | Retention.KeepFor keep ->
      let until = subject.LastTouched + keep
      match now >= until with
      | true -> Standing.Expired(subject.LastTouched, keep)
      | false -> Standing.WithinRetention until
    | Retention.KeepNewest(rank, keep) ->
      match rank < keep with
      | true -> Standing.Newest(rank, keep)
      | false -> Standing.Superseded keep

/// Decide a standing from the facts. Total and deterministic: the same subject and clock give the same answer.
let standingOf (now: DateTime) (subject: Subject) : Standing =
  match subject.Uses with
  | first :: more -> Standing.InUse(first, more)
  | [] ->
    match subject.Git with
    | GitEvidence.GitUnreadable why -> Standing.Unknown why
    | GitEvidence.Branch merge -> byGit merge []
    | GitEvidence.Worktree(merge, changes) -> byGit merge changes
    | GitEvidence.NoGitEvidence -> byLineage now subject

/// Classify gathered facts into a leftover of the subject's kind.
let classify (now: DateTime) (subject: Subject) : Leftover =
  let entry : Entry =
    { Target = subject.Target
      SizeBytes = subject.SizeBytes
      Age = now - subject.LastTouched
      Owner = subject.Owner
      Lineage = subject.Lineage
      Standing = standingOf now subject }
  let nameOf (target: Target) =
    match target with
    | Target.Directory p
    | Target.File p -> IO.Path.GetFileName(p.TrimEnd('/', '\\'))
    | other -> Target.describe other
  match subject.Kind with
  | LeftoverKind.AgentWorktree -> Leftover.AgentWorktree(entry, { Repo = subject.Repo; Branch = subject.Branch })
  | LeftoverKind.GateCheckout ->
    let registration =
      match subject.Repo with
      | RepoLink.InRepo repo -> Registration.WorktreeOf repo
      | RepoLink.NoRepo -> Registration.NotRegistered
    Leftover.GateCheckout(entry, registration)
  | LeftoverKind.GateTierClone -> Leftover.GateTierClone entry
  | LeftoverKind.GatePassRecord -> Leftover.GatePassRecord entry
  | LeftoverKind.HostCacheEntry -> Leftover.HostCacheEntry(entry, nameOf subject.Target)
  | LeftoverKind.WorkerLogFile -> Leftover.WorkerLogFile entry
  | LeftoverKind.TempRunDir -> Leftover.TempRunDir entry
  | LeftoverKind.OrphanProcess -> Leftover.OrphanProcess(entry, { CommandLine = subject.Display })
  | LeftoverKind.StaleBranch ->
    let repo = match subject.Repo with | RepoLink.InRepo r -> r | RepoLink.NoRepo -> ""
    Leftover.StaleBranch(entry, repo)
  | LeftoverKind.SpawnedRegistryEntry -> Leftover.SpawnedRegistryEntry entry

module Leftover =
  let entry (leftover: Leftover) : Entry =
    match leftover with
    | Leftover.AgentWorktree(e, _)
    | Leftover.GateCheckout(e, _)
    | Leftover.HostCacheEntry(e, _)
    | Leftover.OrphanProcess(e, _)
    | Leftover.StaleBranch(e, _) -> e
    | Leftover.GateTierClone e
    | Leftover.GatePassRecord e
    | Leftover.WorkerLogFile e
    | Leftover.TempRunDir e
    | Leftover.SpawnedRegistryEntry e -> e

  let kind (leftover: Leftover) : LeftoverKind =
    match leftover with
    | Leftover.AgentWorktree _ -> LeftoverKind.AgentWorktree
    | Leftover.GateCheckout _ -> LeftoverKind.GateCheckout
    | Leftover.GateTierClone _ -> LeftoverKind.GateTierClone
    | Leftover.GatePassRecord _ -> LeftoverKind.GatePassRecord
    | Leftover.HostCacheEntry _ -> LeftoverKind.HostCacheEntry
    | Leftover.WorkerLogFile _ -> LeftoverKind.WorkerLogFile
    | Leftover.TempRunDir _ -> LeftoverKind.TempRunDir
    | Leftover.OrphanProcess _ -> LeftoverKind.OrphanProcess
    | Leftover.StaleBranch _ -> LeftoverKind.StaleBranch
    | Leftover.SpawnedRegistryEntry _ -> LeftoverKind.SpawnedRegistryEntry

  let target (leftover: Leftover) : Target = (entry leftover).Target

  let private branchOf (leftover: Leftover) : string =
    match leftover with
    | Leftover.AgentWorktree(_, detail) ->
      match detail.Branch with
      | BranchLabel.OnBranch name -> name
      | BranchLabel.DetachedAt sha -> "detached:" + sha
      | BranchLabel.NoBranch -> ""
    | _ -> ""

  /// What makes this leftover the same one across two looks: its kind, target and branch.
  let identity (leftover: Leftover) : string =
    sprintf "%s|%s|%s" (Kind.describe (kind leftover)) (Target.describe (target leftover)) (branchOf leftover)

// ─── The one path gate ─────────────────────────────────────────────────

module Guard =
  /// Collapse `.` and `..` and mixed separators without touching the disk.
  let normalize (path: string) : string =
    let parts = path.Replace('\\', '/').Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
    let folded =
      parts
      |> Array.fold (fun (acc: string list) part ->
        match part with
        | "." -> acc
        | ".." -> (match acc with | _ :: rest -> rest | [] -> [])
        | other -> other :: acc) []
      |> List.rev
    "/" + String.Join("/", folded)

  let private insideOf (root: string) (path: string) : bool =
    path.StartsWith(root + "/", StringComparison.Ordinal)

  /// Accept a path only if it is strictly inside a root SageFs manages, and still is after following links.
  let accept (roots: Roots) (resolve: string -> Result<string, ResolveFailure>) (path: string) : Result<Accepted, Refusal> =
    let normalized = normalize path
    let known = roots.Entries |> List.map (fun (kind, root) -> kind, normalize root)
    // A root with name prefixes only owns the entries directly inside it that carry one of them.
    let namedRight (kind: RootKind) (root: string) : bool =
      match roots.NamePrefixes |> List.filter (fun (k, _) -> k = kind) with
      | [] -> true
      | prefixes ->
        let first = normalized.Substring(root.Length + 1).Split('/').[0]
        prefixes |> List.exists (fun (_, prefix) -> first.StartsWith(prefix, StringComparison.Ordinal))
    match known |> List.tryFind (fun (_, root) -> root = normalized) with
    | Some _ -> Result.Error(Refusal.IsARoot normalized)
    | None ->
      match known |> List.tryFind (fun (kind, root) -> insideOf root normalized && namedRight kind root) with
      | None -> Result.Error(Refusal.OutsideKnownRoots normalized)
      | Some(kind, root) ->
        match resolve normalized, resolve root with
        | Result.Error why, _
        | _, Result.Error why -> Result.Error(Refusal.CannotResolve(normalized, why))
        | Result.Ok real, Result.Ok realRoot ->
          let real = normalize real
          match insideOf (normalize realRoot) real with
          | true -> Result.Ok { AcceptedPath = normalized; AcceptedRoot = kind }
          | false -> Result.Error(Refusal.SymlinkEscapes(normalized, real))

  let path (accepted: Accepted) : string = accepted.AcceptedPath
  let root (accepted: Accepted) : RootKind = accepted.AcceptedRoot

// ─── Planning ──────────────────────────────────────────────────────────

module Planner =
  let execution (risk: Risk) : Execution =
    match risk with
    | Risk.Safe -> Execution.RunsOnConfirm
    | Risk.UnmergedCommits
    | Risk.UncommittedWork
    | Risk.Unverifiable
    | Risk.Busy -> Execution.PresentedOnly

  let private riskRank (risk: Risk) : int =
    match risk with
    | Risk.Safe -> 0
    | Risk.UnmergedCommits -> 1
    | Risk.UncommittedWork -> 2
    | Risk.Unverifiable -> 3
    | Risk.Busy -> 4

  let private repoOf (link: RepoLink) : string =
    match link with
    | RepoLink.InRepo r -> r
    | RepoLink.NoRepo -> "."

  let private q (path: string) : string = if path.Contains ' ' then "'" + path + "'" else path

  let private pathOf (target: Target) : string =
    match target with
    | Target.Directory p
    | Target.File p -> p
    | other -> Target.describe other

  let private forceFlag (standing: Standing) : string =
    match standing with
    | Standing.DirtyGenerated _ -> " --force"
    | _ -> ""

  let private branchDeleteFlag (how: MergeHow) : string =
    match how with
    | MergeHow.Ancestor -> "-d"
    | MergeHow.PatchEquivalent
    | MergeHow.TreeEqual -> "-D"

  let private mergedHow (standing: Standing) : MergeHow option =
    match standing with
    | Standing.Merged how
    | Standing.DirtyGenerated(_, how) -> Some how
    | _ -> None

  /// What would reclaim this one, as the action and the exact command.
  let private reclaim (coveredBranch: string option) (leftover: Leftover) : Action * string =
    let entry = Leftover.entry leftover
    let path = pathOf entry.Target
    match leftover with
    | Leftover.OrphanProcess _ ->
      match entry.Target with
      | Target.RunningProcess(pid, _) -> Action.StopProcess pid, sprintf "kill %d" pid
      | _ -> Action.Remove, ""
    | Leftover.StaleBranch(_, repo) ->
      match entry.Target, mergedHow entry.Standing with
      | Target.GitBranch(_, name), Some how -> Action.Remove, sprintf "git -C %s branch %s %s" (q repo) (branchDeleteFlag how) name
      | _ -> Action.Remove, ""
    | Leftover.AgentWorktree(_, detail) ->
      let remove = sprintf "git -C %s worktree remove%s %s" (q (repoOf detail.Repo)) (forceFlag entry.Standing) (q path)
      match coveredBranch, mergedHow entry.Standing with
      | Some name, Some how ->
        Action.RemoveBranchToo name, sprintf "%s && git -C %s branch %s %s" remove (q (repoOf detail.Repo)) (branchDeleteFlag how) name
      | _ -> Action.Remove, remove
    | Leftover.GateCheckout(_, Registration.WorktreeOf repo) ->
      Action.Remove, sprintf "git -C %s worktree remove --force %s" (q repo) (q path)
    | Leftover.GateCheckout(_, Registration.NotRegistered)
    | Leftover.GateTierClone _
    | Leftover.GatePassRecord _
    | Leftover.HostCacheEntry _
    | Leftover.TempRunDir _
    | Leftover.WorkerLogFile _
    | Leftover.SpawnedRegistryEntry _ ->
      let command =
        match entry.Target with
        | Target.File _ -> sprintf "rm %s" (q path)
        | _ -> sprintf "rm -rf %s" (q path)
      match entry.Standing with
      | Standing.Expired(_, after) -> Action.PruneOlderThan after, command
      | _ -> Action.Remove, command

  let private patchPathFor (path: string) : string = path.TrimEnd('/') + ".hygiene.patch"

  let private ownerNote (entry: Entry) : string =
    match entry.Owner, entry.Lineage with
    | Owner.CreatedByAgent(agent, _), Lineage.AgentGone _
    | Owner.CreatedByAgent(agent, _), Lineage.OwnerGone _ -> sprintf " Made by agent %s, which is gone." agent
    | Owner.CreatedByAgent(agent, _), _ -> sprintf " Made by agent %s." agent
    | _ -> ""

  let private stepOf (coveredBranch: string option) (leftover: Leftover) : Step =
    let entry = Leftover.entry leftover
    let describe = Standing.describe entry.Standing
    let path = pathOf entry.Target
    let identity = Leftover.identity leftover
    let make action command reason bytes risk =
      { Id = StepId(sprintf "%s#%A" identity action)
        Target = leftover
        Action = action
        Command = command
        Reason = reason + ownerNote entry
        ReclaimsBytes = bytes
        Risk = risk }
    match entry.Standing with
    | Standing.InUse _ -> make Action.Nothing "" describe 0L Risk.Busy
    | Standing.WithinRetention _ -> make Action.Nothing "" describe 0L Risk.Busy
    | Standing.Newest _ -> make Action.Nothing "" describe 0L Risk.Busy
    | Standing.Unknown _ -> make Action.Nothing "" describe 0L Risk.Unverifiable
    | Standing.Merged _
    | Standing.DirtyGenerated _
    | Standing.Orphaned _
    | Standing.Superseded _
    | Standing.Expired _ ->
      let action, command = reclaim coveredBranch leftover
      let bytes =
        match action with
        | Action.StopProcess _ -> 0L
        | _ -> entry.SizeBytes
      make action command describe bytes Risk.Safe
    | Standing.CleanButUnmerged commits ->
      let command =
        match leftover with
        | Leftover.AgentWorktree(_, detail) ->
          let repo = q (repoOf detail.Repo)
          match detail.Branch with
          | BranchLabel.OnBranch name ->
            sprintf "git -C %s worktree remove %s   # the work stays reachable on branch %s" repo (q path) name
          | BranchLabel.DetachedAt sha ->
            sprintf "git -C %s branch rescue/%s %s && git -C %s worktree remove %s" repo (IO.Path.GetFileName(path.TrimEnd('/'))) sha repo (q path)
          | BranchLabel.NoBranch -> sprintf "git -C %s log --oneline" (q path)
        | Leftover.StaleBranch(_, repo) ->
          match entry.Target with
          | Target.GitBranch(_, name) -> sprintf "git -C %s log --oneline %s --not HEAD   # merge, or push it, then delete" (q repo) name
          | _ -> ""
        | _ -> sprintf "ls -la %s" (q path)
      make Action.KeepAndReview command describe entry.SizeBytes Risk.UnmergedCommits
    | Standing.DirtyReal _ ->
      let patch = patchPathFor path
      let repo =
        match leftover with
        | Leftover.AgentWorktree(_, detail) -> repoOf detail.Repo
        | _ -> "."
      let command =
        sprintf "git -C %s add -N . && git -C %s diff HEAD > %s && git -C %s worktree remove --force %s" (q path) (q path) (q patch) (q repo) (q path)
      make (Action.SaveDiffThenRemove patch) command describe entry.SizeBytes Risk.UncommittedWork

  let private idOf (steps: Step list) : PlanId =
    let text =
      steps
      |> List.map (fun s -> let (StepId id) = s.Id in sprintf "%s=%A" id s.Risk)
      |> List.sort
      |> String.concat "\n"
    use sha = SHA256.Create()
    let hash = sha.ComputeHash(Encoding.UTF8.GetBytes text)
    PlanId(Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant())

  let plan (leftovers: Leftover list) : Plan =
    // A merged branch whose worktree is also being removed is removed with the worktree, in one step.
    let branchesOf (l: Leftover) =
      match l with
      | Leftover.StaleBranch(entry, repo) ->
        match entry.Target, mergedHow entry.Standing with
        | Target.GitBranch(_, name), Some _ -> Some(repo, name)
        | _ -> None
      | _ -> None
    let removableBranches = leftovers |> List.choose branchesOf |> Set.ofList
    let coveredBranchOf (l: Leftover) : string option =
      match l with
      | Leftover.AgentWorktree(entry, detail) ->
        match detail.Repo, detail.Branch, mergedHow entry.Standing with
        | RepoLink.InRepo repo, BranchLabel.OnBranch name, Some _ when Set.contains (repo, name) removableBranches -> Some name
        | _ -> None
      | _ -> None
    let covered =
      leftovers
      |> List.choose (fun l ->
        match coveredBranchOf l, l with
        | Some name, Leftover.AgentWorktree(_, { Repo = RepoLink.InRepo repo }) -> Some(repo, name)
        | _ -> None)
      |> Set.ofList
    // A branch that a worktree in this list is checked out on, and that worktree is not going, cannot go either.
    let heldBranches =
      leftovers
      |> List.choose (fun l ->
        match l with
        | Leftover.AgentWorktree(_, { Repo = RepoLink.InRepo repo; Branch = BranchLabel.OnBranch name }) -> Some(repo, name)
        | _ -> None)
      |> Set.ofList
    let heldStep (l: Leftover) : Step =
      let step = stepOf None l
      { step with
          Action = Action.Nothing
          Command = ""
          Risk = Risk.Busy
          ReclaimsBytes = 0L
          Reason = "a worktree is checked out on this branch and is not being removed" }
    let steps =
      leftovers
      |> List.filter (fun l ->
        match branchesOf l with
        | Some key -> not (Set.contains key covered)
        | None -> true)
      |> List.map (fun l ->
        match branchesOf l with
        | Some key when Set.contains key heldBranches -> heldStep l
        | _ -> stepOf (coveredBranchOf l) l)
      |> List.sortBy (fun s -> riskRank s.Risk, -s.ReclaimsBytes, (let (StepId id) = s.Id in id))
    let total risk = steps |> List.filter (fun s -> s.Risk = risk) |> List.sumBy (fun s -> s.ReclaimsBytes)
    { Id = idOf steps
      Steps = steps
      SafeBytes = total Risk.Safe
      ReviewBytes = total Risk.UnmergedCommits + total Risk.UncommittedWork }

module Confirmation =
  /// A confirmation for `shown`, only while it is still the plan that exists.
  let safeOnly (current: Plan) (shown: PlanId) : Result<Confirmation, ConfirmError> =
    match current.Id = shown with
    | true -> Result.Ok(ConfirmedSafe shown)
    | false -> Result.Error(ConfirmError.PlanChanged(shown, current.Id))

// ─── Running ───────────────────────────────────────────────────────────

module Executor =
  let private accept (roots: Roots) (resolve: string -> Result<string, ResolveFailure>) (path: string) : Result<Accepted, SkipReason> =
    Guard.accept roots resolve path |> Result.mapError SkipReason.Refused

  let private pathOf (target: Target) : string =
    match target with
    | Target.Directory p
    | Target.File p -> p
    | other -> Target.describe other

  let private deletion (how: MergeHow) : BranchDeletion =
    match how with
    | MergeHow.Ancestor -> BranchDeletion.IfMerged
    | other -> BranchDeletion.BecauseMergedBy other

  let private mergeOf (standing: Standing) : MergeHow option =
    match standing with
    | Standing.Merged how
    | Standing.DirtyGenerated(_, how) -> Some how
    | _ -> None

  /// The operations that carry a step out, built from what the target looks like NOW.
  let private operationsFor
    (roots: Roots)
    (resolve: string -> Result<string, ResolveFailure>)
    (step: Step)
    (fresh: Leftover)
    : Result<Operation list, SkipReason> =
    let entry = Leftover.entry fresh
    let tree () =
      match entry.Target with
      | Target.File p -> accept roots resolve p |> Result.map (fun a -> [ Operation.RemoveFile a ])
      | Target.Directory p -> accept roots resolve p |> Result.map (fun a -> [ Operation.RemoveTree a ])
      | _ -> Result.Error(SkipReason.LookFailed "that kind of thing is not a path")
    match fresh with
    | Leftover.AgentWorktree(_, detail) ->
      let force =
        match entry.Standing with
        | Standing.DirtyGenerated _ -> ForceNeed.OnlyGeneratedFilesDiffer
        | _ -> ForceNeed.NoForce
      match detail.Repo with
      | RepoLink.NoRepo -> tree ()
      | RepoLink.InRepo repo ->
        match accept roots resolve (pathOf entry.Target) with
        | Result.Error skip -> Result.Error skip
        | Result.Ok a ->
          let removeWorktree = Operation.RemoveWorktree(a, repo, force)
          match step.Action, mergeOf entry.Standing with
          | Action.RemoveBranchToo name, Some how -> Result.Ok [ removeWorktree; Operation.DeleteBranch(repo, name, deletion how) ]
          | _ -> Result.Ok [ removeWorktree ]
    | Leftover.GateCheckout(_, Registration.WorktreeOf repo) ->
      accept roots resolve (pathOf entry.Target)
      |> Result.map (fun a -> [ Operation.RemoveWorktree(a, repo, ForceNeed.DisposableCheckout) ])
    | Leftover.GateCheckout(_, Registration.NotRegistered)
    | Leftover.GateTierClone _
    | Leftover.GatePassRecord _
    | Leftover.HostCacheEntry _
    | Leftover.TempRunDir _
    | Leftover.WorkerLogFile _
    | Leftover.SpawnedRegistryEntry _ -> tree ()
    | Leftover.OrphanProcess _ ->
      match entry.Target with
      | Target.RunningProcess(pid, ticks) -> Result.Ok [ Operation.Terminate(pid, ticks) ]
      | _ -> Result.Error(SkipReason.LookFailed "an orphan process has no pid")
    | Leftover.StaleBranch(_, repo) ->
      match entry.Target, mergeOf entry.Standing with
      | Target.GitBranch(_, name), Some how -> Result.Ok [ Operation.DeleteBranch(repo, name, deletion how) ]
      | _ -> Result.Error(SkipReason.LookFailed "no merge evidence for the branch")

  /// What a step comes to once its target has been looked at again: settled without touching anything, or a list of
  /// operations to carry out. The decision is the same for a run that waits on its effects and one that awaits them,
  /// so it is made here once.
  [<RequireQualifiedAccess>]
  type AfterLook =
    | Settled of StepResult
    | Operate of Operation list

  let afterLook
    (roots: Roots)
    (resolve: string -> Result<string, ResolveFailure>)
    (step: Step)
    (looked: Rechecked)
    : AfterLook =
    match looked with
    | Rechecked.Gone -> AfterLook.Settled StepResult.AlreadyGone
    | Rechecked.CouldNotLook why -> AfterLook.Settled(StepResult.Skipped(SkipReason.LookFailed why))
    | Rechecked.Fresh fresh ->
      match Leftover.identity fresh = Leftover.identity step.Target with
      | false -> AfterLook.Settled(StepResult.Skipped SkipReason.IdentityChanged)
      | true ->
        let standing = (Leftover.entry fresh).Standing
        match Standing.reclaimability standing with
        | Reclaimability.NeedsReview
        | Reclaimability.Untouchable -> AfterLook.Settled(StepResult.Skipped(SkipReason.StandingChanged standing))
        | Reclaimability.Reclaimable ->
          match operationsFor roots resolve step fresh with
          | Result.Error skip -> AfterLook.Settled(StepResult.Skipped skip)
          | Result.Ok operations -> AfterLook.Operate operations

  /// What one more operation adds to the outcome so far: the first failure ends it, and nothing after a failure runs.
  let afterOperation (soFar: Outcome) (next: unit -> Outcome) : Outcome =
    match soFar with
    | Outcome.Failed _ -> soFar
    | Outcome.Done bytes ->
      match next () with
      | Outcome.Done more -> Outcome.Done(bytes + more)
      | Outcome.Failed why -> Outcome.Failed why

  let perform (effects: Effects) (operations: Operation list) : Outcome =
    operations |> List.fold (fun acc op -> afterOperation acc (fun () -> effects.Perform op)) (Outcome.Done 0L)

  let runStep (effects: Effects) (roots: Roots) (step: Step) : StepResult =
    match Planner.execution step.Risk with
    | Execution.PresentedOnly -> StepResult.Presented
    | Execution.RunsOnConfirm ->
      match afterLook roots effects.Resolve step (effects.Recheck(Leftover.target step.Target)) with
      | AfterLook.Settled result -> result
      | AfterLook.Operate operations -> StepResult.Ran(perform effects operations)

  /// The report for the steps that were run: what each did, and how much was given back.
  let reportOf (steps: Executed list) : Report =
    let reclaimed =
      steps
      |> List.sumBy (fun e ->
        match e.Result with
        | StepResult.Ran(Outcome.Done bytes) -> bytes
        | _ -> 0L)
    { Executed = steps; ReclaimedBytes = reclaimed }

  /// Run the plan's Safe steps, each after looking at its target again. A confirmation made for another
  /// plan runs nothing.
  let run (effects: Effects) (roots: Roots) (confirmation: Confirmation) (plan: Plan) : Report =
    let (ConfirmedSafe confirmed) = confirmation
    match confirmed = plan.Id with
    | true -> plan.Steps |> List.map (fun step -> { Step = step; Result = runStep effects roots step }) |> reportOf
    | false -> plan.Steps |> List.map (fun step -> { Step = step; Result = StepResult.Presented }) |> reportOf

  let performAsync (effects: AsyncEffects) (operations: Operation list) : System.Threading.Tasks.Task<Outcome> =
    task {
      let mutable outcome = Outcome.Done 0L
      for op in operations do
        match outcome with
        | Outcome.Failed _ -> ()
        | Outcome.Done _ ->
          let! result = effects.Perform op
          outcome <- afterOperation outcome (fun () -> result)
      return outcome
    }

  let runStepAsync (effects: AsyncEffects) (roots: Roots) (step: Step) : System.Threading.Tasks.Task<StepResult> =
    task {
      match Planner.execution step.Risk with
      | Execution.PresentedOnly -> return StepResult.Presented
      | Execution.RunsOnConfirm ->
        let! looked = effects.Recheck(Leftover.target step.Target)
        match afterLook roots effects.Resolve step looked with
        | AfterLook.Settled result -> return result
        | AfterLook.Operate operations ->
          let! outcome = performAsync effects operations
          return StepResult.Ran outcome
    }

  /// `run` for hands that await: each step is looked at and carried out in turn, and no thread is held while a look or
  /// an operation waits on git or a process.
  let runAsync (effects: AsyncEffects) (roots: Roots) (confirmation: Confirmation) (plan: Plan) : System.Threading.Tasks.Task<Report> =
    task {
      let (ConfirmedSafe confirmed) = confirmation
      let executed = ResizeArray<Executed>()
      for step in plan.Steps do
        match confirmed = plan.Id with
        | true ->
          let! result = runStepAsync effects roots step
          executed.Add { Step = step; Result = result }
        | false -> executed.Add { Step = step; Result = StepResult.Presented }
      return reportOf (List.ofSeq executed)
    }
