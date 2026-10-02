/// The local gate's leftovers and the one place that decides them. `scripts/local-gate.fsx` keeps one checkout per
/// invoking repo path, per-tier clones beside it, a record for every commit that passed (a release bundle each),
/// and logs, and for a long time nothing ever removed any of it: 172 GB on one machine. This module reads that
/// directory into `Subject`s for the same pure `classify` and `Planner` everything else uses, and runs the gate's
/// own reap (`scripts/gate-reap.fsx`) through the same `Executor`, so the gate and the daemon cannot disagree about
/// what is safe to remove.
///
/// It depends on nothing but System and the pure hygiene domain, so an `.fsx` can `#load` it.
module SageFs.GateReaper

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open SageFs.WorkspaceHygiene

/// The names the gate script writes. One home, because the script and this module must agree.
module Names =
  let checkoutPrefix = "checkout-"
  let tiersSuffix = ".tiers"
  let passedDir = "passed"
  let ownersDir = "owners"
  let currentFile = "current"
  let logsDir = "logs"

/// The gate keys a checkout by the first 8 hex digits of the sha1 of the invoking repo's path:
/// `echo -n "$REPO" | sha1sum | cut -c1-8`.
let repoKey (repo: string) : string =
  use sha = SHA1.Create()
  Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes repo)).ToLowerInvariant().Substring(0, 8)

/// What the gate needs from the disk, so a test can answer for it.
type GateFs =
  { ChildDirs: string -> string list
    ChildFiles: string -> string list
    Exists: string -> bool
    LastWrite: string -> DateTime
    Size: string -> int64
    FirstLine: string -> string option
    IsAlive: int -> bool }

let private safeList (list: string -> string seq) (dir: string) : string list =
  try list dir |> Seq.map (fun p -> Guard.normalize p) |> Seq.toList with _ -> []

let realFs (isAlive: int -> bool) : GateFs =
  { ChildDirs = safeList Directory.EnumerateDirectories
    ChildFiles = safeList Directory.EnumerateFiles
    Exists = fun p -> Directory.Exists p || File.Exists p
    LastWrite = fun p -> try (match Directory.Exists p with | true -> Directory.GetLastWriteTimeUtc p | false -> File.GetLastWriteTimeUtc p) with _ -> DateTime.UtcNow
    Size = fun p -> match Directory.Exists p with | true -> HygieneFs.directorySize p | false -> (try FileInfo(p).Length with _ -> 0L)
    FirstLine = fun p -> try File.ReadLines p |> Seq.tryHead |> Option.map (fun l -> l.Trim()) with _ -> None
    IsAlive = isAlive }

/// A thing in the gate dir: a checkout, a clone beside it, or a pass record (with its place among the newest).
[<RequireQualifiedAccess>]
type GateEntry =
  | Checkout of dir: string
  | TierClone of dir: string
  | PassRecord of dir: string * rank: int

let entryDir (entry: GateEntry) : string =
  match entry with
  | GateEntry.Checkout d
  | GateEntry.TierClone d
  | GateEntry.PassRecord(d, _) -> d

let entryKind (entry: GateEntry) : LeftoverKind =
  match entry with
  | GateEntry.Checkout _ -> LeftoverKind.GateCheckout
  | GateEntry.TierClone _ -> LeftoverKind.GateTierClone
  | GateEntry.PassRecord _ -> LeftoverKind.GatePassRecord

/// Every checkout, tier clone and pass record in the gate dir. Cheap: names only; nothing is measured yet.
let entries (fs: GateFs) (gateDir: string) : GateEntry list =
  let top = fs.ChildDirs gateDir
  let name (d: string) = Path.GetFileName d
  let checkouts = top |> List.filter (fun d -> (name d).StartsWith Names.checkoutPrefix && not ((name d).EndsWith Names.tiersSuffix))
  let tiers = top |> List.filter (fun d -> (name d).StartsWith Names.checkoutPrefix && (name d).EndsWith Names.tiersSuffix) |> List.collect fs.ChildDirs
  let passes =
    fs.ChildDirs (gateDir + "/" + Names.passedDir)
    |> List.sortByDescending fs.LastWrite
    |> List.indexed
    |> List.map (fun (rank, d) -> GateEntry.PassRecord(d, rank))
  (checkouts |> List.map GateEntry.Checkout) @ (tiers |> List.map GateEntry.TierClone) @ passes

/// The gate run that is doing the reaping: the repo it is for, whose checkout it is about to use, and its own pid, which
/// is already in the gate's `current` file by then and must not make everything look busy.
type Invoker = { Repo: string; Pid: int }

/// The pid of a gate run in progress, from the gate's `current` file ("pid sha"), when that process is alive and is not
/// `ignoring` (the caller's own).
let runningGate (fs: GateFs) (gateDir: string) (ignoring: int option) : int option =
  match fs.FirstLine(gateDir + "/" + Names.currentFile) with
  | None -> None
  | Some line ->
    match line.Split(' ') with
    | [| pid; _ |] ->
      match Int32.TryParse pid with
      | true, p when fs.IsAlive p && ignoring <> Some p -> Some p
      | _ -> None
    | _ -> None

/// The repo a checkout was made for: the gate records it (`owners/<checkout>`, first line), else the repo whose
/// path hashes to the checkout's name when it is one of `knownRepos`.
let ownerRepo (fs: GateFs) (gateDir: string) (knownRepos: string list) (checkoutName: string) : string option =
  let recorded = fs.FirstLine(gateDir + "/" + Names.ownersDir + "/" + checkoutName)
  match recorded with
  | Some repo -> Some repo
  | None ->
    match checkoutName.StartsWith Names.checkoutPrefix && checkoutName.Length >= Names.checkoutPrefix.Length + 8 with
    | false -> None
    | true ->
      let key = checkoutName.Substring(Names.checkoutPrefix.Length, 8)
      knownRepos |> List.tryFind (fun repo -> repoKey repo = key)

/// The facts about one gate entry. `invokingRepo` is the repo the running gate is for: its checkout is about to be
/// used, so it is never a leftover.
let describe
  (fs: GateFs)
  (now: DateTime)
  (gateDir: string)
  (knownRepos: string list)
  (invoker: Invoker option)
  (entry: GateEntry)
  : Subject =
  let dir = entryDir entry
  let name = Path.GetFileName dir
  let ownerName = (match entry with | GateEntry.TierClone _ -> Path.GetFileName(Path.GetDirectoryName dir) | _ -> name).Replace(Names.tiersSuffix, "")
  let running = runningGate fs gateDir (invoker |> Option.map (fun i -> i.Pid))
  let owner = ownerRepo fs gateDir knownRepos ownerName
  let invoking =
    match invoker, entry with
    | Some i, (GateEntry.Checkout _ | GateEntry.TierClone _) when ownerName = Names.checkoutPrefix + repoKey i.Repo -> [ InUseReason.InvokingGate i.Repo ]
    | _ -> []
  let lineage =
    match owner with
    | Some repo when not (fs.Exists repo) -> Lineage.WorkingDirectoryGone(repo, fs.LastWrite dir)
    | _ -> Lineage.NoOwnerRecorded
  // A checkout is a worktree of its owner repo only while that repo still exists and the checkout still has its marker.
  let registered =
    match owner, entry with
    | Some repo, GateEntry.Checkout d when fs.Exists repo && fs.Exists(d + "/.git") -> RepoLink.InRepo repo
    | _ -> RepoLink.NoRepo
  let uses = (match running with | Some pid -> [ InUseReason.GateRunning pid ] | None -> []) @ invoking
  { Kind = entryKind entry
    Target = Target.Directory dir
    SizeBytes = fs.Size dir
    LastTouched = fs.LastWrite dir
    Owner = Owner.OwnerUnrecorded
    Repo = registered
    Branch = BranchLabel.NoBranch
    Display = name
    Uses = uses
    Lineage = lineage
    Retention =
      (match entry with
       | GateEntry.PassRecord(_, rank) -> Retention.KeepNewest(rank, DataRetention.gatePassRecordsKept)
       | _ -> Retention.KeepFor DataRetention.gateCheckoutRetention)
    Git = GitEvidence.NoGitEvidence }

/// Which log files to delete: all but the newest `keep`, newest first by write time. Pure.
let staleLogs (lastWrite: string -> DateTime) (keep: int) (logs: string list) : string list =
  logs |> List.sortByDescending lastWrite |> List.skip (min keep (List.length logs))

// ─── Running the reap ──────────────────────────────────────────────────

let private runGit (repo: string) (args: string list) : Outcome =
  try
    let psi = ProcessStartInfo("git", WorkingDirectory = repo, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false)
    for a in args do psi.ArgumentList.Add a
    use p = Process.Start psi
    let err = p.StandardError.ReadToEndAsync()
    p.StandardOutput.ReadToEnd() |> ignore
    p.WaitForExit()
    match p.ExitCode with
    | 0 -> Outcome.Done 0L
    | _ -> Outcome.Failed(sprintf "git %s: %s" (String.Join(" ", args)) (err.Result.Trim()))
  with ex -> Outcome.Failed ex.Message

/// The effects the gate's reap runs through the shared executor: only the file operations a gate leftover needs.
let effects (fs: GateFs) (now: DateTime) (gateDir: string) (knownRepos: string list) (invoker: Invoker option) : Effects =
  let entryFor (target: Target) =
    match target with
    | Target.Directory dir -> entries fs gateDir |> List.tryFind (fun e -> entryDir e = dir)
    | _ -> None
  { Recheck =
      fun target ->
        match entryFor target with
        | None -> Rechecked.Gone
        | Some entry -> Rechecked.Fresh(classify now (describe fs now gateDir knownRepos invoker entry))
    Resolve = HygieneFs.resolvePath
    Perform =
      fun op ->
        try
          match op with
          | Operation.RemoveWorktree(accepted, repo, _) ->
            let path = Guard.path accepted
            let size = fs.Size path
            match runGit repo [ "worktree"; "remove"; "--force"; path ] with
            | Outcome.Done _ -> Outcome.Done size
            | failed -> failed
          | Operation.RemoveTree accepted ->
            let path = Guard.path accepted
            let size = fs.Size path
            Directory.Delete(path, true)
            Outcome.Done size
          | Operation.RemoveFile accepted ->
            let path = Guard.path accepted
            let size = fs.Size path
            File.Delete path
            Outcome.Done size
          | Operation.DeleteBranch _
          | Operation.Terminate _ -> Outcome.Failed "a gate reap only removes files"
        with ex -> Outcome.Failed ex.Message }

/// The roots a gate reap may remove under: checkouts, their tier clones and the pass records, nothing else in the dir.
let roots (gateDir: string) : Roots =
  { Entries = [ RootKind.GateState, gateDir ]
    NamePrefixes = [ RootKind.GateState, Names.checkoutPrefix; RootKind.GateState, Names.passedDir ] }

/// What a reap did.
type ReapResult =
  { Report: Report
    LogsDeleted: int }

/// Reap the gate dir: plan every checkout, clone and pass record, run the Safe steps through the shared executor
/// (each looked at again first), and trim the logs. The invoking repo's own checkout is never touched.
let reap
  (fs: GateFs)
  (now: DateTime)
  (gateDir: string)
  (knownRepos: string list)
  (invoker: Invoker option)
  (deleteFile: string -> unit)
  : ReapResult =
  let leftovers = entries fs gateDir |> List.map (fun e -> classify now (describe fs now gateDir knownRepos invoker e))
  let plan = Planner.plan leftovers
  let report =
    match Confirmation.safeOnly plan plan.Id with
    | Result.Error _ -> { Executed = []; ReclaimedBytes = 0L }
    | Result.Ok confirmation -> Executor.run (effects fs now gateDir knownRepos invoker) (roots gateDir) confirmation plan
  let stale = staleLogs fs.LastWrite DataRetention.gateLogsKept (fs.ChildFiles(gateDir + "/" + Names.logsDir))
  for log in stale do
    try deleteFile log with _ -> ()
  { Report = report; LogsDeleted = List.length stale }
