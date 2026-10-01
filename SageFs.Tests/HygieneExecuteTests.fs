/// Tidy on real repositories and real directories: the proof that what the executor removes is exactly what
/// the standing says is safe, and that everything else is still there afterwards.
module SageFs.Tests.HygieneExecuteTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.Tests.HygieneSandbox

let private confirm (plan: Plan) : Confirmation =
  match Confirmation.safeOnly plan plan.Id with
  | Result.Ok c -> c
  | Result.Error e -> failtestf "could not confirm: %A" e

let private tidy (sb: Sandbox) (scan: Scan) (alive: int -> int64 option -> bool) : Report =
  let plan = Planner.plan (gather scan None)
  Executor.run (sb.Effects alive) sb.Roots (confirm plan) plan

let private noneAlive : int -> int64 option -> bool = fun _ _ -> false

[<Tests>]
let executeTests =
  testList "Workspace hygiene: tidy on a real repository" [

    testCase "tidy removes what is merged or only build output, with its merged branch, and keeps everything else" <| fun _ ->
      use sb = new Sandbox()
      let paths = populate sb
      let report = tidy sb (sb.Scan()) noneAlive
      for name in [ "clean-merged"; "ff-merged"; "squashed"; "dirty-generated" ] do
        Directory.Exists paths.[name] |> Expect.isFalse (sprintf "%s is gone" name)
        branchExists sb ("worktree-agent-" + name) |> Expect.isFalse (sprintf "the merged branch of %s is gone" name)
      for name in [ "unmerged"; "dirty-real" ] do
        Directory.Exists paths.[name] |> Expect.isTrue (sprintf "%s is still there" name)
        branchExists sb ("worktree-agent-" + name) |> Expect.isTrue (sprintf "the branch of %s is still there" name)
      File.Exists(Path.Combine(paths.["dirty-real"], "src", "precious.fs")) |> Expect.isTrue "the uncommitted file is untouched"
      (git paths.["unmerged"] [ "log"; "-1"; "--format=%s" ]).Trim() |> Expect.equal "the unmerged commit is still reachable" "unmerged work"
      (report.ReclaimedBytes > 0L) |> Expect.isTrue "it reported what it gave back"

    testCase "running the same plan again changes nothing" <| fun _ ->
      use sb = new Sandbox()
      populate sb |> ignore
      let plan = Planner.plan (gather (sb.Scan()) None)
      let confirmation = confirm plan
      let fx = sb.Effects noneAlive
      Executor.run fx sb.Roots confirmation plan |> ignore
      let second = Executor.run fx sb.Roots confirmation plan
      second.ReclaimedBytes |> Expect.equal "nothing more reclaimed" 0L
      second.Executed
      |> List.forall (fun e -> match e.Result with | StepResult.Ran _ -> false | _ -> true)
      |> Expect.isTrue "no step ran again"

    testCase "a worktree that gets real work after the plan was made is skipped, and the work is still there" <| fun _ ->
      use sb = new Sandbox()
      let path = sb.AddWorktree "late"
      let plan = Planner.plan (gather (sb.Scan()) None)
      write (Path.Combine(path, "src", "late-work.fs")) "module Late"
      let report = Executor.run (sb.Effects noneAlive) sb.Roots (confirm plan) plan
      Directory.Exists path |> Expect.isTrue "the worktree is still there"
      File.Exists(Path.Combine(path, "src", "late-work.fs")) |> Expect.isTrue "the new file is still there"
      report.Executed
      |> List.exists (fun e ->
        match e.Result with
        | StepResult.Skipped(SkipReason.StandingChanged(Standing.DirtyReal _)) -> true
        | _ -> false)
      |> Expect.isTrue "the step was skipped because its standing changed"

    testCase "a worktree outside every managed directory is listed and never removed" <| fun _ ->
      use sb = new Sandbox()
      let outside = Path.Combine(sb.Root, "elsewhere", "wt")
      Directory.CreateDirectory(Path.GetDirectoryName outside) |> ignore
      git sb.Repo [ "worktree"; "add"; "-b"; "worktree-agent-outside"; outside; "master" ] |> ignore
      let path = Path.GetFullPath outside
      match standingOfPath (gather (sb.Scan()) None) path with
      | Standing.Unknown(UnknownReason.PathOutsideKnownRoots _) -> ()
      | other -> failtestf "expected Unknown outside the roots, got %A" other
      tidy sb (sb.Scan()) noneAlive |> ignore
      Directory.Exists path |> Expect.isTrue "still there"

    testCase "a sagefs temp entry that is a link out of the temp dir is refused, and what it points at survives" <| fun _ ->
      use sb = new Sandbox()
      let outsideDir = Path.Combine(sb.Root, "precious")
      write (Path.Combine(outsideDir, "keep.txt")) "keep"
      Directory.CreateDirectory sb.Locations.TempDir |> ignore
      let link = Path.Combine(sb.Locations.TempDir, "sagefs-evil")
      Directory.CreateSymbolicLink(link, outsideDir) |> ignore
      sb.Skew <- HygieneAges.clockSkewPastRetention
      let plan = Planner.plan (gather (sb.Scan()) None)
      let report = Executor.run (sb.Effects noneAlive) sb.Roots (confirm plan) plan
      File.Exists(Path.Combine(outsideDir, "keep.txt")) |> Expect.isTrue "the target of the link is untouched"
      report.Executed
      |> List.exists (fun e ->
        match e.Result with
        | StepResult.Skipped(SkipReason.Refused(Refusal.SymlinkEscapes _)) -> true
        | _ -> false)
      |> Expect.isTrue "the gate refused it as a link that leaves its root"

    testCase "host cache: an unused old host goes, a recent one stays, and the host the daemon resolves is never pruned" <| fun _ ->
      use sb = new Sandbox()
      let host name (daysOld: float) =
        let dir = Path.Combine(sb.Locations.HostCacheDir, name)
        write (Path.Combine(dir, "bin", "FsiHost.dll")) "x"
        Directory.SetLastWriteTimeUtc(Path.Combine(dir, "bin"), DateTime.UtcNow - TimeSpan.FromDays daysOld)
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - TimeSpan.FromDays daysOld)
        dir
      let stale = host "sdk-10.0.100-aaaa" 90.0
      let recent = host "sdk-10.0.200-bbbb" 2.0
      let current = host "sdk-11.0.100-cccc" 90.0
      let live = { LiveFacts.none with CurrentSdkVersions = [ "11.0.100" ] }
      let running = host "sdk-9.0.100-eeee" 90.0
      let runner : Proc =
        { Pid = 31; StartTicks = 1L; Name = "dotnet"; CommandLine = sprintf "dotnet %s/bin/FsiHost.dll --args-file x" running; Cwd = CwdState.Unreadable; ParentPid = 1; Environment = Map.empty }
      sb.Procs <- [ runner ]
      sb.Live <- live
      tidy sb (sb.Scan()) noneAlive |> ignore
      Directory.Exists running |> Expect.isTrue "a host a running process was started from stays however old"
      Directory.Exists stale |> Expect.isFalse "the stale host is pruned"
      Directory.Exists recent |> Expect.isTrue "the recent host stays"
      Directory.Exists current |> Expect.isTrue "the host the daemon resolves stays however old"

    testCase "temp runs: one whose owner is gone goes, a young unmarked one stays, one whose owner is alive stays" <| fun _ ->
      use sb = new Sandbox()
      let run name (daysOld: float) (owner: int option) =
        let dir = Path.Combine(sb.Locations.TempDir, "sagefs-hr", name)
        write (Path.Combine(dir, "data.bin")) "x"
        owner |> Option.iter (fun pid -> File.WriteAllText(Path.Combine(dir, "owner.pid"), string pid))
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - TimeSpan.FromDays daysOld)
        dir
      let orphan = run "orphan" 0.1 (Some 111)
      let young = run "young" 0.1 None
      let alive = run "alive" 30.0 (Some 222)
      let old = run "old" 30.0 None
      let notOurs = Path.Combine(sb.Locations.TempDir, "somebody-elses")
      write (Path.Combine(notOurs, "x")) "x"
      Directory.SetLastWriteTimeUtc(notOurs, DateTime.UtcNow - HygieneAges.ancient)
      let only222 = fun pid _ -> pid = 222
      tidy sb (sb.ScanWith(only222, [], LiveFacts.none)) only222 |> ignore
      Directory.Exists orphan |> Expect.isFalse "an orphaned run goes"
      Directory.Exists old |> Expect.isFalse "an old unmarked run goes"
      Directory.Exists young |> Expect.isTrue "a young unmarked run stays"
      Directory.Exists alive |> Expect.isTrue "a run whose owner is alive stays, however old"
      Directory.Exists notOurs |> Expect.isTrue "an entry without the sagefs prefix is never looked at"

    testCase "spawned-daemon registry: an entry whose daemon is gone is removed, and one whose daemon is alive stays" <| fun _ ->
      use sb = new Sandbox()
      let dir = DaemonOwnership.registryDir sb.Locations.DataDir
      let entry (pid: int) =
        DaemonOwnership.registerSpawned
          sb.Locations.DataDir
          { Pid = pid; StartTime = DateTime.UtcNow; OwnerPid = Some 1; OwnerStart = None; McpPort = 1; DashboardPort = 2; DataDir = "/nowhere" }
        Path.Combine(dir, sprintf "%d.json" pid)
      let gone = entry 5001
      let alive = entry 5002
      let only5002 = fun pid _ -> pid = 5002
      tidy sb (sb.ScanWith(only5002, [], LiveFacts.none)) only5002 |> ignore
      File.Exists gone |> Expect.isFalse "the dead daemon's entry goes"
      File.Exists alive |> Expect.isTrue "the live daemon's entry stays"

    testCase "a SageFs process whose daemon is gone is an orphan, and one whose daemon is alive is not listed" <| fun _ ->
      use sb = new Sandbox()
      let worker (pid: int) (daemon: int) : Proc =
        { Pid = pid
          StartTicks = 5L
          Name = "dotnet"
          CommandLine = "dotnet /x/SageFs.Host.dll abc 0"
          Cwd = CwdState.At "/"
          ParentPid = 1
          Environment = Map.ofList [ "SAGEFS_DAEMON_PID", string daemon ] }
      let only9002 = fun pid _ -> pid = 9002
      let scan = sb.ScanWith(only9002, [ worker 7001 9001; worker 7002 9002 ], LiveFacts.none)
      let orphans = gather scan None |> List.filter (fun l -> Leftover.kind l = LeftoverKind.OrphanProcess)
      orphans |> List.map Leftover.target |> Expect.equal "only the one whose daemon is gone" [ Target.RunningProcess(7001, 5L) ]
      match (Leftover.entry orphans.Head).Standing with
      | Standing.Orphaned _ -> ()
      | other -> failtestf "expected Orphaned, got %A" other
  ]
