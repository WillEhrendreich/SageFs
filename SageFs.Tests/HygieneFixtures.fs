/// Builders and generators for workspace hygiene tests: a subject with one thing changed from a quiet
/// default, and FsCheck generators over every kind of fact the classifier decides on.
module SageFs.Tests.HygieneFixtures

open System
open FsCheck
open FsCheck.FSharp
open SageFs.WorkspaceHygiene

let now = DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)

let repoPath = "/work/repo"
let worktreeRoot = "/work/repo/.claude/worktrees"
let gateRoot = "/state/sagefs-gate"
let hostRoot = "/data/hosts"
let dataRoot = "/data"
let tempRoot = "/tmp"

let roots : Roots =
  { Entries =
      [ RootKind.AgentWorktrees, worktreeRoot
        RootKind.GateState, gateRoot
        RootKind.HostCache, hostRoot
        RootKind.DataDir, dataRoot
        RootKind.TempRuns, tempRoot ]
    NamePrefixes = [ RootKind.TempRuns, "sagefs-" ] }

/// A resolver for paths that are real and not links: a path resolves to itself.
let identityResolve (path: string) : Result<string, string> = Result.Ok path

let aWeekAgo = now - TimeSpan.FromDays 7.0

let commit (sha: string) : Commit = { Sha = sha; Subject = "work " + sha }
let realFile (path: string) : ChangedFile = { Path = path; Origin = ChangeOrigin.Real }
let generatedFile (path: string) : ChangedFile = { Path = path; Origin = ChangeOrigin.Generated "build output" }

/// A worktree under the agent root, merged and clean, nobody using it.
let worktree (name: string) : Subject =
  { Kind = LeftoverKind.AgentWorktree
    Target = Target.Directory(worktreeRoot + "/" + name)
    SizeBytes = 1_000L
    LastTouched = aWeekAgo
    Owner = Owner.OwnerUnrecorded
    Repo = RepoLink.InRepo repoPath
    Branch = BranchLabel.OnBranch("worktree-" + name)
    Display = name
    Uses = []
    Lineage = Lineage.NoOwnerRecorded
    Retention = Retention.NeverExpires
    Git = GitEvidence.Worktree(MergeEvidence.MergedInto MergeHow.Ancestor, []) }

let withGit (git: GitEvidence) (s: Subject) : Subject = { s with Git = git }
let withUse (reason: InUseReason) (s: Subject) : Subject = { s with Uses = reason :: s.Uses }

let mergedWith (how: MergeHow) (files: ChangedFile list) (s: Subject) : Subject =
  { s with Git = GitEvidence.Worktree(MergeEvidence.MergedInto how, files) }

let unmergedWith (files: ChangedFile list) (s: Subject) : Subject =
  { s with
      Git =
        GitEvidence.Worktree(
          MergeEvidence.UnmergedCommits(NonEmpty.ofHeadTail (commit "aaa1111") [ commit "bbb2222" ]),
          files) }

/// A cache-like directory with an owner or an age and nothing else.
let cache (kind: LeftoverKind) (path: string) (retention: TimeSpan) : Subject =
  { Kind = kind
    Target = Target.Directory path
    SizeBytes = 5_000L
    LastTouched = aWeekAgo
    Owner = Owner.OwnerUnrecorded
    Repo = RepoLink.NoRepo
    Branch = BranchLabel.NoBranch
    Display = path
    Uses = []
    Lineage = Lineage.NoOwnerRecorded
    Retention = Retention.KeepFor retention
    Git = GitEvidence.NoGitEvidence }

let branch (name: string) (merge: MergeEvidence) : Subject =
  { Kind = LeftoverKind.StaleBranch
    Target = Target.GitBranch(repoPath, name)
    SizeBytes = 0L
    LastTouched = aWeekAgo
    Owner = Owner.OwnerUnrecorded
    Repo = RepoLink.InRepo repoPath
    Branch = BranchLabel.OnBranch name
    Display = name
    Uses = []
    Lineage = Lineage.NoOwnerRecorded
    Retention = Retention.NeverExpires
    Git = GitEvidence.Branch merge }

let orphanProcess (pid: int) : Subject =
  { Kind = LeftoverKind.OrphanProcess
    Target = Target.RunningProcess(pid, 4242L)
    SizeBytes = 300_000_000L
    LastTouched = aWeekAgo
    Owner = Owner.OwnedByProcess 1
    Repo = RepoLink.NoRepo
    Branch = BranchLabel.NoBranch
    Display = "SageFs.Host --worker"
    Uses = []
    Lineage = Lineage.OwnerGone(1, aWeekAgo)
    Retention = Retention.NeverExpires
    Git = GitEvidence.NoGitEvidence }

// ─── Generators ────────────────────────────────────────────────────────

let private genPath = Gen.elements [ "a.fs"; "src/b.fs"; "bin/x.dll"; "obj/y.json"; "packages.lock.json"; "docs/z.md" ]

let genChangedFile : Gen<ChangedFile> =
  gen {
    let! path = genPath
    let! generated = Gen.elements [ true; false ]
    return (if generated then generatedFile path else realFile path)
  }

let genMerge : Gen<MergeEvidence> =
  Gen.oneof [
    Gen.elements [ MergeHow.Ancestor; MergeHow.PatchEquivalent; MergeHow.TreeEqual ] |> Gen.map MergeEvidence.MergedInto
    Gen.constant (MergeEvidence.UnmergedCommits(NonEmpty.ofHeadTail (commit "c0ffee1") []))
    Gen.constant (MergeEvidence.MergeNotDecidable(UnknownReason.MergeUndecidable "no base"))
  ]

let genGit : Gen<GitEvidence> =
  Gen.oneof [
    Gen.constant GitEvidence.NoGitEvidence
    genMerge |> Gen.map GitEvidence.Branch
    gen {
      let! merge = genMerge
      let! files = Gen.listOf genChangedFile
      return GitEvidence.Worktree(merge, files)
    }
    Gen.constant (GitEvidence.GitUnreadable(UnknownReason.GitUnavailable "no git"))
  ]

let genUse : Gen<InUseReason> =
  Gen.oneof [
    Gen.constant (InUseReason.LiveSession "s1")
    Gen.constant (InUseReason.LeaseHeld "agent")
    Gen.constant (InUseReason.ProcessWorkingDirectory(7, "SageFs"))
    Gen.constant (InUseReason.GateRunning 9)
    Gen.constant (InUseReason.OwnerAlive 3)
  ]

let genLineage : Gen<Lineage> =
  Gen.oneof [
    Gen.constant (Lineage.OwnerGone(5, aWeekAgo))
    Gen.constant (Lineage.WorkingDirectoryGone("/gone", aWeekAgo))
    Gen.constant (Lineage.OwnerAlive 6)
    Gen.constant Lineage.NoOwnerRecorded
    Gen.constant (Lineage.LineageUndecidable "no /proc")
  ]

let genKind : Gen<LeftoverKind> =
  Gen.elements
    [ LeftoverKind.AgentWorktree; LeftoverKind.GateCheckout; LeftoverKind.GateTierClone
      LeftoverKind.HostCacheEntry; LeftoverKind.WorkerLogFile; LeftoverKind.TempRunDir
      LeftoverKind.OrphanProcess; LeftoverKind.StaleBranch; LeftoverKind.SpawnedRegistryEntry ]

let genSubject : Gen<Subject> =
  gen {
    let! kind = genKind
    let! id = Gen.choose (1, 40)
    let! uses = Gen.listOfLength 1 genUse |> Gen.bind (fun one -> Gen.elements [ []; one ])
    let! lineage = genLineage
    let! git = genGit
    let! ageDays = Gen.choose (0, 60)
    let! retentionDays = Gen.choose (1, 30)
    let! retentionKind = Gen.elements [ true; false ]
    let target =
      match kind with
      | LeftoverKind.OrphanProcess -> Target.RunningProcess(id, int64 id * 10L)
      | LeftoverKind.StaleBranch -> Target.GitBranch(repoPath, sprintf "worktree-agent-%d" id)
      | LeftoverKind.WorkerLogFile
      | LeftoverKind.SpawnedRegistryEntry -> Target.File(sprintf "%s/f%d.log" dataRoot id)
      | LeftoverKind.AgentWorktree -> Target.Directory(sprintf "%s/agent-%d" worktreeRoot id)
      | LeftoverKind.GateCheckout
      | LeftoverKind.GateTierClone -> Target.Directory(sprintf "%s/checkout-%d" gateRoot id)
      | LeftoverKind.HostCacheEntry -> Target.Directory(sprintf "%s/sdk-%d" hostRoot id)
      | LeftoverKind.TempRunDir -> Target.Directory(sprintf "%s/sagefs-hr/%d" tempRoot id)
    return
      { Kind = kind
        Target = target
        SizeBytes = int64 id * 1_000L
        LastTouched = now - TimeSpan.FromDays(float ageDays)
        Owner = Owner.OwnerUnrecorded
        Repo = RepoLink.InRepo repoPath
        Branch = BranchLabel.OnBranch(sprintf "worktree-agent-%d" id)
        Display = sprintf "item-%d" id
        Uses = uses
        Lineage = lineage
        Retention = (if retentionKind then Retention.KeepFor(TimeSpan.FromDays(float retentionDays)) else Retention.NeverExpires)
        Git = git }
  }

let arbSubject : Arbitrary<Subject> = Arb.fromGen genSubject
