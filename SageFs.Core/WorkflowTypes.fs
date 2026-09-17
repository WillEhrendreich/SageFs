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

// ─── Project kind ───────────────────────────────────────────

/// What kind of runtime a session's projects are, which decides HOW hot reload
/// applies. The browser-refresh config lives on `Web` because it is only
/// meaningful there — a Console or native-GUI project structurally cannot carry a
/// browser config, so that illegal combination cannot be constructed.
[<RequireQualifiedAccess>]
type ProjectKind =
  /// A web app (ASP.NET Core / Falco / Giraffe / Saturn). Hot reload uses the
  /// DevReload middleware plus browser SSE refresh.
  | Web of BrowserRefreshConfig
  /// A console or headless app. Hot reload uses FSI method-detour only — no web
  /// machinery.
  | Console
  /// A native windowed app — a game (Raylib / SDL / Silk.NET / MonoGame) or a
  /// desktop UI (Avalonia / MAUI / WinUI / Uno / WPF). Both run a native window
  /// with a render/event loop and no WebApplication. Hot reload detours the
  /// frame/render-loop methods; no WebApplication/RunAsync patches.
  | NativeGui

module ProjectKind =

  /// Native game AND desktop-UI libraries — both run a native window with a
  /// render/event loop and no WebApplication.
  let private nativeGuiPackages =
    [ "Raylib"; "SDL2"; "Silk.NET"; "MonoGame"; "SFML"          // games
      "Avalonia"; "Microsoft.Maui"; "Microsoft.WindowsAppSDK"   // desktop UI
      "Microsoft.WinUI"; "Uno.UI"; "Uno.WinUI" ]

  /// Web frameworks whose presence means a web app.
  let private webPackages = [ "Falco"; "Giraffe"; "Saturn"; "Microsoft.AspNetCore" ]

  /// Classify a project by its package references. NativeGui wins over Web wins
  /// over Console: a native game/desktop-UI library dominates the runtime shape,
  /// then a web framework, else a plain console/headless app.
  ///
  /// WPF is a Windows framework feature enabled by `<UseWPF>`, not a package, so
  /// it is not detected here and lands in Console. That is harmless and not a
  /// regression: the web DevReload patch is inert for a WPF app (it never calls
  /// WebApplication.Run) and reload still works through the method detour — so
  /// existing Windows/WPF users are unaffected.
  let classify (packageRefs: string list) : ProjectKind =
    let has (names: string list) =
      packageRefs |> List.exists (fun ref -> names |> List.exists ref.Contains)
    if has nativeGuiPackages then ProjectKind.NativeGui
    elif has webPackages then ProjectKind.Web BrowserRefreshConfig.defaults
    else ProjectKind.Console

  /// Short user-facing label.
  let label = function
    | ProjectKind.Web _     -> "web"
    | ProjectKind.Console   -> "console"
    | ProjectKind.NativeGui -> "native-gui"

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

/// How hot reload actually applies to a running app — derived from the workflow
/// AND the project kind, never chosen directly. This is the seam that decouples
/// "hot reload is on" from "this is a web app": the same hot-reload workflow
/// produces web-middleware reload, method-detour-only, or game-loop reload
/// depending on what kind of project is loaded.
[<RequireQualifiedAccess>]
type ReloadStrategy =
  /// No hot reload (Interactive workflow).
  | NoReload
  /// Web app: FSI method-detour PLUS DevReload middleware and browser SSE refresh.
  | WebReload of BrowserRefreshConfig
  /// Console/headless: FSI method-detour only, no web machinery.
  | MethodDetourOnly
  /// Native GUI (game or desktop UI): frame/render-loop method-detour, no
  /// WebApplication/RunAsync patches.
  | NativeGuiReload

module ReloadStrategy =

  /// Whether to install the web DevReload middleware (the WebApplication.Run/
  /// RunAsync Harmony patch plus browser SSE refresh).
  ///
  /// Installed for a web app, AND for the method-detour (console) case — because
  /// a plain ASP.NET app that references the AspNetCore FRAMEWORK rather than a
  /// web package cannot be told apart from a console app by package refs alone,
  /// and the patch is inert for a genuine console app (it never calls
  /// WebApplication.Run), so installing it defensively is correct and never a
  /// regression. A native game is the one kind we are certain has no
  /// WebApplication, so it — and non-reloading Interactive — skip it.
  /// (A precise console-vs-framework-web split would need per-project framework
  /// references, which the loader does not surface yet.)
  let installsWebDevReload = function
    | ReloadStrategy.WebReload _      -> true
    | ReloadStrategy.MethodDetourOnly -> true
    | ReloadStrategy.NativeGuiReload  -> false
    | ReloadStrategy.NoReload         -> false

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

  /// Derive the actual reload strategy from the workflow AND the project kind —
  /// total, no ambiguity. Interactive never reloads. A hot-reload workflow
  /// reloads differently per kind: web gets middleware + browser refresh,
  /// console gets method-detour only, a game gets frame-loop detour.
  let reloadStrategy (workflow: SessionWorkflow) (kind: ProjectKind) : ReloadStrategy =
    match workflow with
    | SessionWorkflow.Interactive -> ReloadStrategy.NoReload
    | SessionWorkflow.WebLive cfg ->
      match kind with
      | ProjectKind.Web _   -> ReloadStrategy.WebReload cfg
      | ProjectKind.Console -> ReloadStrategy.MethodDetourOnly
      | ProjectKind.NativeGui -> ReloadStrategy.NativeGuiReload

  /// Default workflow — full REPL, no restrictions.
  let defaultWorkflow = SessionWorkflow.Interactive

  /// Parse a user- or agent-supplied workflow string into a SessionWorkflow.
  /// Case-insensitive and alias-tolerant, so the CLI, HTTP API, and MCP tools
  /// all accept the same spellings ("live"/"weblive"/"web" → hot reload;
  /// "interactive"/"repl" → full REPL). Unknown or empty input defaults to
  /// Interactive, the safe full-REPL mode. This is the single source of truth
  /// for the string→workflow mapping — surfaces call it instead of re-matching.
  let ofString (s: string) : SessionWorkflow =
    match (s |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
    | "weblive" | "live" | "web" -> SessionWorkflow.WebLive BrowserRefreshConfig.defaults
    | _ -> SessionWorkflow.Interactive

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
}

module TransitionCost =
  let zero = {
    DefinitionsLost = 0
    CellsLost = 0
    EstimatedRestart = System.TimeSpan.Zero
  }

  /// Zero-cost switches skip confirmation.
  /// True when there's no REPL state to lose.
  let isZeroCost (cost: TransitionCost) =
    cost.DefinitionsLost = 0 && cost.CellsLost = 0

  /// Compute transition cost from observable session state.
  /// Every switch spawns a fresh session, so restart always reflects
  /// the cold-start estimate — there is no standby pool anymore.
  let compute (evalCount: int) (cellCount: int) = {
    DefinitionsLost = evalCount
    CellsLost = cellCount
    EstimatedRestart = System.TimeSpan.FromSeconds 15.0
  }

// ─── Workflow switch outcome ────────────────────────────────

/// Outcome of a switch_workflow call — makes impossible states unrepresentable.
///
/// The old record type allowed `Switched=true, NewSessionId=None` (switched
/// but no session?) and `Switched=false, NewSessionId=Some x` (didn't switch
/// but got a session?). This DU eliminates those impossible states:
/// - AlreadyActive structurally cannot carry a sessionId
/// - DryRunPreview structurally cannot carry a sessionId
/// - Executed always carries a sessionId (non-optional)
[<RequireQualifiedAccess>]
type WorkflowSwitchOutcome =
  /// Target = current workflow — nothing happened, zero side effects.
  | AlreadyActive of cost: TransitionCost * message: string
  /// Dry-run preview — shows cost without executing.
  | DryRunPreview of cost: TransitionCost * message: string
  /// Switch executed — new session created, old session stopped.
  | Executed of
      previous: SessionWorkflow *
      target: SessionWorkflow *
      cost: TransitionCost *
      sessionId: string *
      message: string

module WorkflowSwitchOutcome =

  /// Create a no-op outcome when target = current workflow.
  let alreadyInWorkflow (workflow: SessionWorkflow) (cost: TransitionCost) =
    WorkflowSwitchOutcome.AlreadyActive (
      cost,
      sprintf "Already in %s workflow — no switch needed"
        (SessionWorkflow.label workflow))

  /// Create a dry-run preview outcome.
  let preview
    (current: SessionWorkflow)
    (target: SessionWorkflow)
    (cost: TransitionCost) =
    WorkflowSwitchOutcome.DryRunPreview (
      cost,
      sprintf "Preview: switching from %s to %s would lose %d definitions and %d cells"
        (SessionWorkflow.label current)
        (SessionWorkflow.label target)
        cost.DefinitionsLost
        cost.CellsLost)

  /// Create a successful switch outcome.
  let switched
    (previous: SessionWorkflow)
    (target: SessionWorkflow)
    (cost: TransitionCost)
    (newSessionId: string) =
    WorkflowSwitchOutcome.Executed (
      previous, target, cost, newSessionId,
      sprintf "Switched from %s to %s (new session: %s)"
        (SessionWorkflow.label previous)
        (SessionWorkflow.label target)
        newSessionId)

  /// Extract cost from any outcome.
  let cost = function
    | WorkflowSwitchOutcome.AlreadyActive (c, _) -> c
    | WorkflowSwitchOutcome.DryRunPreview (c, _) -> c
    | WorkflowSwitchOutcome.Executed (_, _, c, _, _) -> c

  /// Extract human-readable message from any outcome.
  let message = function
    | WorkflowSwitchOutcome.AlreadyActive (_, m) -> m
    | WorkflowSwitchOutcome.DryRunPreview (_, m) -> m
    | WorkflowSwitchOutcome.Executed (_, _, _, _, m) -> m

  /// Extract session ID (only present for Executed outcomes).
  let sessionId = function
    | WorkflowSwitchOutcome.Executed (_, _, _, sid, _) -> Some sid
    | _ -> None

  /// Whether the outcome represents an actual switch execution.
  let wasExecuted = function
    | WorkflowSwitchOutcome.Executed _ -> true
    | _ -> false

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
