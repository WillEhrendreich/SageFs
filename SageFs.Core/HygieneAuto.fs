/// What the daemon may tidy by itself. One closed list, in one place: the kinds of leftover whose Safe steps an automatic sweep
/// runs, and nothing else. Everything the planner and executor already guarantee still applies to these steps (the path gate,
/// the second look right before acting, the edge's own refusal to remove a dirty worktree or an unmerged branch). This module
/// only narrows WHAT is put in front of them.
///
/// Left out on purpose: a stray process (killing is never automatic), gate checkouts, tier clones and pass records (the gate
/// has its own reaper), the host cache (its own prune), and every standing that is not Safe. A worktree that holds work is
/// never Safe, so it is never here.
module SageFs.HygieneAuto

open SageFs.WorkspaceHygiene

/// The kinds an automatic sweep looks at.
let kinds : LeftoverKind list =
  [ LeftoverKind.AgentWorktree
    LeftoverKind.StaleBranch
    LeftoverKind.WorkerLogFile
    LeftoverKind.TempRunDir
    LeftoverKind.SpawnedRegistryEntry ]

let isAutomatic (kind: LeftoverKind) : bool = List.contains kind kinds

/// The leftovers an automatic sweep plans over.
let eligible (leftovers: Leftover list) : Leftover list =
  leftovers |> List.filter (fun l -> isAutomatic (Leftover.kind l))

/// The plan an automatic sweep runs: planned over the eligible kinds only, so what it shows is exactly what it may do.
let plan (leftovers: Leftover list) : Plan = Planner.plan (eligible leftovers)

/// Whether a step of a plan is one the sweep would run: a Safe step of an automatic kind.
let runs (step: Step) : bool =
  isAutomatic (Leftover.kind step.Target) && Planner.execution step.Risk = Execution.RunsOnConfirm
