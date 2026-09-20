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

open System

// ─── Web markers — the single source of truth ───────────────

/// Every token that means "this project serves HTTP", in ONE list.
///
/// Why one list: this used to be two — `ProjectKind.classify`'s private
/// `webPackages` and `WorkflowDetection.suggest`'s own private near-copy. Two
/// lists that had to agree, with nothing forcing them to, and they had already
/// drifted: `StarFederation.Datastar` existed only in the detection copy (and
/// was misspelled `Starfederation.Datastar`, so with the ordinal, case-SENSITIVE
/// `String.Contains` it never matched the real package id
/// `StarFederation.Datastar.FSharp` either). Both paths now read this module,
/// and `ProjectClassificationTests` iterates `all` so a marker added here is
/// automatically required to behave identically on both paths.
module WebMarkers =

  /// SSE/hypermedia packages. These are web markers like any other AND they
  /// additionally change the WORDING of the workflow suggestion, which is the
  /// only reason they are named separately.
  let datastar = [ "Falco.Datastar"; "StarFederation.Datastar" ]

  /// F# / ASP.NET Core server-side web frameworks.
  ///
  /// `Microsoft.AspNetCore` covers both an explicit `Microsoft.AspNetCore.*`
  /// package AND the `Microsoft.AspNetCore.App` FrameworkReference marker that
  /// `ProjectFileMarkers` contributes, by substring.
  let serverFrameworks =
    [ "Falco"; "Giraffe"; "Saturn"; "Oxpecker"; "Microsoft.AspNetCore" ]

  /// Markers that come from the `.fsproj` XML rather than any package — see
  /// `ProjectFileMarkers`. A modern ASP.NET Core / Minimal API project reaches
  /// ASP.NET through the Web SDK and a FrameworkReference and carries no web
  /// `<PackageReference>` at all, so without these it classified as Console.
  let projectFile = [ "Microsoft.NET.Sdk.Web" ]

  /// The whole vocabulary. Order is irrelevant — matching is substring.
  let all = serverFrameworks @ datastar @ projectFile |> List.distinct

  /// Substring match, case-INSENSITIVE. Package ids are not case-normalised by
  /// NuGet in a way anyone should depend on, and the case-sensitive version of
  /// this check is exactly what silently broke the Datastar marker.
  let private containsCI (needle: string) (haystack: string) =
    haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)

  /// The references that matched any of `markers`.
  let findMatches (markers: string list) (refs: string list) =
    refs |> List.filter (fun r -> markers |> List.exists (fun m -> containsCI m r))

  /// Whether any reference matched any of `markers`.
  let matchesAny (markers: string list) (refs: string list) =
    refs |> List.exists (fun r -> markers |> List.exists (fun m -> containsCI m r))

// ─── Project-file markers ───────────────────────────────────

/// Classification markers that live in the `.fsproj` XML itself rather than in
/// any `<PackageReference>`: the `Sdk` attribute on `<Project>`, and
/// `<FrameworkReference Include="..." />`.
///
/// This exists because package references alone cannot see a plain ASP.NET Core
/// or Minimal API project. Verified against this repo's own
/// `SageFs.Tests/fixtures/WebAppFixture/WebAppFixture.fsproj`: `Sdk =
/// "Microsoft.NET.Sdk.Web"`, zero `<PackageReference>` elements, and the live
/// daemon reports it as `PackageRefs: []`. Every such project — plain ASP.NET,
/// Minimal API, Oxpecker, Giraffe-via-framework-ref — classified as Console and
/// was therefore never offered the hot-reload workflow.
///
/// Markers are raw strings so they compose with package references in the
/// single `string list` both classification paths already take — the same
/// pattern `ProjectLoading.activeUiPropertyMarkers` uses for `UseWPF`.
module ProjectFileMarkers =

  open System.Xml.Linq

  /// Parse markers out of raw `.fsproj` XML. Pure — no IO.
  ///
  /// Best-effort by construction: malformed or empty XML yields `[]` rather
  /// than throwing. A marker we cannot read costs at most a workflow
  /// suggestion; it must never cost the session.
  let parse (fsprojXml: string) : string list =
    match String.IsNullOrWhiteSpace fsprojXml with
    | true -> []
    | false ->
      try
        let doc = XDocument.Parse fsprojXml
        let sdkAttr =
          doc.Root
          |> Option.ofObj
          |> Option.bind (fun root -> root.Attribute(XName.Get "Sdk") |> Option.ofObj)
          |> Option.map (fun a -> a.Value.Trim())
          |> Option.filter (fun v -> v <> "")
          |> Option.toList
        let frameworkRefs =
          doc.Descendants(XName.Get "FrameworkReference")
          |> Seq.choose (fun el ->
            el.Attribute(XName.Get "Include")
            |> Option.ofObj
            |> Option.map (fun a -> a.Value.Trim()))
          |> Seq.filter (fun v -> v <> "")
          |> Seq.toList
        sdkAttr @ frameworkRefs |> List.distinct
      with _ -> []

  /// The one IO edge. Any failure to read the file yields `[]`, never an
  /// exception — see `parse`.
  let read (projPath: string) : string list =
    try
      parse (IO.File.ReadAllText projPath)
    with _ -> []

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

  /// Native game AND desktop-UI markers — both run a native window with a
  /// render/event loop and no WebApplication. Some desktop frameworks are
  /// enabled by an MSBuild PROPERTY, not a package (WPF/WinForms have no
  /// package at all — see ProjectLoading.classifyProject, which surfaces the
  /// active `Use*` properties as classification markers).
  let private nativeGuiPackages =
    [ "Raylib"; "SDL2"; "Silk.NET"; "MonoGame"; "SFML"          // game packages
      "Avalonia"; "Microsoft.Maui"; "Microsoft.WindowsAppSDK"   // desktop-UI packages
      "Microsoft.WinUI"; "Uno.UI"; "Uno.WinUI"
      "UseWPF"; "UseWindowsForms"; "UseMaui"; "UseWinUI" ]       // desktop-UI MSBuild property markers

  /// Classify a project by its package references. NativeGui wins over Web wins
  /// over Console: a native game/desktop-UI library dominates the runtime shape,
  /// then a web framework, else a plain console/headless app.
  ///
  /// WPF is a Windows framework feature enabled by `<UseWPF>`, not a package, so
  /// it is not detected here and lands in Console. That is harmless and not a
  /// regression: the web DevReload patch is inert for a WPF app (it never calls
  /// WebApplication.Run) and reload still works through the method detour — so
  /// existing Windows/WPF users are unaffected.
  /// `packageRefs` is the project's package references PLUS any non-package
  /// classification markers the loader surfaced for it — `UseWPF` and friends
  /// from `ProjectLoading.activeUiPropertyMarkers`, and the Web SDK /
  /// FrameworkReference markers from `ProjectFileMarkers`.
  let classify (packageRefs: string list) : ProjectKind =
    let hasNativeGui =
      packageRefs |> List.exists (fun ref -> nativeGuiPackages |> List.exists ref.Contains)
    if hasNativeGui then ProjectKind.NativeGui
    elif WebMarkers.matchesAny WebMarkers.all packageRefs then
      ProjectKind.Web BrowserRefreshConfig.defaults
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
  /// Full REPL, no hot reload, no test-on-save. The "exploring and
  /// prototyping" workflow.
  | Interactive
  /// Full REPL, no hot reload, affected tests re-run on debounced keystrokes
  /// (as you type — the editor streams buffer changes, it is NOT save-driven).
  /// The "TDD as you type" workflow. Keeps the full REPL because running tests
  /// never patches the running app, so the --multiemit- CLR constraint does
  /// not apply.
  | LiveTesting
  /// Hot reload active, restricted REPL. The "building an app" workflow.
  | HotReload of BrowserRefreshConfig

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
  /// Installed for a web app, AND for the method-detour (console) case. The
  /// console case is now a genuine belt-and-braces rather than a workaround:
  /// `ProjectFileMarkers` DOES surface the Web SDK attribute and the ASP.NET
  /// FrameworkReference, so a plain ASP.NET / Minimal API project classifies as
  /// `Web` on its own merits. It is still installed for `MethodDetourOnly`
  /// because the patch is inert for a genuine console app — it never calls
  /// WebApplication.Run — so a project that reaches ASP.NET by some route we
  /// have not enumerated still gets browser reload, and nothing else pays for
  /// it. A native game is the one kind we are certain has no WebApplication, so
  /// it — and non-reloading Interactive — skip it.
  let installsWebDevReload = function
    | ReloadStrategy.WebReload _      -> true
    | ReloadStrategy.MethodDetourOnly -> true
    | ReloadStrategy.NativeGuiReload  -> false
    | ReloadStrategy.NoReload         -> false

module SessionWorkflow =

  /// Derive the feedback strategy from the workflow.
  let feedbackStrategy = function
    | SessionWorkflow.Interactive  -> FeedbackStrategy.ReplDriven
    | SessionWorkflow.LiveTesting  -> FeedbackStrategy.ReplDriven
    | SessionWorkflow.HotReload cfg  -> FeedbackStrategy.SaveDriven cfg

  /// Derive what the REPL can do — total function, no ambiguity.
  let replCapability workflow =
    match feedbackStrategy workflow with
    | FeedbackStrategy.ReplDriven   -> ReplCapability.Full
    | FeedbackStrategy.SaveDriven _ -> ReplCapability.ExpressionOnly

  /// Derive the extra FSI args needed for this workflow.
  let fsiArgs = function
    | SessionWorkflow.Interactive -> []
    | SessionWorkflow.LiveTesting -> []
    | SessionWorkflow.HotReload _   -> [ "--multiemit-" ]

  /// User-facing label — short, searchable, universal.
  let label = function
    | SessionWorkflow.Interactive -> "REPL"
    | SessionWorkflow.LiveTesting -> "Live Testing"
    | SessionWorkflow.HotReload _   -> "Hot Reload"

  /// Whether hot reload (Harmony patching) is active.
  let isHotReloadActive = function
    | SessionWorkflow.Interactive -> false
    | SessionWorkflow.LiveTesting -> false
    | SessionWorkflow.HotReload _   -> true

  /// Derive the actual reload strategy from the workflow AND the project kind —
  /// total, no ambiguity. Interactive never reloads. A hot-reload workflow
  /// reloads differently per kind: web gets middleware + browser refresh,
  /// console gets method-detour only, a game gets frame-loop detour.
  let reloadStrategy (workflow: SessionWorkflow) (kind: ProjectKind) : ReloadStrategy =
    match workflow with
    | SessionWorkflow.Interactive -> ReloadStrategy.NoReload
    | SessionWorkflow.LiveTesting -> ReloadStrategy.NoReload
    | SessionWorkflow.HotReload cfg ->
      match kind with
      | ProjectKind.Web _   -> ReloadStrategy.WebReload cfg
      | ProjectKind.Console -> ReloadStrategy.MethodDetourOnly
      | ProjectKind.NativeGui -> ReloadStrategy.NativeGuiReload

  /// Default workflow — full REPL, no restrictions.
  let defaultWorkflow = SessionWorkflow.Interactive

  /// Parse a user- or agent-supplied workflow string into a SessionWorkflow.
  /// Case-insensitive and alias-tolerant, so the CLI, HTTP API, and MCP tools
  /// all accept the same spellings ("hotreload"/"live"/"weblive"/"web" → hot
  /// reload; "livetesting"/"testing"/"test" → tests as you type; "interactive"/
  /// "repl" → full REPL). Unknown or empty input defaults to Interactive, the
  /// safe full-REPL mode. This is the single source of truth for the
  /// string→workflow mapping — surfaces call it instead of re-matching.
  ///
  /// Note "live" maps to hot reload for backward compatibility (the workflow
  /// was once labelled "Live"); the as-you-type testing mode is "livetesting".
  /// Parse a workflow string, returning None for anything unrecognized.
  /// The single alias table; `ofString` defaults None to Interactive, while
  /// callers that must reject an unknown target (e.g. switch_workflow) use this
  /// directly instead of writing a second parser that drifts from this one.
  let tryOfString (s: string) : SessionWorkflow option =
    match (s |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
    | "interactive" | "repl" | "normal" -> Some SessionWorkflow.Interactive
    | "hotreload" | "weblive" | "live" | "web" -> Some (SessionWorkflow.HotReload BrowserRefreshConfig.defaults)
    | "livetesting" | "live-testing" | "testing" | "test" -> Some SessionWorkflow.LiveTesting
    | _ -> None

  let ofString (s: string) : SessionWorkflow =
    tryOfString s |> Option.defaultValue SessionWorkflow.Interactive

  /// Convert from the legacy bool representation.
  /// Used at the boundary where env vars are parsed.
  let fromHotReloadBool = function
    | true  -> SessionWorkflow.HotReload BrowserRefreshConfig.defaults
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

  /// Suggest a workflow based on project package references.
  /// Returns None for non-web projects (default to Interactive).
  /// NEVER auto-applies — the UI presents this as a one-time suggestion.
  /// Reads the SAME `WebMarkers` vocabulary `ProjectKind.classify` reads, so
  /// the two can no longer disagree about what "web" means. The only thing
  /// special-cased here is Datastar, and only to change the WORDING.
  let suggest (packageRefs: string list) : WorkflowSuggestion option =
    let datastarHits = WebMarkers.findMatches WebMarkers.datastar packageRefs
    let webHits = WebMarkers.findMatches WebMarkers.all packageRefs
    match datastarHits, webHits with
    | _ :: _, _ ->
      Some {
        SuggestedWorkflow =
          SessionWorkflow.HotReload BrowserRefreshConfig.defaults
        Reason =
          "Datastar project detected — Live mode enables SSE-driven DOM morphing"
        DetectedPackages = datastarHits
      }
    | [], _ :: _ ->
      Some {
        SuggestedWorkflow =
          SessionWorkflow.HotReload BrowserRefreshConfig.defaults
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
