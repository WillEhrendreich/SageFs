/// Two things hygiene lists and never removes by itself, both found on 2026-11-07: a `*.tiers` directory beside a repository
/// that nothing uses (one was 138 GB), and a checkout that a cohort holds (the conductor makes and removes those). Real git
/// and real directories, in throwaway sandboxes.
module SageFs.Tests.HygieneOwnershipTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.Tests.HygieneSandbox

let private tiersDir (sb: Sandbox) (name: string) : string =
  let dir = Path.Combine(sb.Root, name)
  write (Path.Combine(dir, "clone-1", "big.bin")) "x"
  dir

let private standingOfDir (leftovers: Leftover list) (dir: string) : Standing =
  standingOfPath leftovers (Guard.normalize dir)

[<Tests>]
let tiersTests =
  testList "Workspace hygiene: tier directories beside a repository" [

    testTask "an unused <repo>.tiers directory is listed as needing a look, with the reason, and a tidy leaves it" {
      use sb = new Sandbox()
      let dir = tiersDir sb "repo.tiers"
      let! leftovers = gather (sb.Scan()) None
      match standingOfDir leftovers dir with
      | Standing.Unknown(UnknownReason.NoCurrentOwner detail) -> detail |> Expect.stringContains "says what it is" "tier work"
      | other -> failtestf "expected Unknown NoCurrentOwner, got %A" other
      let plan = Planner.plan leftovers
      let step = plan.Steps |> List.find (fun s -> Leftover.target s.Target = Target.Directory(Guard.normalize dir))
      step.Risk |> Expect.equal "a look, not a removal" Risk.Unverifiable
      step.Reason |> Expect.stringContains "tells a person what to do" "look before removing"
      let! report = Executor.runAsync (sb.Effects(fun _ _ -> false)) sb.Roots (Confirmation.safeOnly plan plan.Id |> Result.defaultWith (fun _ -> failtest "plan")) plan
      report.ReclaimedBytes |> Expect.equal "nothing reclaimed" 0L
      Directory.Exists dir |> Expect.isTrue "the directory is still there"
    }

    testTask "a leftover gate tier directory with another name beside the repository is listed too" {
      use sb = new Sandbox()
      let dir = tiersDir sb "checkout-abc.tiers"
      let! leftovers = gather (sb.Scan()) None
      match standingOfDir leftovers dir with
      | Standing.Unknown(UnknownReason.NoCurrentOwner _) -> ()
      | other -> failtestf "expected Unknown NoCurrentOwner, got %A" other
    }

    testTask "a pipeline running from the repository makes its tier work in use" {
      use sb = new Sandbox()
      let dir = tiersDir sb "repo.tiers"
      let pipeline : Proc =
        { Pid = 4100; StartTicks = 9L; Name = "dotnet"; CommandLine = "dotnet fsi ci-pipeline.fsx"; Cwd = CwdState.At sb.Repo
          ParentPid = 1; Environment = Map.empty }
      let! leftovers = gather (sb.ScanWith((fun _ _ -> false), [ pipeline ], LiveFacts.none)) None
      match standingOfDir leftovers dir with
      | Standing.InUse(InUseReason.GateRunning 4100, _) -> ()
      | other -> failtestf "expected InUse GateRunning, got %A" other
    }

    testTask "a process whose working directory is inside the tier directory makes it in use" {
      use sb = new Sandbox()
      let dir = tiersDir sb "repo.tiers"
      let inside : Proc =
        { Pid = 4200; StartTicks = 9L; Name = "dotnet"; CommandLine = "dotnet test"; Cwd = CwdState.At(Path.Combine(dir, "clone-1"))
          ParentPid = 1; Environment = Map.empty }
      let! leftovers = gather (sb.ScanWith((fun _ _ -> false), [ inside ], LiveFacts.none)) None
      match standingOfDir leftovers dir with
      | Standing.InUse(InUseReason.ProcessWorkingDirectory(4200, _), _) -> ()
      | other -> failtestf "expected InUse ProcessWorkingDirectory, got %A" other
    }

    testTask "a file named *.tiers, and a directory that does not end in .tiers, are not listed" {
      use sb = new Sandbox()
      write (Path.Combine(sb.Root, "notes.tiers")) "a file"
      Directory.CreateDirectory(Path.Combine(sb.Root, "other")) |> ignore
      let! leftovers = gather (sb.Scan()) None
      leftovers
      |> List.filter (fun l -> Leftover.kind l = LeftoverKind.GateTierClone)
      |> List.length
      |> Expect.equal "nothing to list" 0
    }
  ]

[<Tests>]
let cohortTests =
  testList "Workspace hygiene: checkouts a cohort holds" [

    testTask "a worktree outside the managed root reads as held by the cohort once the cohort knows it" {
      use sb = new Sandbox()
      let path = Path.Combine(sb.Root, "cohort", "p7-hygiene")
      Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
      git sb.Repo [ "worktree"; "add"; "-b"; "cohort/p7-hygiene"; path; "master" ] |> ignore
      let path = canonicalPath path
      let! before = gather (sb.Scan()) None
      match standingOfPath before path with
      | Standing.Unknown _ -> ()
      | other -> failtestf "before the cohort knows it the worktree is only unknown, got %A" other
      let live = { LiveFacts.none with CohortHeld = [ path, "member p7-hygiene" ] }
      let! after = gather (sb.ScanWith((fun _ _ -> false), [], live)) None
      match standingOfPath after path with
      | Standing.InUse(InUseReason.HeldByCohort holder, _) -> holder |> Expect.stringContains "names who" "p7-hygiene"
      | other -> failtestf "expected InUse HeldByCohort, got %A" other
    }

    testTask "a merged worktree under the managed root that a cohort holds is not reclaimable, and a tidy keeps it" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "held-by-cohort"
      let live = { LiveFacts.none with CohortHeld = [ path, "integration checkout" ] }
      sb.Live <- live
      let! leftovers = gather (sb.Scan()) None
      match standingOfPath leftovers path with
      | Standing.InUse(InUseReason.HeldByCohort _, _) -> ()
      | other -> failtestf "expected InUse HeldByCohort, got %A" other
      let plan = Planner.plan leftovers
      let! report = Executor.runAsync (sb.Effects(fun _ _ -> false)) sb.Roots (Confirmation.safeOnly plan plan.Id |> Result.defaultWith (fun _ -> failtest "plan")) plan
      report.ReclaimedBytes |> Expect.equal "nothing reclaimed" 0L
      Directory.Exists path |> Expect.isTrue "the checkout is still there"
    }

    testCase "the reason is said in words a person can act on" <| fun _ ->
      InUseReason.describe (InUseReason.HeldByCohort "member p7-hygiene")
      |> Expect.stringContains "says the conductor manages it" "conductor"
  ]

[<Tests>]
let worktreeNoteTests =
  testList "Workspace hygiene: a session in an agent worktree is told what happens to it" [

    testCase "a worktree under the agent area gets the note, naming its salvage branch and the rescue command" <| fun _ ->
      let note = SageFs.McpHygiene.agentWorktreeNote (DirPath "/work/repo") (DirPath "/work/repo/.claude/worktrees/agent-1/src") |> Option.defaultValue ""
      note |> Expect.stringContains "says it is an agent worktree" "agent worktree (agent-1)"
      note |> Expect.stringContains "names the salvage branch" "salvage/agent-1"
      note |> Expect.stringContains "names the way to keep the work" "sagefs hygiene --rescue agent-1"

    testCase "the main checkout and the agent area itself get nothing" <| fun _ ->
      SageFs.McpHygiene.agentWorktreeNote (DirPath "/work/repo") (DirPath "/work/repo/src") |> Expect.isNone "the main checkout"
      SageFs.McpHygiene.agentWorktreeNote (DirPath "/work/repo") (DirPath "/work/repo/.claude/worktrees") |> Expect.isNone "the area itself"
  ]

[<Tests>]
let outsideTests =
  testList "Workspace hygiene: worktrees outside the managed directory" [

    testTask "work that exists nowhere else is named wherever it sits, a clean merged one stays unknown, and none of them is removed or salvaged" {
      use sb = new Sandbox()
      let outside (name: string) : string =
        let path = Path.Combine(sb.Root, "elsewhere", name)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        git sb.Repo [ "worktree"; "add"; "-b"; "elsewhere/" + name; path; "master" ] |> ignore
        canonicalPath path
      let unmerged = outside "unmerged"
      write (Path.Combine(unmerged, "src", "u.fs")) "module U"
      commitAll unmerged "work that exists only here"
      let dirty = outside "dirty"
      write (Path.Combine(dirty, "src", "d.fs")) "module D"
      let clean = outside "clean"
      let! leftovers = gather (sb.Scan()) None
      let unmergedOk = (match standingOfPath leftovers unmerged with | Standing.CleanButUnmerged _ -> true | _ -> false)
      unmergedOk |> Expect.isTrue "the one with a commit the base lacks is CleanButUnmerged"
      let dirtyOk = (match standingOfPath leftovers dirty with | Standing.DirtyReal _ -> true | _ -> false)
      dirtyOk |> Expect.isTrue "the one with an uncommitted file is DirtyReal"
      let cleanOk = (match standingOfPath leftovers clean with | Standing.Unknown(UnknownReason.PathOutsideKnownRoots _) -> true | _ -> false)
      cleanOk |> Expect.isTrue "the clean merged one stays unknown"
      let summary = SageFs.WorkspaceHygieneRender.summarize leftovers (Planner.plan leftovers)
      let held = (match summary.Held with | SageFs.WorkspaceHygieneRender.HeldWork.WorkHeld(count, _, _) -> count | SageFs.WorkspaceHygieneRender.HeldWork.NothingHeld -> 0)
      held |> Expect.equal "the two that hold work are counted" 2
      // A tidy, and two sweeps, change none of them.
      let plan = Planner.plan leftovers
      let! _ = Executor.runAsync (sb.Effects(fun _ _ -> false)) sb.Roots (Confirmation.safeOnly plan plan.Id |> Result.defaultWith (fun _ -> failtest "plan")) plan
      let context : SageFs.HygieneSweep.SweepContext =
        { MakeScan = (fun () -> { sb.Scan() with Loc = { sb.Locations with WorktreeGrace = TimeSpan.Zero } })
          Git = runGit
          GitEnv = runGitEnv
          IsAlive = (fun _ _ -> false)
          AutoTidy = (fun () -> On) }
      let! _ = SageFs.HygieneSweep.sweep context
      let! (second: SageFs.HygieneSweep.SweepReport) = SageFs.HygieneSweep.sweep context
      second.Salvage |> Expect.isEmpty "no salvage branch is written for a worktree the daemon does not manage"
      for path in [ unmerged; dirty; clean ] do
        Directory.Exists path |> Expect.isTrue (sprintf "%s is still there" path)
      File.ReadAllText(Path.Combine(dirty, "src", "d.fs")) |> Expect.equal "its uncommitted bytes are intact" "module D"
      (match gitExit sb.Repo [ "rev-parse"; "--verify"; "--quiet"; "refs/sagefs/salvage/dirty" ] with | 0, _, _ -> true | _ -> false)
      |> Expect.isFalse "and nothing was committed on its behalf"
    }
  ]

[<Tests>]
let sessionsReplyTests =
  testList "Workspace hygiene: what a shell asks the daemon" [

    testCase "the sessions reply gives the sessions and the checkouts a cohort holds, and an older daemon holds none" <| fun _ ->
      let withHeld =
        """{"sessions":[{"id":"s1","workingDirectory":"/w/a"}],"cohortHeld":[{"path":"/w/c","holder":"member token abc"}]}"""
      HygieneService.parseSessionsReply withHeld
      |> Expect.equal "both lists" (HygieneService.DaemonFacts.Answered([ "s1", "/w/a" ], [ "/w/c", "member token abc" ]))
      HygieneService.parseSessionsReply """{"sessions":[{"id":"s1","workingDirectory":"/w/a"}]}"""
      |> Expect.equal "no cohortHeld reads as none" (HygieneService.DaemonFacts.Answered([ "s1", "/w/a" ], []))

    testCase "a reply that is not that shape is a daemon that did not answer, never a daemon that holds nothing" <| fun _ ->
      for text in [ ""; "not json"; "{}"; """{"sessions":3}""" ] do
        match HygieneService.parseSessionsReply text with
        | HygieneService.DaemonFacts.DidNotAnswer why -> String.IsNullOrWhiteSpace why |> Expect.isFalse (sprintf "%A says why" text)
        | other -> failtestf "%A should be DidNotAnswer, got %A" text other
  ]
