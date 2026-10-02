/// How workspace hygiene reads to a person or an agent: the plan as text, the one-line nudge for replies an
/// orchestrator already reads, and the summary the dashboard and the status tools share. Pure.
module SageFs.WorkspaceHygieneRender

open System
open SageFs.WorkspaceHygiene

module Limits =
  /// Items listed per group before "and N more". A hundred worktrees listed one by one is a wall; the biggest
  /// few are what a reader acts on, and the rest are one `get_workspace_hygiene` call away in full.
  let itemsPerGroup = 8

let formatBytes (bytes: int64) : string =
  let kib = 1024.0
  let value = float bytes
  match value with
  | v when v >= kib * kib * kib * kib -> sprintf "%.1f TB" (v / (kib * kib * kib * kib))
  | v when v >= kib * kib * kib -> sprintf "%.1f GB" (v / (kib * kib * kib))
  | v when v >= kib * kib -> sprintf "%.0f MB" (v / (kib * kib))
  | v when v >= kib -> sprintf "%.0f KB" (v / kib)
  | _ -> sprintf "%d B" bytes

let formatAge (age: TimeSpan) : string =
  match age with
  | a when a.TotalDays >= 2.0 -> sprintf "%.0fd" a.TotalDays
  | a when a.TotalHours >= 1.0 -> sprintf "%.0fh" a.TotalHours
  | a -> sprintf "%.0fm" (max 0.0 a.TotalMinutes)

module Refusal =
  let describe (refusal: Refusal) : string =
    match refusal with
    | Refusal.OutsideKnownRoots path -> sprintf "%s is outside every directory SageFs manages" path
    | Refusal.IsARoot path -> sprintf "%s is a root itself" path
    | Refusal.SymlinkEscapes(path, resolved) -> sprintf "%s is a link that leaves its root (it goes to %s)" path resolved
    | Refusal.CannotResolve(path, ResolveFailure.TooManyLinks _) -> sprintf "%s goes through too many links to follow" path
    | Refusal.CannotResolve(path, ResolveFailure.Unreadable(_, detail)) -> sprintf "%s could not be resolved (%s)" path detail

module Risk =
  let describe (risk: Risk) : string =
    match risk with
    | Risk.Safe -> "safe to reclaim"
    | Risk.UnmergedCommits -> "has commits the base branch lacks"
    | Risk.UncommittedWork -> "has uncommitted work"
    | Risk.Unverifiable -> "could not be judged"
    | Risk.Busy -> "in use or too young"

  let all : Risk list = [ Risk.Safe; Risk.UnmergedCommits; Risk.UncommittedWork; Risk.Unverifiable; Risk.Busy ]

/// What a person calls a leftover: the path or the name, whichever is shorter to read.
let label (leftover: Leftover) : string =
  match Leftover.target leftover with
  | Target.Directory p
  | Target.File p -> p
  | Target.GitBranch(_, name) -> name
  | Target.RunningProcess(pid, _) ->
    match leftover with
    | Leftover.OrphanProcess(_, info) -> sprintf "pid %d (%s)" pid (if info.CommandLine.Length > 60 then info.CommandLine.Substring(0, 60) + "..." else info.CommandLine)
    | _ -> sprintf "pid %d" pid

/// The numbers the nudge, the dashboard header and the status tools quote.
type Summary =
  { LeftoverWorktrees: int
    LeftoverWorktreeBytes: int64
    GateBytes: int64
    SafeCount: int
    SafeBytes: int64
    ReviewCount: int
    ReviewBytes: int64
    PlanId: PlanId }

let summarize (leftovers: Leftover list) (plan: Plan) : Summary =
  let worktrees =
    leftovers
    |> List.filter (fun l ->
      match l with
      | Leftover.AgentWorktree(e, _) ->
        match e.Standing with
        | Standing.InUse _ -> false
        | _ -> true
      | _ -> false)
  let gate =
    leftovers
    |> List.filter (fun l -> match l with | Leftover.GateCheckout _ | Leftover.GateTierClone _ -> true | _ -> false)
    |> List.sumBy (fun l -> (Leftover.entry l).SizeBytes)
  let safe = plan.Steps |> List.filter (fun s -> s.Risk = Risk.Safe)
  let review = plan.Steps |> List.filter (fun s -> s.Risk = Risk.UnmergedCommits || s.Risk = Risk.UncommittedWork)
  { LeftoverWorktrees = List.length worktrees
    LeftoverWorktreeBytes = worktrees |> List.sumBy (fun l -> (Leftover.entry l).SizeBytes)
    GateBytes = gate
    SafeCount = List.length safe
    SafeBytes = plan.SafeBytes
    ReviewCount = List.length review
    ReviewBytes = plan.ReviewBytes
    PlanId = plan.Id }

/// The line that goes into replies an orchestrator already reads, or nothing while the workspace is tidy enough.
/// Past either named threshold it says what and how much, and what to call.
let nudge (summary: Summary) : string option =
  let tooManyWorktrees = summary.LeftoverWorktrees > Thresholds.leftoverWorktreeNudge
  let gateTooBig = summary.GateBytes > Thresholds.gateDirNudgeBytes
  match tooManyWorktrees, gateTooBig with
  | false, false -> None
  | _ ->
    let parts =
      [ if tooManyWorktrees then
          yield sprintf "%d leftover worktrees (%s)" summary.LeftoverWorktrees (formatBytes summary.LeftoverWorktreeBytes)
        if gateTooBig then
          yield sprintf "the gate dir holds %s" (formatBytes summary.GateBytes) ]
    Some(sprintf "workspace: %s, %d safe to reclaim (%s): call get_workspace_hygiene" (String.Join("; ", parts)) summary.SafeCount (formatBytes summary.SafeBytes))

let private kindsOrder : LeftoverKind list = Kind.all

let private stepLine (step: Step) : string =
  let entry = Leftover.entry step.Target
  sprintf "  %s  %s, %s old. %s" (label step.Target) (formatBytes entry.SizeBytes) (formatAge entry.Age) step.Reason

/// The plan as text: what is safe to reclaim, what needs a look first (with the command that saves the work), and what
/// was left alone and why. Always a dry run: nothing here has been done.
let renderPlan (leftovers: Leftover list) (plan: Plan) : string =
  let summary = summarize leftovers plan
  let sb = Text.StringBuilder()
  let line (s: string) = sb.AppendLine s |> ignore
  line (sprintf "Workspace hygiene (dry run, plan %s)" (let (PlanId id) = plan.Id in id))
  line (sprintf "%d leftover(s) found. %d safe to reclaim (%s), %d need a look (%s)." (List.length leftovers) summary.SafeCount (formatBytes summary.SafeBytes) summary.ReviewCount (formatBytes summary.ReviewBytes))
  for risk in Risk.all do
    let steps = plan.Steps |> List.filter (fun s -> s.Risk = risk)
    match steps with
    | [] -> ()
    | _ ->
      line ""
      line (sprintf "%s (%d)" (Risk.describe risk |> fun d -> d.Substring(0, 1).ToUpperInvariant() + d.Substring 1) (List.length steps))
      for kind in kindsOrder do
        let ofKind = steps |> List.filter (fun s -> Leftover.kind s.Target = kind)
        match ofKind with
        | [] -> ()
        | _ ->
          let sorted = ofKind |> List.sortByDescending (fun s -> (Leftover.entry s.Target).SizeBytes)
          let shown = sorted |> List.truncate Limits.itemsPerGroup
          line (sprintf " %s x%d, %s" (Kind.describe kind) (List.length ofKind) (formatBytes (ofKind |> List.sumBy (fun s -> (Leftover.entry s.Target).SizeBytes))))
          for step in shown do
            line (stepLine step)
            match risk, step.Command with
            | Risk.Safe, _ -> ()
            | _, "" -> ()
            | _, command -> line (sprintf "    to keep it: %s" command)
          match List.length sorted - List.length shown with
          | 0 -> ()
          | more -> line (sprintf "  ...and %d more" more)
  line ""
  match summary.SafeCount with
  | 0 -> line "Nothing is safe to reclaim right now."
  | n -> line (sprintf "To reclaim the %d safe item(s): tidy_workspace with confirm=true and plan=%s. Anything marked needs-a-look is never touched by tidy." n (let (PlanId id) = plan.Id in id))
  sb.ToString().TrimEnd()

/// What `tidy` did, one line per step that did something or was refused.
let renderReport (report: Report) : string =
  let sb = Text.StringBuilder()
  let line (s: string) = sb.AppendLine s |> ignore
  let ran = report.Executed |> List.filter (fun e -> match e.Result with | StepResult.Ran(Outcome.Done _) -> true | _ -> false)
  let failed = report.Executed |> List.filter (fun e -> match e.Result with | StepResult.Ran(Outcome.Failed _) -> true | _ -> false)
  let skipped = report.Executed |> List.filter (fun e -> match e.Result with | StepResult.Skipped _ -> true | _ -> false)
  let gone = report.Executed |> List.filter (fun e -> e.Result = StepResult.AlreadyGone)
  line (sprintf "Tidied: %d removed (%s reclaimed), %d already gone, %d skipped, %d failed." (List.length ran) (formatBytes report.ReclaimedBytes) (List.length gone) (List.length skipped) (List.length failed))
  for e in skipped do
    match e.Result with
    | StepResult.Skipped reason ->
      let why =
        match reason with
        | SkipReason.StandingChanged s -> sprintf "it changed since the plan: %s" (Standing.describe s)
        | SkipReason.IdentityChanged -> "something else is at that path now"
        | SkipReason.Refused r -> sprintf "refused by the path gate: %s" (Refusal.describe r)
        | SkipReason.LookFailed w -> sprintf "could not look again (%s)" w
      line (sprintf "  skipped %s: %s" (label e.Step.Target) why)
    | _ -> ()
  for e in failed do
    match e.Result with
    | StepResult.Ran(Outcome.Failed why) -> line (sprintf "  failed %s: %s" (label e.Step.Target) why)
    | _ -> ()
  sb.ToString().TrimEnd()
