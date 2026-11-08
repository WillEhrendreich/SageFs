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
    /// The directory the call itself named (`working_directory`). It outranks everything: it is the repository the caller
    /// means, and the daemon's own directory (install-local starts it in the install folder) says nothing about it.
    | NamedByCall of workingDirectory: string
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

  /// Which directory `set_integration_ref` cuts the integration worktree from: the one the call named, else the caller's
  /// session, else the daemon's own. A blank name is no name.
  let chooseIntegrationRepoSource
    (named: string option)
    (callerSessionWorkingDirectory: string option)
    (daemonCwd: string)
    : MainRepoRootSource =
    match named with
    | Some dir when not (String.IsNullOrWhiteSpace dir) -> MainRepoRootSource.NamedByCall dir
    | _ -> chooseMainRepoRootSource callerSessionWorkingDirectory daemonCwd

  /// The candidate directory a source names. The caller applies the actual
  /// git-root walk (`SageFs.Checkout.classify`) to this — that is
  /// filesystem IO and stays at the Mcp.fs edge, not here.
  let candidateDirectory = function
    | MainRepoRootSource.NamedByCall dir -> dir
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

  // ── P11 — which cohort the integration belongs to, and what a retry does ──

  /// Which cohort a configured integration serves, against the cohort a call acts in. The daemon holds ONE integration
  /// binding (one integration worktree, one trunk checkout), configured for the cohort of the repository named when
  /// `set_integration_ref` ran. A landing or a status read in any other cohort must not be answered from it.
  [<RequireQualifiedAccess>]
  type BindingFit<'scope> =
    | NotConfigured
    | ForThisCohort
    | ConfiguredForOther of configuredFor: 'scope

  let bindingFit<'scope when 'scope : equality> (configuredFor: 'scope option) (asked: 'scope) : BindingFit<'scope> =
    match configuredFor with
    | None -> BindingFit.NotConfigured
    | Some configured when configured = asked -> BindingFit.ForThisCohort
    | Some configured -> BindingFit.ConfiguredForOther configured

  /// A branch name for the integration branch cut at `sha` that no existing branch has, so calling `set_integration_ref` again
  /// for the same commit (the retry a failed session asks for) starts a fresh branch instead of dying on "a branch named ...
  /// already exists" (F7). A branch that exists may hold landings, so it is never reset or reused.
  let freshBranchName (sha: string) (taken: string -> bool) : string =
    let plain = sprintf "sagefs/cohort-%s" (sha.Substring(0, min 8 sha.Length))
    match taken plain with
    | false -> plain
    | true ->
      Seq.initInfinite (fun i -> sprintf "%s-%d" plain (i + 2))
      |> Seq.find (taken >> not)

  /// The test frameworks whose package a project references to be a test project. One exhaustive to-string function.
  [<RequireQualifiedAccess>]
  type TestFramework =
    | Expecto
    | MicrosoftTestSdk
    | XUnit
    | NUnit
    | MSTest
    | TUnit

  let testFrameworkPackage (framework: TestFramework) : string =
    match framework with
    | TestFramework.Expecto -> "Expecto"
    | TestFramework.MicrosoftTestSdk -> "Microsoft.NET.Test.Sdk"
    | TestFramework.XUnit -> "xunit"
    | TestFramework.NUnit -> "NUnit"
    | TestFramework.MSTest -> "MSTest.TestFramework"
    | TestFramework.TUnit -> "TUnit"

  /// Does this project file (its XML text) reference a test framework, or say it is a test project? Total: text that is not XML
  /// is not a test project.
  let isTestProjectXml (projectXml: string) : bool =
    let frameworkPackages =
      [ TestFramework.Expecto; TestFramework.MicrosoftTestSdk; TestFramework.XUnit; TestFramework.NUnit; TestFramework.MSTest; TestFramework.TUnit ]
      |> List.map testFrameworkPackage
    try
      let doc = XDocument.Parse projectXml
      let referencesFramework =
        doc.Descendants(XName.Get "PackageReference")
        |> Seq.exists (fun el ->
          match el.Attribute(XName.Get "Include") with
          | null -> false
          | attr -> frameworkPackages |> List.exists (fun package -> String.Equals(package, attr.Value, StringComparison.OrdinalIgnoreCase)))
      let declaresTestProject =
        doc.Descendants(XName.Get "IsTestProject")
        |> Seq.exists (fun el -> String.Equals(el.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
      referencesFramework || declaresTestProject
    with :? System.Xml.XmlException -> false

  /// The projects the integration session loads. A landing is verified by tests, so the session needs the test projects and,
  /// through their references, what they test. Every other project of the solution (the samples that need a Rust, Python or Java
  /// toolchain, the demos) can never fail a landing and costs a cold build before the first one. NO-EMPTY-ESCAPE, as above: when
  /// no candidate is a test project the whole candidate list stands, so narrowing can never leave a session with nothing.
  let narrowToTestProjects (candidates: string list) (isTestProject: string -> bool) : string list =
    match List.filter isTestProject candidates with
    | [] -> candidates
    | tests -> tests

  /// The projects a conductor named for the integration session, from the one string a tool argument carries: comma, semicolon or
  /// line separated, repository-relative, with `\` read as `/`. Blank entries are dropped.
  let parseNamedProjects (raw: string) : string list =
    match raw with
    | null -> []
    | _ ->
      raw.Split([| ','; ';'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.map (fun p -> p.Trim().Replace('\\', '/'))
      |> Array.filter (fun p -> p <> "")
      |> Array.toList
      |> List.distinct

  /// The conductor's own list when it gave one (it knows which project its landings are verified by), else what discovery found.
  let chooseIntegrationProjects (named: string list) (discovered: string list) : string list =
    match named with
    | [] -> discovered
    | _ -> named

  /// What a faulted integration session says: the cause, and what to do about it. The retry is `set_integration_ref` again for
  /// the same repository, which replaces the integration worktree and starts a fresh session.
  let failureAdvice (reason: string) (worktreePath: string) (repositoryLabel: string) : string =
    sprintf
      "%s. To retry: look at the integration worktree %s, fix the cause (a build error shows in its build output; a daemon built from a different commit than the worktree has to be reinstalled first), then call set_integration_ref again with working_directory set to the repository of %s. It replaces the worktree and starts a fresh session."
      (reason.TrimEnd('.', ' '))
      worktreePath
      repositoryLabel

  // ── P16 — replacing the integration worktree under a landing ────────────

  /// Whether the integration worktree may be replaced now. Replacing it stops the session that verifies landings and deletes
  /// the sources that session reads, so a landing in the queue (queued, rebasing or verifying) forbids it.
  [<RequireQualifiedAccess>]
  type Replacement =
    | Allowed
    | Refused of landings: string list

  let replacementDecision (landingsInFront: string list) : Replacement =
    match landingsInFront with
    | [] -> Replacement.Allowed
    | landings -> Replacement.Refused landings

  /// What a refused replacement says: which landings are in front, why that forbids it, and the two ways out.
  let replacementRefusal (scopeLabel: string) (landings: string list) : string =
    sprintf
      "The integration for %s is verifying %s: %s. Replacing it now would stop the session that verifies them and delete the sources it reads, so nothing was changed. Wait for them to land, or have each requester take theirs back with withdraw_landing, then call set_integration_ref again."
      scopeLabel
      (match List.length landings with 1 -> "1 landing" | n -> sprintf "%d landings" n)
      (String.concat ", " landings)

  /// A readable slug of a cohort label (its last path segment) and a hash of the whole label: the slug is for the person who
  /// lists the data dir, the hash is what keeps two cohorts apart.
  let private cohortSuffix (scopeLabel: string) : string =
    let lastSegment =
      match scopeLabel.LastIndexOfAny [| '/'; '\\'; ':' |] with
      | -1 -> scopeLabel
      | i -> scopeLabel.Substring(i + 1)
    let slug =
      lastSegment
      |> Seq.filter (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_')
      |> Seq.truncate 24
      |> Seq.toArray
      |> String
    let hash =
      System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes scopeLabel)
      |> Convert.ToHexString
      |> fun hex -> hex.Substring(0, 12).ToLowerInvariant()
    match slug with
    | "" -> hash
    | _ -> sprintf "%s-%s" slug hash

  /// The directory name (under the data dir) of the integration worktree for one cohort. One per cohort: two repositories
  /// never share a path.
  let integrationWorktreeName (scopeLabel: string) : string =
    sprintf "cohort-integration-%s" (cohortSuffix scopeLabel)

  /// The directory name of the trunk checkout for one cohort, by the same rule.
  let trunkCheckoutName (scopeLabel: string) : string =
    sprintf "cohort-trunk-%s" (cohortSuffix scopeLabel)

  /// The integration a new call replaces: the repository its worktree was cut from and where the worktree is.
  type PreviousIntegration = { Repository: string; WorktreePath: string }

  /// Every worktree to remove before the new one is cut, as (repository to run git in, worktree path). A worktree is removed
  /// from the repository that created it: git refuses to remove another repository's worktree.
  let removalTargets (previous: PreviousIntegration option) (newRepository: string) (newWorktreePath: string) : (string * string) list =
    match previous with
    | None -> [ newRepository, newWorktreePath ]
    | Some old when old.WorktreePath = newWorktreePath -> [ old.Repository, old.WorktreePath ]
    | Some old -> [ old.Repository, old.WorktreePath; newRepository, newWorktreePath ]

  /// What a cohort that is not the one the integration serves is told to do to move it.
  let moveAdvice (configuredFor: string) : string =
    sprintf
      "call set_integration_ref with working_directory set to this repository to move it here. That stops %s's integration session and removes its worktree, and it is refused while %s has landings in its queue (the requesters take them back with withdraw_landing)"
      configuredFor
      configuredFor
