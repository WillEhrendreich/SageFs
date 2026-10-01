/// Gathering the facts workspace hygiene decides over: what git says about each worktree and branch, what the
/// process table says about who is using what, how big and how old each directory is. The decisions themselves
/// are `SageFs.WorkspaceHygiene` (pure); this is its edge.
///
/// Every function that asks the outside world takes it as an argument (`Git`, the process table, the clock) so
/// a test can answer for it. Nothing here removes anything: removing is `HygieneEdge`.
///
/// Fail closed: a git that will not run, a status that cannot be read, a process that cannot be inspected
/// each become an `Unknown` or a `LineageUndecidable`, never a guess that the thing is free.
module SageFs.HygieneGather

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open SageFs
open SageFs.WorkspaceHygiene

// ─── Git ───────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type GitResult =
  | Output of string
  | Exit of code: int * stderr: string
  | Unavailable of detail: string

/// Run `git <args>` in a directory. A function so tests can answer for it.
type Git = string -> string list -> GitResult

/// The real git: argument list (never a shell string), both streams drained, bounded by `Timeouts.gitQuick`.
let runGit : Git =
  fun dir args ->
    try
      let psi = ProcessStartInfo("git")
      psi.WorkingDirectory <- dir
      psi.RedirectStandardOutput <- true
      psi.RedirectStandardError <- true
      psi.UseShellExecute <- false
      for a in args do psi.ArgumentList.Add a
      use proc = Process.Start psi
      let out = proc.StandardOutput.ReadToEndAsync()
      let err = proc.StandardError.ReadToEndAsync()
      match proc.WaitForExit(int Timeouts.gitQuick.TotalMilliseconds) with
      | false ->
        (try proc.Kill(true) with _ -> ())
        GitResult.Unavailable(sprintf "git %s timed out" (String.Join(" ", args)))
      | true ->
        Task.WaitAll(out, err)
        match proc.ExitCode with
        | 0 -> GitResult.Output out.Result
        | code -> GitResult.Exit(code, err.Result.Trim())
    with ex -> GitResult.Unavailable ex.Message

// ─── Parsing what git prints ───────────────────────────────────────────

[<RequireQualifiedAccess>]
type LockState =
  | Unlocked
  | LockedWith of reason: string

type WorktreeEntry =
  { Path: string
    Head: string
    Branch: BranchLabel
    Lock: LockState }

module Parse =
  let private lines (text: string) : string list =
    text.Split([| '\n' |], StringSplitOptions.None) |> Array.map (fun l -> l.TrimEnd '\r') |> Array.toList

  let private branchPrefix = "refs/heads/"

  /// `git worktree list --porcelain`: blank-line-separated blocks of `key value` lines.
  let worktreeList (text: string) : WorktreeEntry list =
    let blocks =
      lines text
      |> List.fold (fun (acc: string list list) line ->
        match line, acc with
        | "", _ -> [] :: acc
        | _, current :: rest -> (line :: current) :: rest
        | _, [] -> [ [ line ] ]) [ [] ]
      |> List.rev
      |> List.map List.rev
      |> List.filter (not << List.isEmpty)
    blocks
    |> List.choose (fun block ->
      let value (key: string) =
        block |> List.tryPick (fun l -> if l = key then Some "" elif l.StartsWith(key + " ", StringComparison.Ordinal) then Some(l.Substring(key.Length + 1)) else None)
      match value "worktree" with
      | None -> None
      | Some path ->
        let head = value "HEAD" |> Option.defaultValue ""
        let branch =
          match value "branch" with
          | Some r when r.StartsWith(branchPrefix, StringComparison.Ordinal) -> BranchLabel.OnBranch(r.Substring branchPrefix.Length)
          | Some r -> BranchLabel.OnBranch r
          | None -> BranchLabel.DetachedAt head
        let lock =
          match value "locked" with
          | Some reason -> LockState.LockedWith reason
          | None -> LockState.Unlocked
        Some { Path = path; Head = head; Branch = branch; Lock = lock })

  /// `git status --porcelain=v1 -z`: `XY path` entries, a rename carrying its old path as the next entry.
  let statusPaths (text: string) : string list =
    let tokens = text.Split([| '\000' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    let rec go (rest: string list) (acc: string list) =
      match rest with
      | [] -> List.rev acc
      | entry :: more when entry.Length > 3 ->
        let path = entry.Substring 3
        match entry.[0] with
        | 'R'
        | 'C' -> go (match more with | _ :: afterOld -> afterOld | [] -> []) (path :: acc)
        | _ -> go more (path :: acc)
      | _ :: more -> go more acc
    go tokens []

  /// `git cherry -v base head`: `+ sha subject` for a commit the base lacks, `- sha subject` for one it has.
  let cherryAhead (text: string) : Commit list =
    lines text
    |> List.choose (fun l ->
      match l.StartsWith("+ ", StringComparison.Ordinal) with
      | false -> None
      | true ->
        let rest = l.Substring 2
        match rest.IndexOf ' ' with
        | -1 -> Some { Sha = rest; Subject = "" }
        | i -> Some { Sha = rest.Substring(0, i); Subject = rest.Substring(i + 1) })

  /// The pid a worktree lock names, when it names one: `claude agent agent-x (pid 314668 start 793942)`.
  let lockPid (reason: string) : int option =
    let marker = "(pid "
    match reason.IndexOf(marker, StringComparison.Ordinal) with
    | -1 -> None
    | i ->
      let rest = reason.Substring(i + marker.Length)
      let digits = rest |> Seq.takeWhile Char.IsDigit |> Seq.toArray |> String
      match Int32.TryParse digits with
      | true, pid when pid > 0 -> Some pid
      | _ -> None

// ─── What is running ───────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type CwdState =
  | At of path: string
  | Deleted of path: string
  | Unreadable

type Proc =
  { Pid: int
    StartTicks: int64
    Name: string
    CommandLine: string
    Cwd: CwdState
    /// Environment, only read for processes that look like SageFs (it is private to the user).
    Environment: Map<string, string> }

/// The process table, read once per scan. Linux reads /proc; elsewhere only names and ids are known and every
/// working directory is `Unreadable`, which makes every directory's use "undecidable" rather than "free".
let readProcesses () : Proc list =
  let looksLikeSageFs (cmd: string) =
    cmd.Contains "SageFs" || cmd.Contains "FsiHost" || cmd.Contains "sagefs"
  match Directory.Exists "/proc/self" with
  | false -> []
  | true ->
    Directory.EnumerateDirectories "/proc"
    |> Seq.choose (fun dir ->
      match Int32.TryParse(Path.GetFileName dir) with
      | true, pid ->
        try
          let cmd = (File.ReadAllText(Path.Combine(dir, "cmdline"))).Replace('\000', ' ').Trim()
          let name = (try (File.ReadAllText(Path.Combine(dir, "comm"))).Trim() with _ -> "")
          let cwd =
            try
              match FileInfo(Path.Combine(dir, "cwd")).LinkTarget with
              | null -> CwdState.Unreadable
              | target when target.EndsWith " (deleted)" -> CwdState.Deleted(target.Substring(0, target.Length - " (deleted)".Length))
              | target -> CwdState.At target
            with _ -> CwdState.Unreadable
          let environment =
            match looksLikeSageFs cmd with
            | false -> Map.empty
            | true ->
              try
                File.ReadAllText(Path.Combine(dir, "environ")).Split([| '\000' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose (fun kv -> match kv.IndexOf '=' with | -1 -> None | i -> Some(kv.Substring(0, i), kv.Substring(i + 1)))
                |> Map.ofArray
              with _ -> Map.empty
          let ticks =
            match OwnerMonitor.getProcessById pid with
            | Some p -> (try OwnerMonitor.startTimeTicksOf p with _ -> 0L)
            | None -> 0L
          Some { Pid = pid; StartTicks = ticks; Name = name; CommandLine = cmd; Cwd = cwd; Environment = environment }
        with _ -> None
      | _ -> None)
    |> Seq.toList

// ─── The facts a scan needs from the running daemon ─────────────────────

/// What only a running daemon knows. A scan run without a daemon passes `LiveFacts.none`.
type LiveFacts =
  { Sessions: (string * string) list
    /// The SDK-keyed host directories the daemon resolves now.
    CurrentHostKeys: string list
    /// Who created the session in each working directory, from the daemon's session ledger.
    Owners: (string * Owner) list }

module LiveFacts =
  let none : LiveFacts = { Sessions = []; CurrentHostKeys = []; Owners = [] }

type Locations =
  { Repo: string
    GateDir: string
    DataDir: string
    HostCacheDir: string
    TempDir: string }

/// The names that make a branch an agent's: the prefix the harness gives a worktree's branch.
let agentBranchPrefixes : string list = [ "worktree-agent-" ]

/// The branches a merge is judged against, in order of preference.
let baseBranchCandidates : string list = [ "master"; "main" ]

/// Which of the roots a directory leftover can live under.
let rootsOf (loc: Locations) : Roots =
  { Entries =
      [ RootKind.AgentWorktrees, Path.Combine(loc.Repo, ".claude", "worktrees")
        RootKind.GateState, loc.GateDir
        RootKind.HostCache, loc.HostCacheDir
        RootKind.DataDir, Path.Combine(loc.DataDir, "workers")
        RootKind.DataDir, Path.Combine(loc.DataDir, "spawned")
        RootKind.TempRuns, loc.TempDir ]
    NamePrefixes =
      [ RootKind.TempRuns, "sagefs-"
        RootKind.GateState, "checkout-" ] }

type Scan =
  { Now: DateTime
    Loc: Locations
    Git: Git
    Processes: Proc list
    Live: LiveFacts
    /// Whether a process is alive, by pid and (when known) start time.
    IsAlive: int -> int64 option -> bool }

// ─── Sizes and times ───────────────────────────────────────────────────

/// Total file size under a directory, links not followed, what cannot be read counted as nothing.
let directorySize (dir: string) : int64 =
  try
    let options = EnumerationOptions(RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint)
    DirectoryInfo(dir).EnumerateFiles("*", options) |> Seq.sumBy (fun f -> try f.Length with _ -> 0L)
  with _ -> 0L

let private touched (path: string) : DateTime =
  try Directory.GetLastWriteTimeUtc path with _ -> DateTime.UtcNow

let private fileTouched (path: string) : DateTime =
  try File.GetLastWriteTimeUtc path with _ -> DateTime.UtcNow

let private normalize (p: string) : string = Guard.normalize p

let private under (parent: string) (child: string) : bool =
  let p = normalize parent
  let c = normalize child
  c = p || c.StartsWith(p + "/", StringComparison.Ordinal)

// ─── Merge evidence ────────────────────────────────────────────────────

let private baseBranch (git: Git) (repo: string) : string option =
  baseBranchCandidates
  |> List.tryFind (fun name ->
    match git repo [ "rev-parse"; "--verify"; "--quiet"; "refs/heads/" + name ] with
    | GitResult.Output _ -> true
    | _ -> false)

/// Whether `head` has reached `baseRef`, and how: an ancestor, every commit patch-equivalent (rebased or
/// squashed), or an identical tree. What is left is the commits it lacks.
let mergeEvidence (git: Git) (repo: string) (baseRef: string) (head: string) : MergeEvidence =
  let undecidable (what: string) (detail: string) = MergeEvidence.MergeNotDecidable(UnknownReason.MergeUndecidable(sprintf "%s: %s" what detail))
  match git repo [ "merge-base"; "--is-ancestor"; head; baseRef ] with
  | GitResult.Output _ -> MergeEvidence.MergedInto MergeHow.Ancestor
  | GitResult.Unavailable detail -> MergeEvidence.MergeNotDecidable(UnknownReason.GitUnavailable detail)
  | GitResult.Exit(code, stderr) when code <> 1 -> undecidable "merge-base" stderr
  | GitResult.Exit _ ->
    match git repo [ "cherry"; "-v"; baseRef; head ] with
    | GitResult.Unavailable detail -> MergeEvidence.MergeNotDecidable(UnknownReason.GitUnavailable detail)
    | GitResult.Exit(_, stderr) -> undecidable "cherry" stderr
    | GitResult.Output text ->
      match Parse.cherryAhead text with
      | [] -> MergeEvidence.MergedInto MergeHow.PatchEquivalent
      | ahead ->
        match git repo [ "diff"; "--quiet"; baseRef; head ] with
        | GitResult.Output _ -> MergeEvidence.MergedInto MergeHow.TreeEqual
        | GitResult.Unavailable detail -> MergeEvidence.MergeNotDecidable(UnknownReason.GitUnavailable detail)
        | GitResult.Exit(1, _) ->
          match NonEmpty.tryOfList ahead with
          | Some commits -> MergeEvidence.UnmergedCommits commits
          | None -> undecidable "cherry" "no commits listed"
        | GitResult.Exit(_, stderr) -> undecidable "diff" stderr

/// What differs in a worktree, each file told apart as build output or work.
let changedFiles (git: Git) (worktree: string) : Result<ChangedFile list, UnknownReason> =
  match git worktree [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=normal" ] with
  | GitResult.Output text ->
    Parse.statusPaths text
    |> List.map (fun path ->
      match Generated.ruleFor path with
      | Some rule -> ({ Path = path; Origin = ChangeOrigin.Generated rule } : ChangedFile)
      | None -> ({ Path = path; Origin = ChangeOrigin.Real } : ChangedFile))
    |> Result.Ok
  | GitResult.Exit(_, stderr) -> Result.Error(UnknownReason.StatusUnreadable stderr)
  | GitResult.Unavailable detail -> Result.Error(UnknownReason.GitUnavailable detail)

// ─── Who is using a path ───────────────────────────────────────────────

let private usesOf (scan: Scan) (path: string) : InUseReason list =
  [ for (id, wd) in scan.Live.Sessions do
      if under path wd then yield InUseReason.LiveSession id
    for p in scan.Processes do
      match p.Cwd with
      | CwdState.At cwd when under path cwd -> yield InUseReason.ProcessWorkingDirectory(p.Pid, (if p.Name = "" then "process" else p.Name))
      | _ -> () ]

let private ownerOf (scan: Scan) (path: string) : Owner =
  scan.Live.Owners
  |> List.tryPick (fun (wd, owner) -> if under path wd || under wd path then Some owner else None)
  |> Option.defaultValue Owner.OwnerUnrecorded

// ─── Worktrees, gate checkouts and branches ─────────────────────────────

/// A thing to examine: its target and kind are cheap to know; its facts (size, git, owner) are only built
/// when asked, so looking at ONE target again does not pay for the whole machine.
type Candidate =
  { Target: Target
    Kind: LeftoverKind
    Build: unit -> Subject }

let private subjectBase (kind: LeftoverKind) (target: Target) : Subject =
  { Kind = kind
    Target = target
    SizeBytes = 0L
    LastTouched = DateTime.UtcNow
    Owner = Owner.OwnerUnrecorded
    Repo = RepoLink.NoRepo
    Branch = BranchLabel.NoBranch
    Display = ""
    Uses = []
    Lineage = Lineage.NoOwnerRecorded
    Retention = Retention.NeverExpires
    Git = GitEvidence.NoGitEvidence }

let private isAgentBranch (name: string) : bool =
  agentBranchPrefixes |> List.exists (fun p -> name.StartsWith(p, StringComparison.Ordinal))

let private worktreeEntries (scan: Scan) : Result<WorktreeEntry list, UnknownReason> =
  match scan.Git scan.Loc.Repo [ "worktree"; "list"; "--porcelain" ] with
  | GitResult.Output text -> Result.Ok(Parse.worktreeList text)
  | GitResult.Exit(_, stderr) -> Result.Error(UnknownReason.GitUnavailable stderr)
  | GitResult.Unavailable detail -> Result.Error(UnknownReason.GitUnavailable detail)

/// What the lock on a worktree says: a lock whose named process is alive is a use; a lock whose named process
/// is gone is nothing, and the edge unlocks it before removing.
let private lockUse (scan: Scan) (lock: LockState) : InUseReason list =
  match lock with
  | LockState.Unlocked -> []
  | LockState.LockedWith reason ->
    match Parse.lockPid reason with
    | Some pid when scan.IsAlive pid None -> [ InUseReason.LockedByLiveProcess(pid, reason) ]
    | _ -> []

let private worktreeCandidate (scan: Scan) (baseRef: string option) (entry: WorktreeEntry) (kind: LeftoverKind) : Candidate =
  let target = Target.Directory(normalize entry.Path)
  { Target = target
    Kind = kind
    Build =
      fun () ->
        let path = normalize entry.Path
        let git =
          match baseRef with
          | None -> GitEvidence.GitUnreadable(UnknownReason.MergeUndecidable "no master or main branch to judge a merge against")
          | Some b ->
            let merge = mergeEvidence scan.Git scan.Loc.Repo b (match entry.Branch with | BranchLabel.OnBranch n -> n | _ -> entry.Head)
            match changedFiles scan.Git path with
            | Result.Ok files -> GitEvidence.Worktree(merge, files)
            | Result.Error why -> GitEvidence.GitUnreadable why
        { subjectBase kind target with
            SizeBytes = directorySize path
            LastTouched = touched path
            Owner = ownerOf scan path
            Repo = RepoLink.InRepo scan.Loc.Repo
            Branch = entry.Branch
            Display = Path.GetFileName path
            Uses = usesOf scan path @ lockUse scan entry.Lock
            Git = git } }

/// A worktree git reports that is under no root SageFs manages: listed so the orchestrator sees it, never touched.
let private outsideCandidate (scan: Scan) (entry: WorktreeEntry) : Candidate =
  let path = normalize entry.Path
  let target = Target.Directory path
  { Target = target
    Kind = LeftoverKind.AgentWorktree
    Build =
      fun () ->
        { subjectBase LeftoverKind.AgentWorktree target with
            SizeBytes = directorySize path
            LastTouched = touched path
            Repo = RepoLink.InRepo scan.Loc.Repo
            Branch = entry.Branch
            Display = path
            Uses = usesOf scan path
            Git = GitEvidence.GitUnreadable(UnknownReason.PathOutsideKnownRoots path) } }

let private branchCandidates (scan: Scan) (baseRef: string option) (entries: WorktreeEntry list) : Candidate list =
  match scan.Git scan.Loc.Repo [ "for-each-ref"; "--format=%(refname:short)%09%(committerdate:unix)"; "refs/heads/" ] with
  | GitResult.Output text ->
    text.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList
    |> List.choose (fun line ->
      match line.Split('\t') with
      | [| name; unix |] when isAgentBranch name ->
        let target = Target.GitBranch(scan.Loc.Repo, name)
        Some
          { Target = target
            Kind = LeftoverKind.StaleBranch
            Build =
              fun () ->
                let seconds = match Int64.TryParse unix with | true, s -> s | _ -> 0L
                let checkedOutIn = entries |> List.tryFind (fun e -> e.Branch = BranchLabel.OnBranch name)
                // The branch of a worktree this scan may remove is that worktree's to carry: the planner pairs them.
                let managedRoot = Path.Combine(scan.Loc.Repo, ".claude", "worktrees")
                let uses =
                  match checkedOutIn with
                  | Some e when under managedRoot e.Path -> []
                  | Some e -> [ InUseReason.CheckedOutInWorktree(normalize e.Path) ]
                  | None -> []
                let git =
                  match baseRef with
                  | None -> GitEvidence.GitUnreadable(UnknownReason.MergeUndecidable "no master or main branch to judge a merge against")
                  | Some b -> GitEvidence.Branch(mergeEvidence scan.Git scan.Loc.Repo b name)
                { subjectBase LeftoverKind.StaleBranch target with
                    LastTouched = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                    Repo = RepoLink.InRepo scan.Loc.Repo
                    Branch = BranchLabel.OnBranch name
                    Display = name
                    Uses = uses
                    Git = git } }
      | _ -> None)
  | _ -> []

// ─── The gate's checkouts, the host cache, logs, temp runs, registry ────

let private childDirs (dir: string) : string list =
  try Directory.EnumerateDirectories dir |> Seq.map normalize |> Seq.toList with _ -> []

let private childFiles (dir: string) : string list =
  try Directory.EnumerateFiles dir |> Seq.map normalize |> Seq.toList with _ -> []

/// The pid and sha of a gate run in progress, from the gate's `current` file, if that process is alive.
let private runningGate (scan: Scan) : int option =
  try
    let text = File.ReadAllText(Path.Combine(scan.Loc.GateDir, "current")).Trim()
    match text.Split(' ') with
    | [| pid; _ |] ->
      match Int32.TryParse pid with
      | true, p when scan.IsAlive p None -> Some p
      | _ -> None
    | _ -> None
  with _ -> None

/// The repo a gate checkout was made for: recorded by the gate (`owners/<name>`), else the repo whose path hashes
/// to the checkout's name (the gate keys a checkout by sha1 of the invoking repo path).
let private gateOwnerRepo (scan: Scan) (name: string) : string option =
  let recorded =
    try
      let file = Path.Combine(scan.Loc.GateDir, "owners", name)
      match File.Exists file with
      | true -> (File.ReadAllLines file |> Array.tryHead |> Option.map (fun l -> l.Trim()))
      | false -> None
    with _ -> None
  match recorded with
  | Some repo -> Some repo
  | None ->
    let hash (p: string) =
      use sha = System.Security.Cryptography.SHA1.Create()
      Convert.ToHexString(sha.ComputeHash(Text.Encoding.UTF8.GetBytes p)).ToLowerInvariant().Substring(0, 8)
    match name.StartsWith "checkout-" && name.Substring("checkout-".Length).Length >= 8 with
    | true when name.Substring("checkout-".Length, 8) = hash scan.Loc.Repo -> Some scan.Loc.Repo
    | _ -> None

let private gateCandidates (scan: Scan) (entries: WorktreeEntry list) : Candidate list =
  let gateRunning = runningGate scan
  let make (kind: LeftoverKind) (dir: string) : Candidate =
    let target = Target.Directory dir
    { Target = target
      Kind = kind
      Build =
        fun () ->
          let name = Path.GetFileName(dir.TrimEnd '/')
          let topName = name
          let ownerName = (match kind with | LeftoverKind.GateTierClone -> Path.GetFileName(Path.GetDirectoryName dir) | _ -> topName).Replace(".tiers", "")
          let ownerRepo = gateOwnerRepo scan ownerName
          let lineage =
            match ownerRepo with
            | Some repo when not (Directory.Exists repo) -> Lineage.WorkingDirectoryGone(repo, touched dir)
            | _ -> Lineage.NoOwnerRecorded
          let registered = entries |> List.tryFind (fun e -> normalize e.Path = dir)
          { subjectBase kind target with
              SizeBytes = directorySize dir
              LastTouched = touched dir
              Repo = (match registered with | Some _ -> RepoLink.InRepo scan.Loc.Repo | None -> RepoLink.NoRepo)
              Display = name
              Uses =
                (match gateRunning with
                 | Some pid -> [ InUseReason.GateRunning pid ]
                 | None -> [])
                @ usesOf scan dir
              Lineage = lineage
              Retention = Retention.KeepFor DataRetention.gateCheckoutRetention } }
  let checkouts =
    childDirs scan.Loc.GateDir
    |> List.filter (fun d -> let n = Path.GetFileName d in n.StartsWith "checkout-" && not (n.EndsWith ".tiers"))
    |> List.map (make LeftoverKind.GateCheckout)
  let tiers =
    childDirs scan.Loc.GateDir
    |> List.filter (fun d -> (Path.GetFileName d).EndsWith ".tiers")
    |> List.collect childDirs
    |> List.map (make LeftoverKind.GateTierClone)
  checkouts @ tiers

let private hostCandidates (scan: Scan) : Candidate list =
  childDirs scan.Loc.HostCacheDir
  |> List.map (fun dir ->
    let target = Target.Directory dir
    { Target = target
      Kind = LeftoverKind.HostCacheEntry
      Build =
        fun () ->
          let key = Path.GetFileName dir
          let marker = Path.Combine(dir, ".last-used")
          let last = max (touched dir) (match File.Exists marker with | true -> fileTouched marker | false -> DateTime.MinValue)
          { subjectBase LeftoverKind.HostCacheEntry target with
              SizeBytes = directorySize dir
              LastTouched = last
              Display = key
              Uses =
                (scan.Live.CurrentHostKeys |> List.filter (fun k -> key = k || key.StartsWith(k + "-", StringComparison.Ordinal)) |> List.map InUseReason.CurrentSdkHost)
                @ usesOf scan dir
              Retention = Retention.KeepFor DataRetention.hostCacheMaxAge } })

let private workerLogCandidates (scan: Scan) : Candidate list =
  let dir = Path.Combine(scan.Loc.DataDir, "workers")
  childFiles dir
  |> List.filter (fun f -> f.EndsWith ".log")
  |> List.map (fun file ->
    let target = Target.File file
    { Target = target
      Kind = LeftoverKind.WorkerLogFile
      Build =
        fun () ->
          let sessionId = Path.GetFileNameWithoutExtension file
          { subjectBase LeftoverKind.WorkerLogFile target with
              SizeBytes = (try FileInfo(file).Length with _ -> 0L)
              LastTouched = fileTouched file
              Display = Path.GetFileName file
              Uses = scan.Live.Sessions |> List.filter (fun (id, _) -> id = sessionId) |> List.map (fun (id, _) -> InUseReason.LiveSession id)
              Retention = Retention.KeepFor DataRetention.workerLogMaxAge } })

/// The owner a temp run recorded for itself (`owner.pid`), as lineage.
let private tempLineage (scan: Scan) (dir: string) : Lineage =
  match OrphanTempDirSweep.readOwnerPid "owner.pid" dir with
  | None -> Lineage.NoOwnerRecorded
  | Some pid ->
    match scan.IsAlive pid None with
    | true -> Lineage.OwnerAlive pid
    | false -> Lineage.OwnerGone(pid, touched dir)

/// The runner families whose children are the runs. The family directory itself is only a parent.
let private runnerFamilies : string list = [ "sagefs-browser"; "sagefs-hr"; "sagefs-lt"; "sagefs-test" ]

let private tempCandidates (scan: Scan) : Candidate list =
  let top =
    (childDirs scan.Loc.TempDir @ childFiles scan.Loc.TempDir)
    |> List.filter (fun p -> (Path.GetFileName p).StartsWith("sagefs-", StringComparison.Ordinal))
  let runs =
    top
    |> List.collect (fun p ->
      match runnerFamilies |> List.contains (Path.GetFileName p) with
      | true -> childDirs p
      | false -> [ p ])
  let isDir (p: string) = Directory.Exists p
  runs
  |> List.map (fun path ->
    let target = if isDir path then Target.Directory path else Target.File path
    { Target = target
      Kind = LeftoverKind.TempRunDir
      Build =
        fun () ->
          { subjectBase LeftoverKind.TempRunDir target with
              SizeBytes = (if isDir path then directorySize path else (try FileInfo(path).Length with _ -> 0L))
              LastTouched = (if isDir path then touched path else fileTouched path)
              Display = Path.GetFileName path
              Uses = (if isDir path then usesOf scan path else [])
              Lineage = (if isDir path then tempLineage scan path else Lineage.NoOwnerRecorded)
              Retention = Retention.KeepFor DataRetention.tempRunMaxAge } })

let private registryEntries (scan: Scan) : (string * DaemonOwnership.DaemonInfoFile) list =
  let dir = DaemonOwnership.registryDir scan.Loc.DataDir
  childFiles dir
  |> List.filter (fun f -> f.EndsWith ".json")
  |> List.choose (fun f -> try DaemonOwnership.DaemonInfoFile.read f |> Result.toOption |> Option.map (fun i -> f, i) with _ -> None)

let private registryCandidates (scan: Scan) : Candidate list =
  let dir = DaemonOwnership.registryDir scan.Loc.DataDir
  childFiles dir
  |> List.filter (fun f -> f.EndsWith ".json")
  |> List.map (fun file ->
    let target = Target.File file
    { Target = target
      Kind = LeftoverKind.SpawnedRegistryEntry
      Build =
        fun () ->
          let lineage =
            match DaemonOwnership.DaemonInfoFile.read file with
            | Result.Error _ -> Lineage.LineageUndecidable "the entry could not be read"
            | Result.Ok info ->
              match scan.IsAlive info.Pid None with
              | true -> Lineage.OwnerAlive info.Pid
              | false -> Lineage.OwnerGone(info.Pid, fileTouched file)
          { subjectBase LeftoverKind.SpawnedRegistryEntry target with
              SizeBytes = (try FileInfo(file).Length with _ -> 0L)
              LastTouched = fileTouched file
              Display = Path.GetFileName file
              Lineage = lineage
              Retention = Retention.KeepFor DataRetention.tempRunMaxAge } })

// ─── Orphan processes ──────────────────────────────────────────────────

let private sageFsProcess (p: Proc) : bool =
  let c = p.CommandLine
  c.Contains "SageFs.Host.dll" || c.Contains "FsiHost.dll" || c.Contains "SageFs.dll" || p.Name = "sagefs"

let private processCandidates (scan: Scan) : Candidate list =
  let registry = registryEntries scan
  scan.Processes
  |> List.filter sageFsProcess
  |> List.choose (fun p ->
    let lineage =
      let workerOwner =
        match Map.tryFind "SAGEFS_DAEMON_PID" p.Environment with
        | Some text ->
          match Int32.TryParse text with
          | true, pid ->
            let ticks = Map.tryFind "SAGEFS_DAEMON_START_TICKS" p.Environment |> Option.bind (fun t -> match Int64.TryParse t with | true, v -> Some v | _ -> None)
            Some(pid, scan.IsAlive pid ticks)
          | _ -> None
        | None -> None
      let daemonOwner =
        registry
        |> List.tryPick (fun (_, info) ->
          match info.Pid = p.Pid, info.OwnerPid with
          | true, Some owner -> Some(owner, scan.IsAlive owner info.OwnerStart)
          | _ -> None)
      match workerOwner, daemonOwner, p.Cwd with
      | Some(pid, false), _, _
      | None, Some(pid, false), _ -> Some(Lineage.OwnerGone(pid, scan.Now))
      | _, _, CwdState.Deleted path -> Some(Lineage.WorkingDirectoryGone(path, scan.Now))
      | _ -> None
    match lineage with
    | None -> None
    | Some lineage ->
      let target = Target.RunningProcess(p.Pid, p.StartTicks)
      Some
        { Target = target
          Kind = LeftoverKind.OrphanProcess
          Build =
            fun () ->
              let rss = try (OwnerMonitor.getProcessById p.Pid |> Option.map (fun x -> x.WorkingSet64) |> Option.defaultValue 0L) with _ -> 0L
              { subjectBase LeftoverKind.OrphanProcess target with
                  SizeBytes = rss
                  Display = p.CommandLine
                  Lineage = lineage } })

// ─── Everything ────────────────────────────────────────────────────────

/// Every candidate leftover, cheap to list. Nothing expensive is read until `Build` runs.
let candidates (scan: Scan) : Candidate list =
  let baseRef = lazy (baseBranch scan.Git scan.Loc.Repo)
  let managedRoot = normalize (Path.Combine(scan.Loc.Repo, ".claude", "worktrees"))
  let gateRoot = normalize scan.Loc.GateDir
  match worktreeEntries scan with
  | Result.Error why ->
    // Without the worktree list there is nothing to say about worktrees, and that is said once, here.
    let target = Target.Directory(normalize scan.Loc.Repo)
    [ { Target = target
        Kind = LeftoverKind.AgentWorktree
        Build =
          fun () ->
            { subjectBase LeftoverKind.AgentWorktree target with
                Repo = RepoLink.InRepo scan.Loc.Repo
                Git = GitEvidence.GitUnreadable why } } ]
    @ gateCandidates scan [] @ hostCandidates scan @ workerLogCandidates scan @ tempCandidates scan @ registryCandidates scan @ processCandidates scan
  | Result.Ok all ->
    let others = match all with | _ :: rest -> rest | [] -> [] // the first entry is the main checkout
    let worktrees =
      others
      |> List.choose (fun e ->
        let p = normalize e.Path
        match under managedRoot p, under gateRoot p with
        | true, _ -> Some(worktreeCandidate scan (baseRef.Force()) e LeftoverKind.AgentWorktree)
        | _, true -> None // a gate checkout: `gateCandidates` owns it
        | _ -> Some(outsideCandidate scan e))
    worktrees
    @ branchCandidates scan (baseRef.Force()) all
    @ gateCandidates scan all
    @ hostCandidates scan
    @ workerLogCandidates scan
    @ tempCandidates scan
    @ registryCandidates scan
    @ processCandidates scan

/// Classify every candidate. `only` restricts the work to one target.
let gather (scan: Scan) (only: Target option) : Leftover list =
  candidates scan
  |> List.filter (fun c -> match only with | None -> true | Some t -> c.Target = t)
  |> List.map (fun c -> classify scan.Now (c.Build()))

/// A scan against the real machine.
let realScan (loc: Locations) (live: LiveFacts) : Scan =
  { Now = DateTime.UtcNow
    Loc = loc
    Git = runGit
    Processes = readProcesses ()
    Live = live
    IsAlive = fun pid ticks -> OwnerMonitor.isAlive OwnerMonitor.getProcessById { Pid = pid; StartTimeTicks = ticks } }
