module SageFs.Tests.WorkspaceHygieneTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.WorkspaceHygiene
open SageFs.Tests.HygieneFixtures

let private standing (s: Subject) = standingOf now s

type ArbHolder =
  static member Subject() = arbSubject

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 300; arbitrary = [ typeof<ArbHolder> ] }

let private leftoverOf (s: Subject) = classify now s

let private isReclaimable (s: Standing) = Standing.reclaimability s = Reclaimability.Reclaimable

[<Tests>]
let standingTests =
  testList "Workspace hygiene: standing" [

    testCase "a worktree whose branch is an ancestor of the base is Merged, however it was merged" <| fun _ ->
      for how in [ MergeHow.Ancestor; MergeHow.PatchEquivalent; MergeHow.TreeEqual ] do
        worktree "a" |> mergedWith how [] |> standing
        |> Expect.equal (sprintf "merged by %A" how) (Standing.Merged how)

    testCase "a merged worktree whose only changes are build output is DirtyGenerated, and lists them" <| fun _ ->
      let files = [ generatedFile "packages.lock.json"; generatedFile "obj/x.json" ]
      match worktree "a" |> mergedWith MergeHow.Ancestor files |> standing with
      | Standing.DirtyGenerated(listed, MergeHow.Ancestor) ->
        NonEmpty.toList listed |> Expect.equal "the generated files are named" files
      | other -> failtestf "expected DirtyGenerated, got %A" other

    testCase "any real change makes a merged worktree DirtyReal, and names only the real files" <| fun _ ->
      let files = [ generatedFile "packages.lock.json"; realFile "src/b.fs" ]
      match worktree "a" |> mergedWith MergeHow.Ancestor files |> standing with
      | Standing.DirtyReal listed ->
        NonEmpty.toList listed |> Expect.equal "just the real file is named" [ realFile "src/b.fs" ]
      | other -> failtestf "expected DirtyReal, got %A" other

    testCase "a worktree with commits the base lacks is CleanButUnmerged, even with generated dirt" <| fun _ ->
      match worktree "a" |> unmergedWith [ generatedFile "obj/y.json" ] |> standing with
      | Standing.CleanButUnmerged commits -> NonEmpty.length commits |> Expect.equal "both commits are listed" 2
      | other -> failtestf "expected CleanButUnmerged, got %A" other

    testCase "real uncommitted work outranks unmerged commits: the work is what a removal loses first" <| fun _ ->
      match worktree "a" |> unmergedWith [ realFile "src/b.fs" ] |> standing with
      | Standing.DirtyReal _ -> ()
      | other -> failtestf "expected DirtyReal, got %A" other

    testCase "anything using it is InUse and keeps every reason, whatever git says" <| fun _ ->
      let s =
        worktree "a"
        |> unmergedWith [ realFile "src/b.fs" ]
        |> withUse (InUseReason.LiveSession "s1")
        |> withUse (InUseReason.ProcessWorkingDirectory(12, "SageFs.Host"))
      match standing s with
      | Standing.InUse(first, more) -> (first :: more) |> List.length |> Expect.equal "both reasons are kept" 2
      | other -> failtestf "expected InUse, got %A" other

    testCase "a git that will not answer is Unknown, not guessed" <| fun _ ->
      let why = UnknownReason.GitUnavailable "git is not installed"
      worktree "a" |> withGit (GitEvidence.GitUnreadable why) |> standing
      |> Expect.equal "unknown carries why" (Standing.Unknown why)

    testCase "a merge that cannot be decided is Unknown" <| fun _ ->
      let why = UnknownReason.MergeUndecidable "no base branch"
      worktree "a" |> withGit (GitEvidence.Worktree(MergeEvidence.MergeNotDecidable why, [])) |> standing
      |> Expect.equal "unknown carries why" (Standing.Unknown why)

    testCase "a cache whose owner is gone is Orphaned since then" <| fun _ ->
      let s = { cache LeftoverKind.GateCheckout (gateRoot + "/checkout-1") (TimeSpan.FromDays 14.0) with Lineage = Lineage.OwnerGone(77, aWeekAgo) }
      standing s |> Expect.equal "orphaned" (Standing.Orphaned aWeekAgo)

    testCase "a cache nobody owns is WithinRetention while young and Expired once old" <| fun _ ->
      let young = cache LeftoverKind.HostCacheEntry (hostRoot + "/sdk-1") (TimeSpan.FromDays 30.0)
      match standing young with
      | Standing.WithinRetention until -> until |> Expect.equal "kept until last use + retention" (aWeekAgo + TimeSpan.FromDays 30.0)
      | other -> failtestf "expected WithinRetention, got %A" other
      let old = cache LeftoverKind.HostCacheEntry (hostRoot + "/sdk-1") (TimeSpan.FromDays 3.0)
      standing old |> Expect.equal "expired" (Standing.Expired(aWeekAgo, TimeSpan.FromDays 3.0))

    testCase "a cache whose owner is alive is InUse, and one whose owner cannot be told is Unknown" <| fun _ ->
      let alive = { cache LeftoverKind.TempRunDir (tempRoot + "/sagefs-hr/1") (TimeSpan.FromDays 1.0) with Lineage = Lineage.OwnerAlive 99 }
      match standing alive with
      | Standing.InUse(InUseReason.OwnerAlive 99, []) -> ()
      | other -> failtestf "expected InUse OwnerAlive, got %A" other
      let undecided = { alive with Lineage = Lineage.LineageUndecidable "no /proc" }
      match standing undecided with
      | Standing.Unknown(UnknownReason.OwnerLivenessUndecidable _) -> ()
      | other -> failtestf "expected Unknown, got %A" other

    testCase "classify puts a subject in the leftover case of its kind and keeps path, size and age" <| fun _ ->
      match leftoverOf (worktree "a") with
      | Leftover.AgentWorktree(entry, detail) ->
        entry.SizeBytes |> Expect.equal "size" 1_000L
        entry.Age |> Expect.equal "age is now minus last touched" (TimeSpan.FromDays 7.0)
        detail.Repo |> Expect.equal "repo" (RepoLink.InRepo repoPath)
        detail.Branch |> Expect.equal "branch" (BranchLabel.OnBranch "worktree-a")
      | other -> failtestf "expected AgentWorktree, got %A" other

    testCase "build output is told from real work by path" <| fun _ ->
      for path in [ "packages.lock.json"; "SageFs.Core/obj/project.assets.json"; "bin/Debug/net11.0/x.dll"; "SageFs.Tests/Foo.received.txt" ] do
        Generated.ruleFor path |> Option.isSome |> Expect.isTrue (sprintf "%s is regenerated" path)
      for path in [ "SageFs.Core/Checkout.fs"; "docs/mcp-tools.md"; "scripts/local-gate" ] do
        Generated.ruleFor path |> Option.isNone |> Expect.isTrue (sprintf "%s is somebody's work" path)

    testCase "every kind describes itself in its own words" <| fun _ ->
      let all =
        [ LeftoverKind.AgentWorktree; LeftoverKind.GateCheckout; LeftoverKind.GateTierClone; LeftoverKind.HostCacheEntry
          LeftoverKind.WorkerLogFile; LeftoverKind.TempRunDir; LeftoverKind.OrphanProcess; LeftoverKind.StaleBranch
          LeftoverKind.SpawnedRegistryEntry ]
      all |> List.map Kind.describe |> List.distinct |> List.length |> Expect.equal "no two kinds share a name" all.Length

    testPropertyWithConfig cfg "PROPERTY: anything InUse is never reclaimable" <| fun (s: Subject) ->
      match s.Uses with
      | [] -> true
      | _ -> Standing.reclaimability (standing s) = Reclaimability.Untouchable

    testPropertyWithConfig cfg "PROPERTY: DirtyReal and CleanButUnmerged are never reclaimable, only reviewable" <| fun (s: Subject) ->
      match standing s with
      | Standing.DirtyReal _
      | Standing.CleanButUnmerged _ -> Standing.reclaimability (standing s) = Reclaimability.NeedsReview
      | _ -> true

    testPropertyWithConfig cfg "PROPERTY: real uncommitted work is never classified as reclaimable" <| fun (s: Subject) ->
      let hasReal =
        match s.Git with
        | GitEvidence.Worktree(_, files) -> files |> List.exists (fun f -> f.Origin = ChangeOrigin.Real)
        | _ -> false
      match hasReal with
      | false -> true
      | true -> not (isReclaimable (standing s))

    testPropertyWithConfig cfg "PROPERTY: Unknown is never reclaimable" <| fun (s: Subject) ->
      match standing s with
      | Standing.Unknown _ -> Standing.reclaimability (standing s) = Reclaimability.Untouchable
      | _ -> true

    testPropertyWithConfig cfg "PROPERTY: classification is total and deterministic" <| fun (s: Subject) ->
      leftoverOf s = leftoverOf s && (Leftover.kind (leftoverOf s) = s.Kind)
  ]

[<Tests>]
let guardTests =
  testList "Workspace hygiene: the one path gate" [

    let accept path = Guard.accept roots identityResolve path

    testCase "a path inside a known root is accepted and remembers which root" <| fun _ ->
      match accept (worktreeRoot + "/agent-1") with
      | Result.Ok accepted ->
        Guard.root accepted |> Expect.equal "root" RootKind.AgentWorktrees
        Guard.path accepted |> Expect.equal "path" (worktreeRoot + "/agent-1")
      | Result.Error refusal -> failtestf "refused: %A" refusal

    testCase "a path outside every root is refused" <| fun _ ->
      match accept "/home/will/Documents" with
      | Result.Error(Refusal.OutsideKnownRoots _) -> ()
      | other -> failtestf "expected OutsideKnownRoots, got %A" other

    testCase "a root itself is refused: only what is inside it can go" <| fun _ ->
      for root in roots.Entries |> List.map snd do
        match accept root with
        | Result.Error(Refusal.IsARoot _) -> ()
        | other -> failtestf "expected IsARoot for %s, got %A" root other

    testCase "a sibling that only shares the root's prefix is outside it" <| fun _ ->
      match accept (gateRoot + "-evil/checkout") with
      | Result.Error(Refusal.OutsideKnownRoots _) -> ()
      | other -> failtestf "expected OutsideKnownRoots, got %A" other

    testCase "dot-dot segments cannot walk out of a root" <| fun _ ->
      match accept (worktreeRoot + "/agent-1/../../../../etc") with
      | Result.Error(Refusal.OutsideKnownRoots _) -> ()
      | other -> failtestf "expected OutsideKnownRoots, got %A" other

    testCase "a path that resolves outside its root through a symlink is refused" <| fun _ ->
      let resolve (p: string) = Result.Ok(if p.EndsWith "link" then "/etc" else p)
      match Guard.accept roots resolve (worktreeRoot + "/link") with
      | Result.Error(Refusal.SymlinkEscapes(_, resolved)) -> resolved |> Expect.equal "where it really goes" "/etc"
      | other -> failtestf "expected SymlinkEscapes, got %A" other

    testCase "a path that cannot be resolved is refused, not assumed fine" <| fun _ ->
      match Guard.accept roots (fun _ -> Result.Error "permission denied") (worktreeRoot + "/agent-1") with
      | Result.Error(Refusal.CannotResolve(_, why)) -> why |> Expect.equal "why" "permission denied"
      | other -> failtestf "expected CannotResolve, got %A" other

    testPropertyWithConfig cfg "PROPERTY: whatever the path, an accepted one is strictly inside a known root" <| fun (segments: string list) ->
      let path = "/" + String.Join("/", segments |> List.map (fun s -> if isNull s then "x" else s.Replace("\000", "")))
      match accept path with
      | Result.Error _ -> true
      | Result.Ok accepted ->
        let p = Guard.path accepted
        roots.Entries |> List.exists (fun (_, root) -> p.StartsWith(root + "/", StringComparison.Ordinal))
  ]

// ─── Plans ─────────────────────────────────────────────────────────────

let private planOf (subjects: Subject list) : Plan = subjects |> List.map leftoverOf |> Planner.plan

let private stepFor (subject: Subject) : Step =
  (planOf [ subject ]).Steps |> List.exactlyOne

[<Tests>]
let planTests =
  testList "Workspace hygiene: the plan" [

    testCase "a merged worktree is a Safe Remove that names the exact command and the bytes it gives back" <| fun _ ->
      let step = worktree "agent-1" |> mergedWith MergeHow.Ancestor [] |> stepFor
      step.Risk |> Expect.equal "risk" Risk.Safe
      step.ReclaimsBytes |> Expect.equal "bytes" 1_000L
      step.Command |> Expect.stringContains "uses git worktree remove" "worktree remove"
      step.Command |> Expect.stringContains "names the worktree" (worktreeRoot + "/agent-1")
      Planner.execution step.Risk |> Expect.equal "runs on confirm" Execution.RunsOnConfirm

    testCase "a merged worktree and its merged branch become one step that removes both" <| fun _ ->
      let wt = worktree "agent-1" |> mergedWith MergeHow.PatchEquivalent []
      let br = branch "worktree-agent-1" (MergeEvidence.MergedInto MergeHow.PatchEquivalent)
      let plan = planOf [ wt; br ]
      plan.Steps |> List.length |> Expect.equal "one step, not two" 1
      plan.Steps.Head.Action |> Expect.equal "removes the branch too" (Action.RemoveBranchToo "worktree-agent-1")

    testCase "a merged branch with no worktree is a Safe branch removal" <| fun _ ->
      let step = branch "worktree-agent-9" (MergeEvidence.MergedInto MergeHow.Ancestor) |> stepFor
      step.Risk |> Expect.equal "safe" Risk.Safe
      step.Command |> Expect.stringContains "deletes the branch" "branch"

    testCase "real uncommitted work is never a Safe step: it saves a patch first and is only presented" <| fun _ ->
      let step = worktree "agent-1" |> mergedWith MergeHow.Ancestor [ realFile "src/b.fs" ] |> stepFor
      step.Risk |> Expect.equal "risk" Risk.UncommittedWork
      Planner.execution step.Risk |> Expect.equal "never run by tidy" Execution.PresentedOnly
      match step.Action with
      | Action.SaveDiffThenRemove patch -> step.Command |> Expect.stringContains "saves the diff to the patch" patch
      | other -> failtestf "expected SaveDiffThenRemove, got %A" other

    testCase "unmerged commits are only presented, with the command that keeps them on a branch" <| fun _ ->
      let step = worktree "agent-1" |> unmergedWith [] |> stepFor
      step.Risk |> Expect.equal "risk" Risk.UnmergedCommits
      step.Action |> Expect.equal "review" Action.KeepAndReview
      Planner.execution step.Risk |> Expect.equal "never run by tidy" Execution.PresentedOnly
      step.Command |> Expect.stringContains "names the branch that keeps the commits" "worktree-agent-1"

    testCase "a worktree on a detached head with unmerged commits gets a branch made for them first" <| fun _ ->
      let s = { worktree "agent-1" with Branch = BranchLabel.DetachedAt "abc1234" } |> unmergedWith []
      (stepFor s).Command |> Expect.stringContains "makes a branch before removing" "branch rescue/agent-1 abc1234"

    testCase "something in use is a Nothing step that says what is using it" <| fun _ ->
      let step = worktree "agent-1" |> withUse (InUseReason.LiveSession "abc") |> stepFor
      step.Action |> Expect.equal "nothing" Action.Nothing
      step.Risk |> Expect.equal "busy" Risk.Busy
      step.Reason |> Expect.stringContains "names the session" "abc"

    testCase "an Unknown is a Nothing step that says why" <| fun _ ->
      let step = worktree "agent-1" |> withGit (GitEvidence.GitUnreadable(UnknownReason.GitUnavailable "no git")) |> stepFor
      step.Action |> Expect.equal "nothing" Action.Nothing
      step.Risk |> Expect.equal "unverifiable" Risk.Unverifiable
      step.Reason |> Expect.stringContains "says why" "no git"

    testCase "an orphaned process is a Safe StopProcess, and an expired cache entry a Safe prune" <| fun _ ->
      (stepFor (orphanProcess 4321)).Action |> Expect.equal "stop it" (Action.StopProcess 4321)
      let host = cache LeftoverKind.HostCacheEntry (hostRoot + "/sdk-1") (TimeSpan.FromDays 3.0)
      match (stepFor host).Action with
      | Action.PruneOlderThan age -> age |> Expect.equal "named retention" (TimeSpan.FromDays 3.0)
      | other -> failtestf "expected PruneOlderThan, got %A" other

    testCase "the same facts in any order make the same plan with the same id" <| fun _ ->
      let subjects = [ worktree "a"; worktree "b" |> unmergedWith []; cache LeftoverKind.TempRunDir (tempRoot + "/sagefs-hr/1") (TimeSpan.FromDays 1.0) ]
      (planOf subjects).Id |> Expect.equal "order does not matter" (planOf (List.rev subjects)).Id

    testCase "a different set of facts makes a different plan id" <| fun _ ->
      (planOf [ worktree "a" ]).Id |> Expect.notEqual "changed" (planOf [ worktree "a"; worktree "b" ]).Id

    testCase "safe steps come first, biggest first, and the totals add up" <| fun _ ->
      let small = { worktree "small" with SizeBytes = 10L }
      let big = { worktree "big" with SizeBytes = 900L }
      let review = worktree "review" |> unmergedWith []
      let plan = planOf [ review; small; big ]
      plan.Steps |> List.map (fun s -> s.Risk) |> Expect.equal "safe before review" [ Risk.Safe; Risk.Safe; Risk.UnmergedCommits ]
      plan.Steps.Head.ReclaimsBytes |> Expect.equal "biggest first" 900L
      plan.SafeBytes |> Expect.equal "safe total" 910L
      plan.ReviewBytes |> Expect.equal "review total" 1_000L

    testPropertyWithConfig cfg "PROPERTY: only a Safe step runs on confirm, and a Safe step only comes from a reclaimable standing" <| fun (subjects: Subject list) ->
      let leftovers = subjects |> List.map leftoverOf
      let plan = Planner.plan leftovers
      let standings = leftovers |> List.map (fun l -> (Leftover.entry l).Standing)
      plan.Steps
      |> List.forall (fun step ->
        let runs = Planner.execution step.Risk = Execution.RunsOnConfirm
        let safe = step.Risk = Risk.Safe
        let fromReclaimable =
          match step.Risk with
          | Risk.Safe -> (Leftover.entry step.Target).Standing |> isReclaimable
          | _ -> true
        runs = safe && fromReclaimable)
      && (standings |> List.length) >= (plan.Steps |> List.length)

    testPropertyWithConfig cfg "PROPERTY: nothing in use, unknown, unmerged or really dirty ever gets a step that removes it" <| fun (subjects: Subject list) ->
      Planner.plan (subjects |> List.map leftoverOf)
      |> fun plan ->
        plan.Steps
        |> List.forall (fun step ->
          match step.Action, (Leftover.entry step.Target).Standing with
          | (Action.Remove | Action.RemoveBranchToo _ | Action.PruneOlderThan _ | Action.StopProcess _), Standing.InUse _ -> false
          | (Action.Remove | Action.RemoveBranchToo _ | Action.PruneOlderThan _ | Action.StopProcess _), Standing.Unknown _ -> false
          | (Action.Remove | Action.RemoveBranchToo _ | Action.PruneOlderThan _ | Action.StopProcess _), Standing.DirtyReal _ -> false
          | (Action.Remove | Action.RemoveBranchToo _ | Action.PruneOlderThan _ | Action.StopProcess _), Standing.CleanButUnmerged _ -> false
          | _ -> true)

    testPropertyWithConfig cfg "PROPERTY: planning is deterministic and does not depend on input order" <| fun (subjects: Subject list) ->
      let leftovers = subjects |> List.map leftoverOf
      (Planner.plan leftovers).Id = (Planner.plan (List.rev leftovers)).Id
  ]

// ─── Running a plan ────────────────────────────────────────────────────

/// A world of leftovers keyed by target, with a log of what was performed.
type private World(initial: Subject list) =
  let mutable subjects = initial |> List.map (fun s -> s.Target, s) |> Map.ofList
  let performed = ResizeArray<Operation>()
  member _.Performed = performed |> Seq.toList
  member _.Mutate(target: Target, f: Subject -> Subject) = subjects <- subjects |> Map.change target (Option.map f)
  member _.Replace(target: Target, subject: Subject) = subjects <- Map.add target subject subjects
  member _.Effects : Effects =
    { Recheck =
        fun target ->
          match Map.tryFind target subjects with
          | None -> Rechecked.Gone
          | Some s -> Rechecked.Fresh(classify now s)
      Resolve = identityResolve
      Perform =
        fun op ->
          performed.Add op
          match op with
          | Operation.RemoveWorktree(a, _, _)
          | Operation.RemoveTree a -> subjects <- subjects |> Map.remove (Target.Directory(Guard.path a)); Outcome.Done 1L
          | Operation.RemoveFile a -> subjects <- subjects |> Map.remove (Target.File(Guard.path a)); Outcome.Done 1L
          | Operation.DeleteBranch(repo, name, _) -> subjects <- subjects |> Map.remove (Target.GitBranch(repo, name)); Outcome.Done 0L
          | Operation.Terminate(pid, ticks) -> subjects <- subjects |> Map.remove (Target.RunningProcess(pid, ticks)); Outcome.Done 0L }

let private confirmed (plan: Plan) : Confirmation =
  match Confirmation.safeOnly plan plan.Id with
  | Result.Ok c -> c
  | Result.Error e -> failtestf "could not confirm the plan it just made: %A" e

[<Tests>]
let executorTests =
  testList "Workspace hygiene: running a plan" [

    testCase "a confirmation is only given for the plan the caller was shown" <| fun _ ->
      let shown = planOf [ worktree "a" ]
      let now' = planOf [ worktree "a"; worktree "b" ]
      match Confirmation.safeOnly now' shown.Id with
      | Result.Error(ConfirmError.PlanChanged(s, c)) ->
        s |> Expect.equal "shown" shown.Id
        c |> Expect.equal "current" now'.Id
      | Result.Ok _ -> failtest "confirmed a plan that changed"

    testCase "running a Safe step performs the operation and counts what it gave back" <| fun _ ->
      let s = worktree "agent-1"
      let world = World [ s ]
      let plan = planOf [ s ]
      let report = Executor.run world.Effects roots (confirmed plan) plan
      world.Performed |> List.length |> Expect.equal "one operation" 1
      report.Executed |> List.map (fun e -> e.Result) |> Expect.equal "ran" [ StepResult.Ran(Outcome.Done 1L) ]
      report.ReclaimedBytes |> Expect.equal "sum of what ran" 1L

    testCase "steps that need a look are presented and never performed" <| fun _ ->
      let subjects = [ worktree "a" |> unmergedWith []; worktree "b" |> mergedWith MergeHow.Ancestor [ realFile "src/x.fs" ]; worktree "c" |> withUse (InUseReason.LiveSession "s") ]
      let world = World subjects
      let plan = planOf subjects
      let report = Executor.run world.Effects roots (confirmed plan) plan
      world.Performed |> Expect.isEmpty "nothing was performed"
      report.Executed |> List.forall (fun e -> e.Result = StepResult.Presented) |> Expect.isTrue "every step was only presented"

    testCase "a worktree that became busy between the plan and the run is skipped and says so" <| fun _ ->
      let s = worktree "agent-1"
      let world = World [ s ]
      let plan = planOf [ s ]
      world.Mutate(s.Target, withUse (InUseReason.LiveSession "late"))
      let report = Executor.run world.Effects roots (confirmed plan) plan
      world.Performed |> Expect.isEmpty "nothing was performed"
      match report.Executed |> List.exactlyOne |> fun e -> e.Result with
      | StepResult.Skipped(SkipReason.StandingChanged(Standing.InUse _)) -> ()
      | other -> failtestf "expected a skip for the new standing, got %A" other

    testCase "a worktree that gained real work between the plan and the run is skipped" <| fun _ ->
      let s = worktree "agent-1"
      let world = World [ s ]
      let plan = planOf [ s ]
      world.Mutate(s.Target, mergedWith MergeHow.Ancestor [ realFile "src/new.fs" ])
      let report = Executor.run world.Effects roots (confirmed plan) plan
      world.Performed |> Expect.isEmpty "nothing was performed"
      match (List.exactlyOne report.Executed).Result with
      | StepResult.Skipped(SkipReason.StandingChanged(Standing.DirtyReal _)) -> ()
      | other -> failtestf "expected a skip, got %A" other

    testCase "a path that now holds a different branch is not the thing that was planned" <| fun _ ->
      let s = worktree "agent-1"
      let world = World [ s ]
      let plan = planOf [ s ]
      world.Replace(s.Target, { s with Branch = BranchLabel.OnBranch "somebody-elses-branch" })
      let report = Executor.run world.Effects roots (confirmed plan) plan
      world.Performed |> Expect.isEmpty "nothing was performed"
      (List.exactlyOne report.Executed).Result |> Expect.equal "identity changed" (StepResult.Skipped SkipReason.IdentityChanged)

    testCase "a target that is already gone counts as done, not as a failure" <| fun _ ->
      let s = worktree "agent-1"
      let world = World []
      let plan = planOf [ s ]
      let report = Executor.run world.Effects roots (confirmed plan) plan
      (List.exactlyOne report.Executed).Result |> Expect.equal "already gone" StepResult.AlreadyGone

    testCase "running the same plan twice is the same as running it once" <| fun _ ->
      let subjects = [ worktree "a"; worktree "b"; orphanProcess 4321; cache LeftoverKind.TempRunDir (tempRoot + "/sagefs-hr/1") (TimeSpan.FromDays 1.0) ]
      let world = World subjects
      let plan = planOf subjects
      let c = confirmed plan
      let first = Executor.run world.Effects roots c plan
      let second = Executor.run world.Effects roots c plan
      second.ReclaimedBytes |> Expect.equal "nothing more reclaimed" 0L
      second.Executed |> List.forall (fun e -> e.Result = StepResult.AlreadyGone || e.Result = StepResult.Presented) |> Expect.isTrue "no second removal"
      (first.ReclaimedBytes > 0L) |> Expect.isTrue "the first run did reclaim"

    testCase "a path outside the known roots is refused at the gate and never performed" <| fun _ ->
      let outside = { worktree "x" with Target = Target.Directory "/home/will/Documents/x" }
      let world = World [ outside ]
      let plan = planOf [ outside ]
      let report = Executor.run world.Effects roots (confirmed plan) plan
      world.Performed |> Expect.isEmpty "nothing was performed"
      match (List.exactlyOne report.Executed).Result with
      | StepResult.Skipped(SkipReason.Refused _) -> ()
      | other -> failtestf "expected a refusal, got %A" other

    testCase "a worktree that differs only by build output is removed with the force that allows it, and no more" <| fun _ ->
      let s = worktree "agent-1" |> mergedWith MergeHow.Ancestor [ generatedFile "packages.lock.json" ]
      let world = World [ s ]
      let plan = planOf [ s ]
      Executor.run world.Effects roots (confirmed plan) plan |> ignore
      match world.Performed with
      | [ Operation.RemoveWorktree(_, _, ForceNeed.OnlyGeneratedFilesDiffer) ] -> ()
      | other -> failtestf "expected one forced-for-generated removal, got %A" other

    testCase "a clean merged worktree is removed with no force at all" <| fun _ ->
      let s = worktree "agent-1"
      let world = World [ s ]
      let plan = planOf [ s ]
      Executor.run world.Effects roots (confirmed plan) plan |> ignore
      match world.Performed with
      | [ Operation.RemoveWorktree(_, _, ForceNeed.NoForce) ] -> ()
      | other -> failtestf "expected an unforced removal, got %A" other

    testCase "removing a worktree and its branch deletes the branch only on the merge evidence it still has" <| fun _ ->
      let wt = worktree "agent-1" |> mergedWith MergeHow.PatchEquivalent []
      let br = branch "worktree-agent-1" (MergeEvidence.MergedInto MergeHow.PatchEquivalent)
      let world = World [ wt; br ]
      let plan = planOf [ wt; br ]
      Executor.run world.Effects roots (confirmed plan) plan |> ignore
      match world.Performed with
      | [ Operation.RemoveWorktree _; Operation.DeleteBranch(_, "worktree-agent-1", BranchDeletion.BecauseMergedBy MergeHow.PatchEquivalent) ] -> ()
      | other -> failtestf "expected the worktree then the branch, got %A" other

    testCase "an orphan process is stopped by pid and start time, so a recycled pid is never hit" <| fun _ ->
      let s = orphanProcess 4321
      let world = World [ s ]
      let plan = planOf [ s ]
      Executor.run world.Effects roots (confirmed plan) plan |> ignore
      world.Performed |> Expect.equal "terminated with its start time" [ Operation.Terminate(4321, 4242L) ]
  ]
