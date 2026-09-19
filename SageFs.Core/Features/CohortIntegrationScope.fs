namespace SageFs.Features

open System
open System.Xml.Linq

/// Pure decision logic behind the cohort's integration-worktree bootstrap
/// (`SageFs/Mcp.fs`'s `setIntegrationRef`/`discoverProjects`) —
/// cohort-dogfood-findings.md F16, F6, F5. Three IO-adjacent decisions, each
/// provable in isolation from the filesystem/git/process effects that carry
/// them out:
///
///  - F16: WHICH directory is the "main repo" the integration worktree is
///    cut from — never blindly the daemon PROCESS's own cwd. F16 pinned
///    this precisely: an isolated daemon rooted at an unrelated cwd
///    worktreed the WRONG (huge, unrelated) repo's whole solution just
///    because that happened to be its own working directory.
///  - F6: WHICH `.fsproj` set the integration session loads — never an
///    unbounded recursive filesystem walk. The dogfood found 34 projects
///    that way, including external-toolchain samples and GUI/VS/vscode
///    projects that can never build inside the FSI session, versus the
///    repo's OWN curated, already-known-to-build `.slnx` (15 projects,
///    confirmed by dogfood: `dotnet build SageFs.slnx` exits 0).
///  - F5: a failed worktree build must NEVER be allowed to reach
///    `CreateSession` — that is what turned "34 missing DLLs" into an
///    alarming generic Faulted session instead of an immediate, honest,
///    actionable build-failure reason.
module CohortIntegrationScope =

  // ── F16 — where does the main-repo root candidate come from? ───────────

  /// The caller's own (conductor's) active session working directory
  /// ALWAYS wins over the daemon process's cwd when one is resolvable: the
  /// daemon's cwd is just whatever directory it happened to be launched
  /// from and has no necessary relationship to the repo a cohort is
  /// landing changes for.
  [<RequireQualifiedAccess>]
  type MainRepoRootSource =
    | CallerSession of workingDirectory: string
    | DaemonProcessCwd of cwd: string

  /// Pure priority decision — no IO. `callerSessionWorkingDirectory` is
  /// `None` when the calling agent has no resolvable session (e.g. it
  /// called `set_integration_ref` from a bare MCP connection with no
  /// session created yet); only then does the daemon's own cwd apply. A
  /// blank/whitespace-only directory is treated the same as `None` — never
  /// trusted as a real path.
  let chooseMainRepoRootSource
    (callerSessionWorkingDirectory: string option)
    (daemonCwd: string)
    : MainRepoRootSource =
    match callerSessionWorkingDirectory with
    | Some dir when not (String.IsNullOrWhiteSpace dir) -> MainRepoRootSource.CallerSession dir
    | _ -> MainRepoRootSource.DaemonProcessCwd daemonCwd

  /// The candidate directory a source names. The caller applies the actual
  /// git-root walk (`SageFs.Checkout.classify`) to this — that is
  /// filesystem IO and stays at the Mcp.fs edge, not here.
  let candidateDirectory = function
    | MainRepoRootSource.CallerSession dir -> dir
    | MainRepoRootSource.DaemonProcessCwd dir -> dir

  // ── F6 — narrow the integration session's project set ──────────────────

  /// Parse a `.slnx` file's XML TEXT for its declared `.fsproj` project
  /// paths (relative, exactly as written — no filesystem touched here).
  /// Total: malformed/non-XML input yields `[]`, never throws, so a caller
  /// can treat `[]` as "no solution-declared set" and fall back safely.
  let parseSlnxProjectPaths (xmlContent: string) : string list =
    try
      let doc = XDocument.Parse xmlContent
      doc.Descendants(XName.Get "Project")
      |> Seq.choose (fun el ->
        match el.Attribute(XName.Get "Path") with
        | null -> None
        | attr -> Some attr.Value)
      |> Seq.filter (fun p -> p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
      |> Seq.toList
    with _ -> []

  /// The project set to load into the cohort's integration session.
  /// NO-EMPTY-ESCAPE (mirrors `AffectedTests`'s doctrine): when the
  /// worktree carries no `.slnx`, or its `.slnx` declares nothing, fall
  /// back to the unbounded directory walk rather than silently starting an
  /// integration session with ZERO projects. Narrowing must never risk
  /// missing a project a landing needs — it only avoids pulling in MORE
  /// than the repo's own solution already commits to building.
  let selectIntegrationProjects
    (solutionDeclaredFsprojPaths: string list)
    (directoryWalkedFsprojPaths: string list)
    : string list =
    match solutionDeclaredFsprojPaths with
    | [] -> directoryWalkedFsprojPaths
    | declared -> declared

  // ── F5 — a failed worktree build must never reach CreateSession ────────

  /// What the integration binding becomes after the worktree build step,
  /// given the build's outcome. A failed build is a DEAD END here — the
  /// caller must record `IntegrationSession.Failed reason` and return,
  /// never call `CreateSession`. This is what turns a generic, alarming
  /// "Faulted" warmup (F5's "34 missing DLLs" symptom) into an immediate,
  /// honest, actionable build-failure reason, fail-fast, never hanging.
  [<RequireQualifiedAccess>]
  type WorktreeBuildOutcome =
    | ReadyForSession
    | BuildFailed of reason: string

  let worktreeBuildOutcome (buildResult: Result<string, string>) : WorktreeBuildOutcome =
    match buildResult with
    | Ok _ -> WorktreeBuildOutcome.ReadyForSession
    | Error reason -> WorktreeBuildOutcome.BuildFailed reason
