module SageFs.Tests.HygieneGatherTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.Tests.HygieneSandbox

[<Tests>]
let parseTests =
  testList "Workspace hygiene gather: parsing git" [

    testCase "a worktree list names each worktree's path, head, branch and lock" <| fun _ ->
      let text =
        String.Join("\n",
          [ "worktree /repo"; "HEAD aaa"; "branch refs/heads/master"; ""
            "worktree /repo/.claude/worktrees/agent-1"; "HEAD bbb"; "branch refs/heads/worktree-agent-1"
            "locked claude agent agent-1 (pid 314668 start 793942)"; ""
            "worktree /tmp/detached"; "HEAD ccc"; "detached"; "" ])
      match Parse.worktreeList text with
      | [ main; locked; detached ] ->
        main.Branch |> Expect.equal "main branch" (BranchLabel.OnBranch "master")
        main.Lock |> Expect.equal "main is unlocked" LockState.Unlocked
        locked.Path |> Expect.equal "path" "/repo/.claude/worktrees/agent-1"
        locked.Lock |> Expect.equal "lock reason" (LockState.LockedWith "claude agent agent-1 (pid 314668 start 793942)")
        detached.Branch |> Expect.equal "detached head carries its sha" (BranchLabel.DetachedAt "ccc")
      | other -> failtestf "expected three worktrees, got %A" other

    testCase "a lock reason that names a pid gives it, and one that does not gives none" <| fun _ ->
      Parse.lockPid "claude agent agent-1 (pid 314668 start 793942)" |> Expect.equal "the pid" (Some 314668)
      Parse.lockPid "held for a rebase" |> Expect.isNone "no pid"

    testCase "status paths come out of the NUL-separated form, and a rename's old path is not a second change" <| fun _ ->
      let text = " M packages.lock.json\000?? src/new.fs\000R  new.fs\000old.fs\000"
      Parse.statusPaths text |> Expect.equal "three changed paths" [ "packages.lock.json"; "src/new.fs"; "new.fs" ]

    testCase "cherry lists only the commits the base lacks" <| fun _ ->
      let text = "+ abc1234 add the thing\n- def5678 already there\n+ 0a0b0c0 and another\n"
      Parse.cherryAhead text |> List.map (fun c -> c.Sha) |> Expect.equal "the + lines" [ "abc1234"; "0a0b0c0" ]
  ]

[<Tests>]
let gatherTests =
  testList "Workspace hygiene gather: a real repository" [

    testTask "each kind of worktree gets the standing its git state earns" {
      use sb = new Sandbox()
      let paths = populate sb
      let! leftovers = gather (sb.Scan()) None
      let at name = standingOfPath leftovers paths.[name]
      at "clean-merged" |> Expect.equal "merged at the base head is an ancestor" (Standing.Merged MergeHow.Ancestor)
      at "ff-merged" |> Expect.equal "fast-forwarded is an ancestor" (Standing.Merged MergeHow.Ancestor)
      at "squashed" |> Expect.equal "cherry-picked is patch-equivalent" (Standing.Merged MergeHow.PatchEquivalent)
      match at "unmerged" with
      | Standing.CleanButUnmerged commits -> commits.Head.Subject |> Expect.equal "names the commit" "unmerged work"
      | other -> failtestf "expected CleanButUnmerged, got %A" other
      match at "dirty-generated" with
      | Standing.DirtyGenerated(files, MergeHow.Ancestor) ->
        NonEmpty.toList files |> List.map (fun f -> f.Path) |> Expect.equal "the lock file" [ "packages.lock.json" ]
      | other -> failtestf "expected DirtyGenerated, got %A" other
      match at "dirty-real" with
      | Standing.DirtyReal files ->
        NonEmpty.toList files |> List.map (fun f -> f.Path) |> Expect.equal "the new file" [ "src/precious.fs" ]
      | other -> failtestf "expected DirtyReal, got %A" other
    }

    testTask "a worktree where a process has its working directory is InUse" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "busy"
      let proc : Proc = { Pid = 4321; StartTicks = 1L; Name = "bash"; CommandLine = "bash"; Cwd = CwdState.At path; ParentPid = 1; Environment = Map.empty }
      let! leftovers = gather (sb.ScanWith((fun _ _ -> false), [ proc ], LiveFacts.none)) None
      match standingOfPath leftovers path with
      | Standing.InUse(InUseReason.ProcessWorkingDirectory(4321, "bash"), []) -> ()
      | other -> failtestf "expected InUse by the process, got %A" other
    }

    testTask "a worktree where a session runs is InUse" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "session"
      let live = { LiveFacts.none with Sessions = [ "abc12345", Path.Combine(path, "src") ] }
      let! leftovers = gather (sb.ScanWith((fun _ _ -> false), [], live)) None
      match standingOfPath leftovers path with
      | Standing.InUse(InUseReason.LiveSession "abc12345", _) -> ()
      | other -> failtestf "expected InUse by the session, got %A" other
    }

    testTask "a worktree locked by a live process is InUse, and one locked by a dead one is not" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "locked"
      git sb.Repo [ "worktree"; "lock"; "--reason"; "claude agent locked (pid 777 start 1)"; path ] |> ignore
      let! live = gather (sb.ScanWith((fun pid _ -> pid = 777), [], LiveFacts.none)) None
      match standingOfPath live path with
      | Standing.InUse(InUseReason.LockedByLiveProcess(777, _), _) -> ()
      | other -> failtestf "expected locked by a live process, got %A" other
      let! dead = gather (sb.Scan()) None
      standingOfPath dead path |> Expect.equal "a dead lock does not make it busy" (Standing.Merged MergeHow.Ancestor)
    }

    testTask "a merged branch whose worktree is in the agent area is paired with it and not marked in use" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "pair"
      let! leftovers = gather (sb.Scan()) None
      let branchStanding =
        leftovers
        |> List.pick (fun l ->
          match l with
          | Leftover.StaleBranch(e, _) when e.Target = Target.GitBranch(sb.Repo, "worktree-agent-pair") -> Some e.Standing
          | _ -> None)
      branchStanding |> Expect.equal "merged" (Standing.Merged MergeHow.Ancestor)
      let plan = Planner.plan leftovers
      plan.Steps
      |> List.filter (fun s -> Leftover.target s.Target = Target.Directory path || Leftover.target s.Target = Target.GitBranch(sb.Repo, "worktree-agent-pair"))
      |> List.length
      |> Expect.equal "one step covers the worktree and its branch" 1
    }

    testTask "a worktree whose creating agent has disconnected says who made it and that they are gone" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "orphaned-by-agent"
      let live = { LiveFacts.none with Owners = [ path, Owner.CreatedByAgent("claude", "conn-1"), AgentConnection.NotConnected ] }
      let! found = gather (sb.ScanWith((fun _ _ -> false), [], live)) None
      let plan = Planner.plan found
      let step = plan.Steps |> List.find (fun s -> Leftover.target s.Target = Target.Directory path)
      step.Reason |> Expect.stringContains "names the agent" "claude"
      step.Reason |> Expect.stringContains "says it is gone" "gone"
      (Leftover.entry step.Target).Owner |> Expect.equal "the owner is the connection, not just a name" (Owner.CreatedByAgent("claude", "conn-1"))
    }

    testTask "a daemon scoped to its own descendants lists only those as orphans, while every process still counts as a use" {
      use sb = new Sandbox()
      let path = sb.AddWorktree "scoped"
      let orphan (pid: int) (parent: int) : Proc =
        { Pid = pid; StartTicks = 5L; Name = "dotnet"; CommandLine = "dotnet /x/SageFs.Host.dll abc 0"; Cwd = CwdState.At "/"
          ParentPid = parent; Environment = Map.ofList [ "SAGEFS_DAEMON_PID", "9001" ] }
      let outsider : Proc = { orphan 8001 1 with Name = "bash"; CommandLine = "bash"; Cwd = CwdState.At path }
      let scan = { sb.ScanWith((fun _ _ -> false), [ orphan 7001 100; orphan 7002 1; outsider ], LiveFacts.none) with Loc = { sb.Locations with Processes = ProcessScope.DescendantsOf 100 } }
      let! leftovers = gather scan None
      leftovers
      |> List.filter (fun l -> Leftover.kind l = LeftoverKind.OrphanProcess)
      |> List.map Leftover.target
      |> Expect.equal "only the descendant of the scope root" [ Target.RunningProcess(7001, 5L) ]
      match standingOfPath leftovers path with
      | Standing.InUse(InUseReason.ProcessWorkingDirectory(8001, _), _) -> ()
      | other -> failtestf "a process outside the scope still holds the worktree, got %A" other
    }

    testTask "a git that is not there makes everything about worktrees Unknown, not clean" {
      use sb = new Sandbox()
      let scan = { sb.Scan() with Git = (fun _ _ -> Task.FromResult(GitResult.Unavailable "git is not installed")) }
      let! leftovers = gather scan None
      leftovers |> List.isEmpty |> Expect.isFalse "something is still reported"
      leftovers
      |> List.forall (fun l ->
        match (Leftover.entry l).Standing with
        | Standing.Unknown _ -> true
        | _ -> false)
      |> Expect.isTrue "every leftover is Unknown"
    }
  ]
