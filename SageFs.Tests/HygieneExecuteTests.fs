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

// ─── The daemon's host cache housekeeping ───────────────────────────────

let private housekeeping (sb: Sandbox) : Report =
  let ctx : HygieneEdge.EdgeContext = { MakeScan = (fun () -> sb.Scan()); Git = runGit; IsAlive = noneAlive }
  HygieneService.pruneHostCacheWith ctx sb.Locations

let private makeHost (sb: Sandbox) (name: string) (usedDaysAgo: float option) : string =
  let dir = Path.Combine(sb.Locations.HostCacheDir, name)
  write (Path.Combine(dir, "bin", "FsiHost.dll")) "x"
  match usedDaysAgo with
  | Some days ->
    let marker = Path.Combine(dir, FsiHostBuild.HostLastUsedMarker)
    write marker "used"
    File.SetLastWriteTimeUtc(marker, DateTime.UtcNow - TimeSpan.FromDays days)
    Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - TimeSpan.FromDays days)
  | None -> Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - HygieneAges.ancient)
  dir

[<Tests>]
let hostHousekeepingTests =
  testList "Workspace hygiene: the daemon's host cache housekeeping" [

    testCase "a host nobody used for the retention is pruned, and a recently used one stays" <| fun _ ->
      use sb = new Sandbox()
      let stale = makeHost sb "sdk-10.0.100-aaaa" (Some HygieneAges.ancient.TotalDays)
      let recent = makeHost sb "sdk-10.0.100-bbbb" (Some 1.0)
      housekeeping sb |> ignore
      Directory.Exists stale |> Expect.isFalse "unused for ninety days: pruned"
      Directory.Exists recent |> Expect.isTrue "used yesterday: kept"

    testCase "a host with no use marker is stamped as used now, so the first prune on an old cache loses nothing" <| fun _ ->
      use sb = new Sandbox()
      let old = makeHost sb "sdk-10.0.100-cccc" None
      housekeeping sb |> ignore
      Directory.Exists old |> Expect.isTrue "an unmarked host is kept this time"
      File.Exists(Path.Combine(old, FsiHostBuild.HostLastUsedMarker)) |> Expect.isTrue "and now carries a marker"

    testCase "a host a process is running from is never pruned, however long ago it was marked" <| fun _ ->
      use sb = new Sandbox()
      let running = makeHost sb "sdk-9.0.100-dddd" (Some HygieneAges.ancient.TotalDays)
      sb.Procs <- [ { Pid = 77; StartTicks = 1L; Name = "dotnet"; CommandLine = sprintf "dotnet %s/bin/FsiHost.dll" running; Cwd = CwdState.Unreadable; ParentPid = 1; Environment = Map.empty } ]
      housekeeping sb |> ignore
      Directory.Exists running |> Expect.isTrue "in use: kept"

    testCase "the newest host of an SDK a session resolves is never pruned" <| fun _ ->
      use sb = new Sandbox()
      let newest = makeHost sb "sdk-11.0.100-eeee" (Some HygieneAges.ancient.TotalDays)
      let older = makeHost sb "sdk-11.0.100-ffff" (Some(HygieneAges.ancient.TotalDays + 5.0))
      sb.Live <- { LiveFacts.none with CurrentSdkVersions = [ "11.0.100" ] }
      housekeeping sb |> ignore
      Directory.Exists newest |> Expect.isTrue "the one a new session would reuse stays"
      Directory.Exists older |> Expect.isFalse "an older host of the same SDK goes"

    testCase "it only ever looks at the host cache: stale temp runs and worktrees are untouched" <| fun _ ->
      use sb = new Sandbox()
      let tempRun = Path.Combine(sb.Locations.TempDir, "sagefs-hr", "old")
      write (Path.Combine(tempRun, "x")) "x"
      Directory.SetLastWriteTimeUtc(tempRun, DateTime.UtcNow - HygieneAges.ancient)
      let worktree = sb.AddWorktree "merged-and-idle"
      housekeeping sb |> ignore
      Directory.Exists tempRun |> Expect.isTrue "a stale temp run is not the host cache's business"
      Directory.Exists worktree |> Expect.isTrue "nor is a merged worktree"
  ]
