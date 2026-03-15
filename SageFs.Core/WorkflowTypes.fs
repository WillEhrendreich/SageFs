/// Session workflow model — encodes the hot-reload / REPL tradeoff structurally.
///
/// The core constraint (CLR-level, non-negotiable):
///   Hot reload requires Harmony to detour JIT-compiled methods.
///   Harmony requires --multiemit- (single-assembly FSI mode).
///   Single-assembly mode prevents type redefinition in the REPL.
///
/// This module models that constraint as a discriminated union so that
/// illegal states (hot reload + full REPL) are unrepresentable.
module SageFs.WorkflowTypes

// ─── Browser refresh configuration ──────────────────────────

/// Configuration for the browser hot-reload pipeline.
/// Only meaningful when the workflow uses SaveDriven feedback.
type BrowserRefreshConfig = {
  /// File patterns that trigger a browser refresh on save.
  WatchPatterns: string list
}

module BrowserRefreshConfig =
  let defaults = { WatchPatterns = [ "*.fs"; "*.fsx" ] }

// ─── Feedback strategy ──────────────────────────────────────

/// How the user wants to see their changes reflected.
/// Determined at session creation — controls FSI compiler flags.
[<RequireQualifiedAccess>]
type FeedbackStrategy =
  /// Full REPL: type redefinition, interactive exploration.
  /// FSI runs with default flags (multi-emit enabled).
  | ReplDriven
  /// Hot reload: save → #load → Harmony patch → SSE refresh.
  /// FSI runs with --multiemit- (single assembly mode).
  | SaveDriven of BrowserRefreshConfig

// ─── REPL capability ────────────────────────────────────────

/// What the REPL can do — derived from FeedbackStrategy, never set independently.
/// This is a consequence of the CLR constraint, not a user choice.
[<RequireQualifiedAccess>]
type ReplCapability =
  /// Type/module redefinition, expression eval, everything.
  | Full
  /// Expression eval, function calls — no type/module redefinition.
  | ExpressionOnly

module ReplCapability =
  let label = function
    | ReplCapability.Full -> "Full"
    | ReplCapability.ExpressionOnly -> "ExpressionOnly"

// ─── Session workflow (the main DU) ─────────────────────────

/// The session workflow — what the user chose at session creation.
/// Encodes the hot-reload/REPL tradeoff structurally:
/// you cannot construct "hot reload + full REPL" because there is no DU case for it.
[<RequireQualifiedAccess>]
type SessionWorkflow =
  /// Full REPL, no hot reload. The "exploring and prototyping" workflow.
  | Interactive
  /// Hot reload active, restricted REPL. The "building a web app" workflow.
  | WebLive of BrowserRefreshConfig

module SessionWorkflow =

  /// Derive the feedback strategy from the workflow.
  let feedbackStrategy = function
    | SessionWorkflow.Interactive  -> FeedbackStrategy.ReplDriven
    | SessionWorkflow.WebLive cfg  -> FeedbackStrategy.SaveDriven cfg

  /// Derive what the REPL can do — total function, no ambiguity.
  let replCapability workflow =
    match feedbackStrategy workflow with
    | FeedbackStrategy.ReplDriven   -> ReplCapability.Full
    | FeedbackStrategy.SaveDriven _ -> ReplCapability.ExpressionOnly

  /// Derive the extra FSI args needed for this workflow.
  let fsiArgs = function
    | SessionWorkflow.Interactive -> []
    | SessionWorkflow.WebLive _   -> [ "--multiemit-" ]

  /// User-facing label — short, searchable, universal.
  let label = function
    | SessionWorkflow.Interactive -> "REPL"
    | SessionWorkflow.WebLive _   -> "Live"

  /// Whether hot reload (Harmony patching) is active.
  let isHotReloadActive = function
    | SessionWorkflow.Interactive -> false
    | SessionWorkflow.WebLive _   -> true

  /// Default workflow — full REPL, no restrictions.
  let defaultWorkflow = SessionWorkflow.Interactive

  /// Convert from the legacy bool representation.
  /// Used at the boundary where env vars are parsed.
  let fromHotReloadBool = function
    | true  -> SessionWorkflow.WebLive BrowserRefreshConfig.defaults
    | false -> SessionWorkflow.Interactive

// ─── Transition cost ────────────────────────────────────────

/// What the user will lose when switching workflows.
/// Computed before the switch happens — the UI renders this for confirmation.
type TransitionCost = {
  /// Number of REPL let-bindings that will be cleared.
  DefinitionsLost: int
  /// Number of evaluated cells that will be lost.
  CellsLost: int
  /// Estimated time for the new session to warm up.
  EstimatedRestart: System.TimeSpan
  /// Whether a standby worker is ready (near-instant switch).
  StandbyReady: bool
}

module TransitionCost =
  let zero = {
    DefinitionsLost = 0
    CellsLost = 0
    EstimatedRestart = System.TimeSpan.Zero
    StandbyReady = false
  }

  /// Zero-cost switches skip confirmation.
  /// True when there's no REPL state to lose.
  let isZeroCost (cost: TransitionCost) =
    cost.DefinitionsLost = 0 && cost.CellsLost = 0

  /// Compute transition cost from observable session state.
  let compute (evalCount: int) (cellCount: int) (standbyReady: bool) = {
    DefinitionsLost = evalCount
    CellsLost = cellCount
    EstimatedRestart =
      match standbyReady with
      | true -> System.TimeSpan.FromMilliseconds 200.0
      | false -> System.TimeSpan.FromSeconds 15.0
    StandbyReady = standbyReady
  }

// ─── Workflow switch result ─────────────────────────────────

/// Result of a switch_workflow call.
/// Always includes cost (even for no-ops and dry-runs).
type WorkflowSwitchResult = {
  /// Workflow before the switch.
  PreviousWorkflow: string
  /// Target workflow requested.
  TargetWorkflow: string
  /// Computed cost of the transition.
  Cost: TransitionCost
  /// Whether the switch was actually executed.
  Switched: bool
  /// New session ID if a switch was performed.
  NewSessionId: string option
  /// Human-readable summary of what happened.
  Message: string
}

module WorkflowSwitchResult =
  /// Create a no-op result when target = current workflow.
  let alreadyInWorkflow (workflow: SessionWorkflow) (cost: TransitionCost) = {
    PreviousWorkflow = SessionWorkflow.label workflow
    TargetWorkflow = SessionWorkflow.label workflow
    Cost = cost
    Switched = false
    NewSessionId = None
    Message =
      sprintf "Already in %s workflow — no switch needed"
        (SessionWorkflow.label workflow)
  }

  /// Create a dry-run preview result.
  let preview
    (current: SessionWorkflow)
    (target: SessionWorkflow)
    (cost: TransitionCost) = {
    PreviousWorkflow = SessionWorkflow.label current
    TargetWorkflow = SessionWorkflow.label target
    Cost = cost
    Switched = false
    NewSessionId = None
    Message =
      sprintf "Preview: switching from %s to %s would lose %d definitions and %d cells"
        (SessionWorkflow.label current)
        (SessionWorkflow.label target)
        cost.DefinitionsLost
        cost.CellsLost
  }

  /// Create a successful switch result.
  let switched
    (previous: SessionWorkflow)
    (target: SessionWorkflow)
    (cost: TransitionCost)
    (newSessionId: string) = {
    PreviousWorkflow = SessionWorkflow.label previous
    TargetWorkflow = SessionWorkflow.label target
    Cost = cost
    Switched = true
    NewSessionId = Some newSessionId
    Message =
      sprintf "Switched from %s to %s (new session: %s)"
        (SessionWorkflow.label previous)
        (SessionWorkflow.label target)
        newSessionId
  }

// ─── Workflow suggestion (project detection) ────────────────

/// Suggestion to switch workflows based on project package references.
/// Computed once at session creation — never auto-applied.
type WorkflowSuggestion = {
  /// The workflow SageFs thinks would be a good fit.
  SuggestedWorkflow: SessionWorkflow
  /// Human-readable reason for the suggestion.
  Reason: string
  /// Package references that triggered the suggestion.
  DetectedPackages: string list
}

module WorkflowDetection =

  let private datastarPackages =
    [ "Falco.Datastar"; "Starfederation.Datastar" ]

  let private webPackages =
    [ "Falco"; "Falco.Htmx"; "Giraffe"; "Saturn"
      "Microsoft.AspNetCore" ]

  let private findMatches (knownPackages: string list) (projectRefs: string list) =
    projectRefs
    |> List.filter (fun ref ->
      knownPackages |> List.exists (fun known -> ref.Contains(known)))

  /// Suggest a workflow based on project package references.
  /// Returns None for non-web projects (default to Interactive).
  /// NEVER auto-applies — the UI presents this as a one-time suggestion.
  let suggest (packageRefs: string list) : WorkflowSuggestion option =
    let datastarHits = findMatches datastarPackages packageRefs
    let webHits = findMatches webPackages packageRefs
    match datastarHits, webHits with
    | _ :: _, _ ->
      Some {
        SuggestedWorkflow =
          SessionWorkflow.WebLive BrowserRefreshConfig.defaults
        Reason =
          "Datastar project detected — Live mode enables SSE-driven DOM morphing"
        DetectedPackages = datastarHits
      }
    | [], _ :: _ ->
      Some {
        SuggestedWorkflow =
          SessionWorkflow.WebLive BrowserRefreshConfig.defaults
        Reason =
          "Web project detected — Live mode enables browser hot reload"
        DetectedPackages = webHits
      }
    | [], [] -> None

  // ── Package extraction (pure) ─────────────────────────────

  /// Package names that indicate a test project.
  let private testPackageNames =
    [ "Expecto"; "xunit"; "xunit.v3"; "NUnit"
      "MSTest.TestFramework"; "Microsoft.NET.Test.Sdk" ]

  /// True when the package list looks like a test project.
  let isTestPackageSet (packages: string list) =
    packages
    |> List.exists (fun pkg ->
      testPackageNames
      |> List.exists (fun tp ->
        pkg.StartsWith(tp, System.StringComparison.OrdinalIgnoreCase)))

  /// Extract package reference names from grouped per-project packages,
  /// filtering out test projects. Returns a distinct union of all names.
  let extractPackageNames (projectPackages: string list list) : string list =
    projectPackages
    |> List.filter (isTestPackageSet >> not)
    |> List.concat
    |> List.distinct
