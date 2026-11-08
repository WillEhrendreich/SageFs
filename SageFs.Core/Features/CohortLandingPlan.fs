namespace SageFs.Features

open System
open System.Xml.Linq

/// What the cohort does with a landing that carries a change to a PRODUCTION source file (P14, findings C6 to C8 of the
/// first cohort run). Pure decisions only: the daemon reads the files and runs the processes, this module says what
/// to do with what it read.
///
/// The first landing of source in a compiled project (SageFs.Core's AppState.fs) was Blocked Inconclusive three times.
/// The verifier bound the rebased file into the integration session with a re-evaluation, and the session's project
/// already holds that file's types from its compiled DLL, so the re-evaluation could not stand beside them. A test
/// project's own files are different: the session evaluates those in place of the build's copy, and that has always
/// worked. So the decision is per file, and it is made here:
///
///  - a test project's source: re-evaluate it over the build (the cheap path, unchanged);
///  - source of a project the session loads that is NOT a test project, or anything that changes what the build makes
///    (a project file, props, targets, a lock file, the SDK pin): rebuild the integration worktree at the rebased
///    commit and run the tests against what the build made;
///  - a page a script generates: no claim, regenerated after the rebase (`ClaimExempt`).
module CohortLandingPlan =

  // ── Which files a landing carries, and what each is to the integration session ──

  [<RequireQualifiedAccess>]
  type ProjectRole =
    | Test
    | Production

  /// One project of the repository, as its project file says: where it is, whether it tests, the files it compiles
  /// (repository-relative, `/` separated) and the projects it references.
  type ProjectInfo = {
    Path: string
    Role: ProjectRole
    Compiles: string list
    References: string list
  }

  [<RequireQualifiedAccess>]
  type ChangedFile =
    /// F# source of a test project the session loads. The session evaluates it in place of the build's copy.
    | TestSource of path: string
    /// F# source (or signature) of a project the session loads that the tests call into. Its types are in the compiled
    /// assembly the session holds, so only a build puts the new text there.
    | CompiledSource of path: string * project: string
    /// A project, props, targets, lock or SDK pin file: changes what the build makes.
    | BuildInput of path: string
    /// A page a script generates. It needs no claim and is regenerated after the rebase.
    | Generated of path: string
    /// Anything else (documents, scripts, data). It cannot change what the build makes.
    | Other of path: string

  /// `path` with `\` read as `/`, a leading `./` dropped, and `.` and `..` segments resolved. A path that climbs out of
  /// the root keeps its `..`, so it never matches a repository file by accident.
  let normalize (path: string) : string =
    let parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
    let resolved =
      parts
      |> Array.fold (fun (acc: string list) part ->
        match part with
        | "." -> acc
        | ".." ->
          match acc with
          | previous :: rest when previous <> ".." -> rest
          | _ -> ".." :: acc
        | _ -> part :: acc) []
    resolved |> List.rev |> String.concat "/"

  let private directoryOf (path: string) : string =
    match path.LastIndexOf '/' with
    | -1 -> ""
    | i -> path.Substring(0, i)

  let private fileNameOf (path: string) : string =
    match path.LastIndexOf '/' with
    | -1 -> path
    | i -> path.Substring(i + 1)

  let private resolveFrom (dir: string) (relative: string) : string =
    match dir with
    | "" -> normalize relative
    | _ -> normalize (dir + "/" + relative)

  /// Read one project file. `None` for text that is not XML. Wildcard includes are not expanded: a project that compiles
  /// by glob owns its files through the directory rule in `ownerOf`.
  let parseProject (path: string) (xml: string) : ProjectInfo option =
    try
      let doc = XDocument.Parse xml
      let dir = directoryOf (normalize path)
      let includes (element: string) =
        doc.Descendants(XName.Get element)
        |> Seq.choose (fun el ->
          match el.Attribute(XName.Get "Include") with
          | null -> None
          | attr -> Some attr.Value)
        |> Seq.filter (fun value -> not (value.Contains '*'))
        |> Seq.map (resolveFrom dir)
        |> Seq.toList
      Some {
        Path = normalize path
        Role = (match CohortIntegrationScope.isTestProjectXml xml with true -> ProjectRole.Test | false -> ProjectRole.Production)
        Compiles = includes "Compile"
        References = includes "ProjectReference"
      }
    with :? System.Xml.XmlException -> None

  /// The projects the session loads, and everything they reference, transitively.
  let closure (index: ProjectInfo list) (loaded: string list) : Set<string> =
    let byPath = index |> List.map (fun p -> p.Path, p) |> Map.ofList
    let rec visit (seen: Set<string>) (path: string) : Set<string> =
      match Set.contains path seen with
      | true -> seen
      | false ->
        let seen' = Set.add path seen
        match Map.tryFind path byPath with
        | None -> seen'
        | Some project -> project.References |> List.fold visit seen'
    loaded |> List.map normalize |> List.fold visit Set.empty

  /// The project a source file belongs to: the one that compiles it by name, else the nearest project file above it.
  let ownerOf (index: ProjectInfo list) (path: string) : ProjectInfo option =
    let named = index |> List.tryFind (fun p -> List.contains path p.Compiles)
    match named with
    | Some project -> Some project
    | None ->
      index
      |> List.filter (fun p ->
        let dir = directoryOf p.Path
        dir = "" || path.StartsWith(dir + "/", StringComparison.Ordinal))
      |> List.sortByDescending (fun p -> (directoryOf p.Path).Length)
      |> List.tryHead

  let private buildInputFileNames =
    [ "Directory.Build.props"; "Directory.Build.targets"; "Directory.Packages.props"; "packages.lock.json"; "global.json"; "nuget.config"; "NuGet.Config" ]

  let private isBuildInputByName (path: string) : bool =
    let name = fileNameOf path
    List.exists (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)) buildInputFileNames
    || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
    || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)

  /// What a changed file is to the integration session. FAIL-CLOSED: a source file whose project cannot be found while the
  /// index is empty (nothing was read) counts as compiled source, and an empty `loaded` counts every project as loaded,
  /// because a rebuild is slower than a re-evaluation and never wrong.
  let classify (index: ProjectInfo list) (loaded: string list) (path: string) (generated: string -> bool) : ChangedFile =
    let p = normalize path
    let inClosure =
      match loaded with
      | [] -> fun (_: string) -> true
      | _ ->
        let set = closure index loaded
        fun project -> Set.contains project set
    match generated p with
    | true -> ChangedFile.Generated p
    | false ->
      let lower = p.ToLowerInvariant()
      match lower.EndsWith ".fsproj" with
      | true ->
        match inClosure p || List.isEmpty index with
        | true -> ChangedFile.BuildInput p
        | false -> ChangedFile.Other p
      | false ->
        match isBuildInputByName p with
        | true -> ChangedFile.BuildInput p
        | false ->
          match lower.EndsWith ".fs" || lower.EndsWith ".fsi" with
          | false -> ChangedFile.Other p
          | true ->
            match ownerOf index p with
            | None ->
              match List.isEmpty index with
              | true -> ChangedFile.CompiledSource(p, "(unknown project)")
              | false -> ChangedFile.Other p
            | Some owner ->
              match inClosure owner.Path with
              | false -> ChangedFile.Other p
              | true ->
                match owner.Role with
                | ProjectRole.Test -> ChangedFile.TestSource p
                | ProjectRole.Production -> ChangedFile.CompiledSource(p, owner.Path)

  // ── What to do with the landing ──

  [<RequireQualifiedAccess>]
  type RebuildReason =
    | CompiledSourceChanged of files: string list
    | BuildInputChanged of files: string list
    /// The session's build was made from a commit that did not land (a refused landing's rebased commit), so it is not
    /// the head's build whatever this landing carries.
    | SessionBuildIsOffHead of builtFor: string

  [<RequireQualifiedAccess>]
  type Strategy =
    /// Re-evaluate the landing's own files over the session's build.
    | EvalOverBuild
    /// Rebuild the integration worktree at the rebased commit, restart the session on it, and run the tests against it.
    | RebuildAtHead of reasons: RebuildReason list

  /// `sessionBuildOffHead` is the commit the session's build was made from when that commit is NOT in the head's history.
  let strategy (sessionBuildOffHead: string option) (changed: ChangedFile list) : Strategy =
    let compiled = changed |> List.choose (function ChangedFile.CompiledSource(path, _) -> Some path | _ -> None)
    let inputs = changed |> List.choose (function ChangedFile.BuildInput path -> Some path | _ -> None)
    let reasons =
      [ match sessionBuildOffHead with
        | Some commit -> yield RebuildReason.SessionBuildIsOffHead commit
        | None -> ()
        match compiled with
        | [] -> ()
        | files -> yield RebuildReason.CompiledSourceChanged files
        match inputs with
        | [] -> ()
        | files -> yield RebuildReason.BuildInputChanged files ]
    match reasons with
    | [] -> Strategy.EvalOverBuild
    | _ -> Strategy.RebuildAtHead reasons

  let describeReason (reason: RebuildReason) : string =
    match reason with
    | RebuildReason.CompiledSourceChanged files -> sprintf "compiled source changed (%s)" (String.concat ", " files)
    | RebuildReason.BuildInputChanged files -> sprintf "build input changed (%s)" (String.concat ", " files)
    | RebuildReason.SessionBuildIsOffHead commit -> sprintf "the session's build was made from %s, which did not land" commit

  /// The build, as the integration session holds it: made from `builtFor`, and the head's history says whether that
  /// commit landed. `isInHistory` answers for a commit. A session never rebuilt has no commit and is never off the head.
  let buildOffHead (isInHistory: string -> bool) (builtFor: string option) : string option =
    builtFor |> Option.filter (isInHistory >> not)

  /// Where the integration worktree goes once no landing is in flight: back to the head. `None` when it is already there
  /// or a landing is queued (the next rebase moves the worktree itself, and moving it under a verification would break it).
  let idleResetTarget (queueIsEmpty: bool) (worktreeAt: string) (head: string) : string option =
    match queueIsEmpty, worktreeAt = head with
    | true, false -> Some head
    | _ -> None

  // ── What a refused build says ──

  /// The one thing a refused build tells a member: the FIRST compiler error with its file, not the last line printed.
  /// MSBuild repeats an error once per project that sees it and prints the project's summary after, so the first
  /// diagnostic with a file and an `FS` code is the cause and everything after it is its echo.
  let describeBuildFailure (error: SageFs.SageFsError) : string =
    match error with
    | SageFs.SageFsError.BuildFailed(_, diagnostics) ->
      let blocking = diagnostics |> List.filter (fun d -> d.Severity = SageFs.BuildDiagnosticSeverity.Blocking)
      let isCompilerError (d: SageFs.BuildDiagnostic) =
        d.File.IsSome && (match d.Code with Some code -> code.StartsWith("FS", StringComparison.OrdinalIgnoreCase) | None -> false)
      let first =
        blocking
        |> List.tryFind isCompilerError
        |> Option.orElse (blocking |> List.tryFind (fun d -> d.File.IsSome))
        |> Option.orElse (List.tryHead blocking)
      match first with
      | Some d -> sprintf "the build of the rebased commit failed. First error: %s" (SageFs.BuildDiagnostic.describe [ d ])
      | None -> sprintf "the build of the rebased commit failed: %s" (SageFs.SageFsError.describeForAgent error)
    | other -> sprintf "the build of the rebased commit did not run: %s" (SageFs.SageFsError.describeForAgent other)

  /// The project files the integration worktree's index reads, as `git ls-files` patterns. A project the index misses is a
  /// project whose files classify as unowned, so every project language the session can load is listed.
  let projectFilePatterns : string list = [ "*.fsproj" ]

  /// A refused build, told as the one of two kinds it is (P16, roast priority 7): a compiler error in the landing's own source
  /// is the member's to fix; a restore that could not reach its feed (NU1301), a locked file (MSB3027), a missing SDK or any
  /// failure that is not a compiler error says nothing about the member's commit. The text carries the first error and the
  /// action that fits its kind.
  let classifyBuildFailure (error: SageFs.SageFsError) : CohortOwner.VerificationFault =
    CohortOwner.VerificationFault.Environment(describeBuildFailure error)
