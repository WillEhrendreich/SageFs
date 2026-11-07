/// Which dashboard panels follow the viewer's context, and what a panel that is not drawn says.
/// Pure domain: no Falco, no HTML, no HTTP. `DashboardTypes.PanelVisibility.apply` draws the
/// outcome; this file only decides it.
///
/// Every panel is in exactly one of three states for any set of facts: `Shown`, `Hidden` (it
/// leaves a one-line reason where it would have been, and the next step when there is one) or
/// `Quiet` (it has nothing to report, which is also a decision a test can read).
module SageFs.Server.DashboardPanelContext

open System
open SageFs

/// The routes a panel's next step posts to. One literal each, so the endpoint list and the
/// button that posts to it cannot name different paths.
module DashboardRoutes =
  let [<Literal>] LiveTestingEnable = "/dashboard/live-testing/enable"
  let [<Literal>] LiveTestingDisable = "/dashboard/live-testing/disable"

/// Every panel that follows the viewer's context.
[<RequireQualifiedAccess>]
type ContextPanel =
  | HotReload
  | LiveTesting
  /// The cohort panel and its lanes, together.
  | Cohort
  | Friction
  | Diagnostics
  | FailureNarratives
  | Alarms

module ContextPanel =
  /// One entry per case. `decisionsCoverEveryPanel` pins that this list has no gap.
  let all : ContextPanel list =
    [ ContextPanel.HotReload
      ContextPanel.LiveTesting
      ContextPanel.Cohort
      ContextPanel.Friction
      ContextPanel.Diagnostics
      ContextPanel.FailureNarratives
      ContextPanel.Alarms ]

  /// The name a sentence uses for the panel.
  let sentenceName (panel: ContextPanel) : string =
    match panel with
    | ContextPanel.HotReload -> "Hot reload"
    | ContextPanel.LiveTesting -> "Live testing"
    | ContextPanel.Cohort -> "Cohort"
    | ContextPanel.Friction -> "Friction reports"
    | ContextPanel.Diagnostics -> "Diagnostics"
    | ContextPanel.FailureNarratives -> "Failing tests"
    | ContextPanel.Alarms -> "Alarms"

/// What the viewed session is doing, as far as panel visibility cares.
[<RequireQualifiedAccess>]
type ViewedSession =
  | NoSession
  | Viewing of workflow: WorkflowTypes.SessionWorkflow * liveTesting: Features.LiveTestActivity.LiveTestActivity

/// Whether a cohort is running right now: members actually present, never just rows sitting in
/// the ledger. A cohort with nobody present can still hold history another agent can trip on:
/// a claim whose holder left waits for a conductor to reassign it, and a landing stays on record.
[<RequireQualifiedAccess>]
type CohortPresence =
  | NoActiveMembers
  | OnlyHistory of orphanedClaims: int * landings: int
  | ActiveMembers of count: int

module CohortPresence =
  let plural(n: int) (one: string) (many: string) : string =
    match n with
    | 1 -> sprintf "1 %s" one
    | n -> sprintf "%d %s" n many

  /// What the cohort panel's header says about a cohort with nobody present.
  let historyLine (orphanedClaims: int) (landings: int) : string =
    let remains =
      [ match orphanedClaims with
        | 0 -> ()
        | n -> yield sprintf "%s %s" (plural n "orphaned claim" "orphaned claims") (match n with 1 -> "remains" | _ -> "remain")
        match landings with
        | 0 -> ()
        | n -> yield sprintf "%s on record" (plural n "landing" "landings") ]
    match remains with
    | [] -> "no cohort is running"
    | parts -> sprintf "no cohort is running; %s" (String.Join(", ", parts))

/// The friction panel is off by default until reporting has a real endpoint.
/// A tab can still ask for it, deliberately, with `/dashboard?panels=friction`.
[<RequireQualifiedAccess>]
type FrictionPanelOptIn =
  | NotOptedIn
  | OptedIn

/// Whether the viewed session's code has been checked, and what the check found.
[<RequireQualifiedAccess>]
type DiagnosticsCheck =
  | NotChecked
  | Checked of count: int

type PanelFacts = {
  Viewed: ViewedSession
  Cohort: CohortPresence
  Friction: FrictionPanelOptIn
  Diagnostics: DiagnosticsCheck
  /// System alarms raised and not dismissed.
  Alarms: int
  /// Tests failing in the viewed session.
  FailingTests: int
}

/// One case per sentence the user can read.
[<RequireQualifiedAccess>]
type HiddenBecause =
  | NoSessionSelected of panel: ContextPanel
  | SessionInReplMode
  | SessionInLiveTestingMode
  | LiveTestingOff
  | NoCohortRunning
  | NotCheckedYet of what: string

/// What a person can do about a hidden panel.
[<RequireQualifiedAccess>]
type NextStep =
  | NoNextStep
  /// A button that turns the thing on: the label and the route it posts to.
  | PostAction of label: string * route: string
  /// A control that already exists elsewhere on the page, named.
  | UseControl of controlName: string

/// Why a panel with nothing to report draws nothing.
[<RequireQualifiedAccess>]
type QuietBecause =
  | NoAlarms
  | NoFailingTests
  | NoDiagnostics
  | FrictionNotRequested
  | NoTestsDiscovered
  | NoCoverageYet

[<RequireQualifiedAccess>]
type PanelPresence =
  | Shown
  | Hidden of because: HiddenBecause * next: NextStep
  /// Nothing to report. Renders nothing, and is still a decision.
  | Quiet of because: QuietBecause

/// Why a panel that is on the page has nothing in it yet. A panel that says "no active session" while a session is open and
/// its worker has not answered tells a lie, so each reason is a case with its own sentence.
[<RequireQualifiedAccess>]
type EmptyBecause =
  | NoSessionInView
  | HotReloadStateUnanswered
  | WarmupNotReported

module EmptyBecause =
  let text (because: EmptyBecause) : string =
    match because with
    | EmptyBecause.NoSessionInView -> "No session is selected."
    | EmptyBecause.HotReloadStateUnanswered -> "The session's worker did not report its hot reload state, so no file list is shown yet."
    | EmptyBecause.WarmupNotReported -> "This session has not reported its warmup yet. Opened namespaces and files appear once it has."

module HiddenBecause =
  /// The sentence. Exhaustive, so a new reason cannot be added without writing it.
  let text (because: HiddenBecause) : string =
    match because with
    | HiddenBecause.NoSessionSelected panel -> sprintf "%s: no session is selected" (ContextPanel.sentenceName panel)
    | HiddenBecause.SessionInReplMode ->
      "Hot reload is off: this is a REPL session. Choose Mode: Hot Reload in the top bar to patch a running app on save."
    | HiddenBecause.SessionInLiveTestingMode -> "Hot reload: this session is in Live Testing mode"
    | HiddenBecause.LiveTestingOff -> "Live testing is off for this session, so no tests re-run as you edit. Choose Mode: Live Testing in the top bar to turn it on."
    | HiddenBecause.NoCohortRunning ->
      "No cohort is running (a cohort is several agents working on one project together)."
    | HiddenBecause.NotCheckedYet what -> sprintf "Diagnostics: %s have not been checked yet." what

module QuietBecause =
  let text (because: QuietBecause) : string =
    match because with
    | QuietBecause.NoAlarms -> "no alarms"
    | QuietBecause.NoFailingTests -> "no failing tests"
    | QuietBecause.NoDiagnostics -> "no diagnostics"
    | QuietBecause.FrictionNotRequested -> "friction reports are off until a tab asks for them"
    | QuietBecause.NoTestsDiscovered -> "no tests discovered"
    | QuietBecause.NoCoverageYet -> "no coverage yet"

/// Why a part of the cohort panel that is on the page has nothing in it. The cohort is running, so a missing lane view, trunk
/// list or scrubber needs a word, or it reads as a broken panel.
[<RequireQualifiedAccess>]
type CohortEmptyBecause =
  | NoLaneActivity
  | NoLandingFollowed
  | EmptyLedger

module CohortEmptyBecause =
  let text (because: CohortEmptyBecause) : string =
    match because with
    | CohortEmptyBecause.NoLaneActivity -> "Lanes: no claim or landing has happened yet."
    | CohortEmptyBecause.NoLandingFollowed -> "Trunk: no landing has been followed yet."
    | CohortEmptyBecause.EmptyLedger -> "Scrubber: the ledger is empty, so there is nothing to scrub."

module PanelFacts =
  /// The query key and value that opt a tab into the friction panel.
  let panelsQueryKey = "panels"
  let frictionPanelValue = "friction"

  /// `?panels=friction` (comma-separated, case-insensitive) opts in.
  let frictionOptInOfQuery (panels: string) : FrictionPanelOptIn =
    let requested =
      (if isNull panels then "" else panels).Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.exists (fun p -> String.Equals(p, frictionPanelValue, StringComparison.OrdinalIgnoreCase))
    match requested with
    | true -> FrictionPanelOptIn.OptedIn
    | false -> FrictionPanelOptIn.NotOptedIn

  let cohortPresence (frame: SageFs.Cohort.CohortFrame<'m>) : CohortPresence =
    let present =
      frame.MemberSeat
      |> Array.filter (fun seat ->
        match seat with
        | SageFs.Cohort.SeatState.Present -> true
        | SageFs.Cohort.SeatState.Departed _ -> false)
      |> Array.length
    let orphaned =
      frame.ClaimState
      |> Array.filter (fun state ->
        match state with
        | SageFs.Cohort.ClaimState.Orphaned _ -> true
        | SageFs.Cohort.ClaimState.Held _
        | SageFs.Cohort.ClaimState.Released _ -> false)
      |> Array.length
    let landings = frame.LandingIds.Length
    match present, orphaned, landings with
    | 0, 0, 0 -> CohortPresence.NoActiveMembers
    | 0, o, l -> CohortPresence.OnlyHistory(o, l)
    | n, _, _ -> CohortPresence.ActiveMembers n

module PanelContext =
  let useWorkflowSwitcher= NextStep.UseControl "the workflow switcher in the header"

  /// The friction panel's decision depends on one fact. Taken on its own so the snapshot builder can decide
  /// before it builds, and skip the panel's SQLite read for a tab that did not ask for it.
  let frictionPresence (optIn: FrictionPanelOptIn) : PanelPresence =
    match optIn with
    | FrictionPanelOptIn.OptedIn -> PanelPresence.Shown
    | FrictionPanelOptIn.NotOptedIn -> PanelPresence.Quiet QuietBecause.FrictionNotRequested

  /// A session card's test grid: there is a grid once a test has been discovered.
  let testGrid (entries: int) : PanelPresence =
    match entries with
    | 0 -> PanelPresence.Quiet QuietBecause.NoTestsDiscovered
    | _ -> PanelPresence.Shown

  /// A session card's coverage map: there is a map once the instrumented build reports a probe.
  let coverageMap (probes: int option) : PanelPresence =
    match probes with
    | None
    | Some 0 -> PanelPresence.Quiet QuietBecause.NoCoverageYet
    | Some _ -> PanelPresence.Shown

  let decide (facts: PanelFacts) (panel: ContextPanel) : PanelPresence =
    match panel with
    | ContextPanel.HotReload ->
      match facts.Viewed with
      | ViewedSession.NoSession -> PanelPresence.Hidden(HiddenBecause.NoSessionSelected panel, NextStep.NoNextStep)
      | ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.HotReload _, _) -> PanelPresence.Shown
      | ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.Interactive, _) ->
        PanelPresence.Hidden(HiddenBecause.SessionInReplMode, useWorkflowSwitcher)
      | ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.LiveTesting, _) ->
        PanelPresence.Hidden(HiddenBecause.SessionInLiveTestingMode, useWorkflowSwitcher)
    | ContextPanel.LiveTesting ->
      match facts.Viewed with
      | ViewedSession.NoSession -> PanelPresence.Hidden(HiddenBecause.NoSessionSelected panel, NextStep.NoNextStep)
      | ViewedSession.Viewing(_, Features.LiveTestActivity.LiveTestActivity.Off) ->
        PanelPresence.Hidden(HiddenBecause.LiveTestingOff, NextStep.PostAction("Turn on", DashboardRoutes.LiveTestingEnable))
      | ViewedSession.Viewing _ -> PanelPresence.Shown
    | ContextPanel.Cohort ->
      match facts.Cohort with
      | CohortPresence.ActiveMembers _
      | CohortPresence.OnlyHistory _ -> PanelPresence.Shown
      | CohortPresence.NoActiveMembers -> PanelPresence.Hidden(HiddenBecause.NoCohortRunning, NextStep.NoNextStep)
    | ContextPanel.Friction -> frictionPresence facts.Friction
    | ContextPanel.Diagnostics ->
      match facts.Diagnostics with
      | DiagnosticsCheck.NotChecked -> PanelPresence.Hidden(HiddenBecause.NotCheckedYet "diagnostics", NextStep.NoNextStep)
      | DiagnosticsCheck.Checked 0 -> PanelPresence.Quiet QuietBecause.NoDiagnostics
      | DiagnosticsCheck.Checked _ -> PanelPresence.Shown
    | ContextPanel.FailureNarratives ->
      match facts.FailingTests with
      | 0 -> PanelPresence.Quiet QuietBecause.NoFailingTests
      | _ -> PanelPresence.Shown
    | ContextPanel.Alarms ->
      match facts.Alarms with
      | 0 -> PanelPresence.Quiet QuietBecause.NoAlarms
      | _ -> PanelPresence.Shown

  /// The one list every other piece reads: the renderer, the tests, and later the View menu and the command palette.
  let decisions (facts: PanelFacts) : (ContextPanel * PanelPresence) list =
    ContextPanel.all |> List.map (fun panel -> panel, decide facts panel)
