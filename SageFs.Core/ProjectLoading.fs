module SageFs.ProjectLoading

open System
open System.IO
open System.Xml.Linq

open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo

open Ionide.ProjInfo.Types
open SageFs.Utils

type FileName = string
type DllName = string
type DirName = string

/// Project role classification for session management.
/// Determines which projects are suitable for hot-reloading vs. testing.
type ProjectRole =
  | Executable    // Has OutputType = Exe and is meant to be run as a web app
  | Library       // Shared libraries, static assemblies
  | Test          // Test projects (contained in test packages or marked with IsTestProject)

/// A loaded project with its role. The entry point of an executable is not
/// stored here: the compiled assembly's Assembly.EntryPoint is authoritative.
and ClassifiedProject = {
  Path: string
  Role: ProjectRole
  PackageRefs: string list
}

/// Minimal manual .fsproj parse used as a fallback when Ionide's workspace
/// loader silently returns zero projects (MSBuild evaluation can fail
/// in-process without throwing). Produces FSharpProjectOptions directly so
/// warm-up still finds source files and can open namespaces.
module ManualProjectParse =

  let private xname (local: string) = XName.Get(local)

  let rec private collectSourceFiles (projPath: string) (visited: Set<string>) (acc: FileName list) =
    let full = Path.GetFullPath projPath
    match visited.Contains full with
    | true -> acc, visited
    | false ->
      let visited' = visited.Add full
      match File.Exists full with
      | false -> acc, visited'
      | true ->
        try
          let doc = XDocument.Load full
          let dir = Path.GetDirectoryName full
          let ns = doc.Root.Attribute(xname "xmlns") |> Option.ofObj |> Option.map (fun a -> XNamespace.Get a.Value) |> Option.defaultValue (XNamespace.None)
          let compileIncludes =
            doc.Descendants(ns + "Compile")
            |> Seq.choose (fun el -> el.Attribute(xname "Include") |> Option.ofObj |> Option.map (fun a -> a.Value))
            |> Seq.map (fun inc -> Path.GetFullPath(Path.Combine(dir, inc.Replace('\\', Path.DirectorySeparatorChar))))
            |> Seq.filter (fun p -> p.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase))
            |> Seq.filter File.Exists
            |> Seq.toList
          // Recurse into ProjectReferences
          let refs =
            doc.Descendants(ns + "ProjectReference")
            |> Seq.choose (fun el -> el.Attribute(xname "Include") |> Option.ofObj |> Option.map (fun a -> a.Value))
            |> Seq.map (fun inc -> Path.GetFullPath(Path.Combine(dir, inc.Replace('\\', Path.DirectorySeparatorChar))))
            |> Seq.toList
          let recAcc, recVisited =
            refs |> List.fold (fun (a, v) r -> collectSourceFiles r v a) (acc, visited')
          recAcc @ compileIncludes, recVisited
        with _ ->
          acc, visited'

  /// Collect the built assembly and its dependencies for a project that was
  /// already compiled (bin/<config>/<tfm>/). Used by the manual fallback so FSI
  /// still gets project + NuGet references even when MSBuild evaluation fails.
  let collectBinReferences (logger: ILogger) (projPaths: string list) : DllName list =
    projPaths
    |> List.collect (fun projPath ->
      let projDir = Path.GetDirectoryName (Path.GetFullPath projPath)
      let binDir = Path.Combine(projDir, "bin")
      match Directory.Exists binDir with
      | false ->
        logger.LogWarning (sprintf "  No bin dir for %s — project may not be built yet" (Path.GetFileName projPath))
        []
      | true ->
        // Layout varies: some builds put DLLs in bin/<cfg>/ directly, others in
        // bin/<cfg>/<tfm>/. Collect from ONE config dir only (newest by write
        // time) — mixing Debug + Release DLLs produces duplicate assembly
        // versions that FSI rejects with 0x80131040.
        let cfgDirs = Directory.EnumerateDirectories binDir |> Seq.sortByDescending (fun d -> Directory.GetLastWriteTimeUtc d) |> Seq.toList
        match cfgDirs with
        | [] -> []
        | cfgDir :: _ ->
          let cfgRootDlls =
            Directory.EnumerateFiles(cfgDir, "*.dll", SearchOption.TopDirectoryOnly)
          let tfmSubDlls =
            Directory.EnumerateDirectories cfgDir
            |> Seq.collect (fun tfmDir ->
              Directory.EnumerateFiles(tfmDir, "*.dll", SearchOption.TopDirectoryOnly))
          Seq.append cfgRootDlls tfmSubDlls
          // Exclude satellite/resource assemblies (they live in culture subdirs
          // and would collide in the shadow dir) and native/non-managed DLLs that
          // FSI can't load via -r: (e.g. aspnetcorev2_inprocess.dll).
          |> Seq.filter (fun dll ->
            let name = Path.GetFileName dll
            not (name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
            && not (name.Contains("aspnetcorev2", StringComparison.OrdinalIgnoreCase))
            && not (name.EndsWith(".ni.dll", StringComparison.OrdinalIgnoreCase))
            && not (name.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
          // Same-named DLLs can appear in MULTIPLE TFM subdirs (a project's bin
          // may hold orphans from old target layouts, e.g. a net10 copy of
          // SageFs.Core.dll left behind after the project targeted a different
          // TFM (net11-only during the preview era, or a net9 orphan)). Passing
          // both to FSI lets the stale one shadow the fresh build, so the REPL
          // compiles against ancient metadata. Dedupe by file name, keeping the
          // NEWEST copy — an orphan can never shadow a fresh build.
          |> Seq.groupBy (fun dll -> Path.GetFileName dll)
          |> Seq.map (fun (_, group) ->
            group |> Seq.maxBy (fun dll -> File.GetLastWriteTimeUtc dll))
          |> Seq.toList
          |> fun dlls ->
          // ASP.NET Core framework DLLs (Microsoft.AspNetCore.*, etc.) are NOT in
          // bin/ — they come from the shared framework. MSBuild's FrameworkReference
          // normally adds them; the manual fallback must add them explicitly or FSI
          // fails with "type ... is defined in an assembly that is not referenced".
          let dotnetRoot =
            Environment.GetEnvironmentVariable("DOTNET_ROOT")
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
              // typeof<obj>.Assembly.Location = .../shared/Microsoft.NETCore.App/<ver>/System.Private.CoreLib.dll
              // ../../../ = dotnet root
              let runtimeDir = Path.GetDirectoryName(typeof<obj>.Assembly.Location)
              Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..")))
          // Newest-version *.dll of a shared framework under the dotnet root, or []
          // when that framework is not installed. A FrameworkReference normally adds
          // these; the manual fallback must add them or FSI fails with "type ... is
          // defined in an assembly that is not referenced".
          let sharedFrameworkDlls (frameworkName: string) =
            let dir = Path.Combine(dotnetRoot, "shared", frameworkName)
            match Directory.Exists dir with
            | false -> []
            | true ->
              Directory.EnumerateDirectories dir
              |> Seq.sortDescending
              |> Seq.tryHead
              |> Option.map (fun verDir -> Directory.EnumerateFiles(verDir, "*.dll", SearchOption.TopDirectoryOnly) |> Seq.toList)
              |> Option.defaultValue []
          let aspNetShared = sharedFrameworkDlls "Microsoft.AspNetCore.App"
          // WPF/WinForms assemblies come from the Microsoft.WindowsDesktop.App shared
          // framework (Windows only), exactly the way ASP.NET Core does. The directory
          // is absent on Linux/macOS, so this is a no-op there and present only on a
          // Windows box with the Desktop runtime installed.
          let windowsDesktopShared = sharedFrameworkDlls "Microsoft.WindowsDesktop.App"
          // Apply the same safety filter to BOTH lists (native DLLs like
          // aspnetcorev2_inprocess.dll exist in the shared framework and must
          // never be passed to FSI as -r: references).
          let isManagedRef (dll: string) =
            let name = Path.GetFileName dll
            not (name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
            && not (name.Contains("aspnetcorev2", StringComparison.OrdinalIgnoreCase))
            && not (name.EndsWith(".ni.dll", StringComparison.OrdinalIgnoreCase))
          // Shared-framework entries are only used when the app's own bin
          // doesn't already provide that assembly — the bin version is the
          // exact dependency the app was built against.
          let binNames = dlls |> List.map Path.GetFileName |> Set.ofList
          let combined =
            dlls
            |> List.append (aspNetShared |> List.filter (fun d -> not (binNames.Contains(Path.GetFileName d))))
            |> List.append (windowsDesktopShared |> List.filter (fun d -> not (binNames.Contains(Path.GetFileName d))))
            |> List.filter isManagedRef
            |> List.distinct
          logger.LogInfo (sprintf "  Collected %d reference DLL(s) from %s (%d ASP.NET + %d WindowsDesktop shared framework)" combined.Length binDir aspNetShared.Length windowsDesktopShared.Length)
          combined)

  /// Parse an .fsproj (and its project references) into FSharpProjectOptions.
  /// Returns None if the file doesn't exist or has no source files.
  let parseFsproj (logger: ILogger) (projPath: string) : FSharpProjectOptions list =
    let files, _ = collectSourceFiles projPath Set.empty []
    match files with
    | [] ->
      logger.LogWarning (sprintf "  Manual parse of %s found no source files" (Path.GetFileName projPath))
      []
    | _ ->
      logger.LogInfo (sprintf "  Manual parse of %s found %d source file(s)" (Path.GetFileName projPath) files.Length)
      [ { ProjectFileName = Path.GetFullPath projPath
          ProjectId = None
          SourceFiles = files |> List.toArray
          OtherOptions = [||]
          ReferencedProjects = [||]
          IsIncompleteTypeCheckEnvironment = false
          UseScriptResolutionRules = false
          LoadTime = DateTime.UtcNow
          OriginalLoadReferences = []
          UnresolvedReferences = None
          Stamp = None } ]

type Solution = {
  FsProjects: FSharpProjectOptions list
  Projects: ProjectOptions list
  StartupFiles: FileName list
  References: DllName list
  LibPaths: DirName list
  OtherArgs: string list
}

let emptySolution = {
  FsProjects = []
  Projects = []
  StartupFiles = []
  References = []
  LibPaths = []
  OtherArgs = []
}

/// If a build output does not exist, probe the same path under the sibling
/// configuration (Debug ↔ Release) and return it when it exists. Ionide
/// evaluates projects with MSBuild's default Configuration (Debug), so after a
/// Release-only build every Debug path it reports is missing — the project's
/// own bin/<Config>/<TFM>/x.dll AND the referenced projects' reference
/// assemblies at obj/<Config>/<TFM>/ref/x.dll. The nearest Debug/Release
/// directory in the path is the configuration, whatever the layout.
/// The same build-output path under the OTHER configuration (Debug ↔ Release),
/// or None when the path contains no Debug/Release segment. The nearest such
/// segment (searched from the end) is the configuration, whatever the layout.
let siblingConfigPath (dllPath: string) : string option =
  let separators = [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]
  let segments = dllPath.Split separators
  let isConfig (segment: string) =
    String.Equals(segment, "Debug", StringComparison.OrdinalIgnoreCase)
    || String.Equals(segment, "Release", StringComparison.OrdinalIgnoreCase)
  match segments |> Array.tryFindIndexBack isConfig with
  | None -> None
  | Some index ->
    let sibling =
      match String.Equals(segments.[index], "Debug", StringComparison.OrdinalIgnoreCase) with
      | true -> "Release"
      | false -> "Debug"
    segments
    |> Array.mapi (fun i segment -> match i = index with | true -> sibling | false -> segment)
    |> String.concat (string Path.DirectorySeparatorChar)
    |> Some

let resolveSiblingConfigOutput (dllPath: string) : string option =
  try
    match File.Exists dllPath with
    | true -> Some dllPath
    | false ->
      match siblingConfigPath dllPath with
      | Some candidate when File.Exists candidate -> Some candidate
      | _ -> None
  with _ -> None

/// The FRESHEST existing build output across the Debug/Release sibling
/// configurations. Existence alone is not enough: Ionide evaluates projects
/// with MSBuild's default Configuration (Debug), so it reports a bin/Debug
/// TargetPath even when the user's real, current build is Release — and if a
/// STALE Debug output happens to exist, `resolveSiblingConfigOutput` (which
/// stops at the first path that exists) would load that stale assembly into the
/// REPL. That was a genuine dogfood failure: a session ran a project's OLD code
/// while the freshly-built Release output sat unused. Picking the newest write
/// time across configs means the code you built is the code the REPL runs,
/// regardless of which config Ionide named. Pure: existence + write time are
/// injected so the selection is unit-testable without a filesystem.
let chooseFreshestConfigOutputWith
    (exists: string -> bool)
    (writeTimeUtc: string -> DateTime)
    (dllPath: string) : string option =
  let candidates =
    dllPath :: (siblingConfigPath dllPath |> Option.toList)
    |> List.filter exists
  match candidates with
  | [] -> None
  | xs -> xs |> List.maxBy writeTimeUtc |> Some

let resolveFreshestConfigOutput (dllPath: string) : string option =
  try chooseFreshestConfigOutputWith File.Exists File.GetLastWriteTimeUtc dllPath
  with _ -> None

let loadSolution (logger: ILogger) (config: Args.ProjectLoadConfig) =
  let directory = config.WorkingDir

  let explicitProjects = config.Projects
  let explicitSolutions = config.Solutions

  // When projects are given explicitly, don't auto-discover .sln files.
  // Only auto-discover when neither projects nor solutions is specified.
  let solutions =
    match explicitSolutions with
    | _ :: _ -> explicitSolutions |> List.map Path.GetFullPath
    | [] when not explicitProjects.IsEmpty -> []
    | [] ->
      Directory.EnumerateFiles directory
      |> Seq.filter (fun s -> s.EndsWith(".sln", System.StringComparison.Ordinal) || s.EndsWith(".slnx", System.StringComparison.Ordinal))
      |> Seq.toList

  let projects =
    match explicitProjects with
    | _ :: _ -> explicitProjects |> List.map Path.GetFullPath
    | [] when not explicitSolutions.IsEmpty -> [] // solutions handle their own projects
    | [] ->
      Directory.EnumerateFiles directory
      |> Seq.filter (fun s -> s.EndsWith(".fsproj", System.StringComparison.Ordinal))
      |> Seq.toList

  match solutions, projects with
  | [], [] ->
    logger.LogWarning "Couldnt find any solution or project"

    {
      FsProjects = []
      Projects = []
      StartupFiles = []
      References = []
      LibPaths = []
      OtherArgs = []
    }
  | _ ->

    for s in solutions do
      logger.LogInfo (sprintf "Found solution: %s" (Path.GetFileName s))
    for p in projects do
      logger.LogInfo (sprintf "Found project: %s" (Path.GetFileName p))

    logger.LogInfo "Initializing build tooling..."
    let toolsPath = Init.init (DirectoryInfo directory) None
    logger.LogInfo (sprintf "  MSBuild/tools path: %A" toolsPath)
    let defaultLoader: IWorkspaceLoader = WorkspaceLoader.Create(toolsPath, [])

    logger.LogInfo "Loading solution and project references..."
    let loadedProjects =
      try
        let slnProjects =
          solutions
          |> List.collect (fun s ->
            logger.LogInfo (sprintf "  Loading %s..." (Path.GetFileName s))
            defaultLoader.LoadSln s |> Seq.toList)
        slnProjects
        |> Seq.append (defaultLoader.LoadProjects projects)
        |> Seq.toList
      with ex ->
        logger.LogWarning (sprintf "  Project loader failed (%s) — falling back to manual fsproj parse" ex.Message)
        []

    logger.LogInfo (sprintf "  Loaded %d project(s)." (List.length loadedProjects))

    match loadedProjects with
    | [] when not (List.isEmpty projects) ->
      // Ionide's loader can silently return empty (no exception) when MSBuild
      // evaluation fails in-process. Fall back to a manual parse so sessions
      // still get source files — better than a silent 0.
      logger.LogWarning "  Loader returned 0 projects — attempting manual fsproj parse"
      let manual = projects |> List.collect (fun projPath -> ManualProjectParse.parseFsproj logger projPath)
      let refs = ManualProjectParse.collectBinReferences logger projects
      // LibPaths must lead with the project's bin dir so FSI's assembly probe
      // resolves the project's own dependency versions (Falco, Npgsql, ...)
      // BEFORE the worker process's own copies (SageFs bundles Falco for its
      // dashboard — a version collision breaks #load with 0x80131040).
      let binLibPaths =
        projects
        |> List.map (fun projPath ->
          let binDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath projPath), "bin")
          match Directory.Exists binDir with
          | false -> None
          | true ->
            Directory.EnumerateDirectories binDir
            |> Seq.sortByDescending (fun d -> Directory.GetLastWriteTimeUtc d)
            |> Seq.tryHead)
        |> List.choose id
        |> List.filter Directory.Exists
      {
        FsProjects = manual
        Projects = []
        StartupFiles = []
        References = refs
        LibPaths = binLibPaths
        // Match the language version a normal build would use. FSI defaults to
        // an older language version than the SDK's default, which rejects
        // modern constructs (e.g. "A type parameter is missing a constraint
        // 'when 'T: not struct'" from try/with null patterns). With the manual
        // fallback there are no ProjectOptions to carry --langversion, so set
        // it explicitly.
        OtherArgs = [ "--langversion:preview" ]
      }
    | _ ->
      // Ionide's loader evaluates projects with MSBuild defaults (Debug
      // output), so TargetPath points at bin/Debug even when the user's real,
      // current build is Release (or vice versa). Rewrite each project's
      // TargetPath to the FRESHEST config output across Debug/Release — not
      // merely one that exists — so a stale Debug artifact can never shadow a
      // freshly-built Release one in the REPL (a real dogfood failure: a
      // session ran a project's old code while its new build sat unused). When
      // neither exists the original path is kept so the missing-DLL error stays
      // accurate.
      let loadedProjects' =
        loadedProjects
        |> Seq.map (fun po ->
          match resolveFreshestConfigOutput po.TargetPath with
          | Some fresh -> { po with TargetPath = fresh }
          | None -> po)
        |> Seq.toList
      let fcsProjectOptions = List.ofSeq <| FCS.mapManyOptions loadedProjects'
      {
        FsProjects = fcsProjectOptions
        Projects = loadedProjects'
        StartupFiles = []
        References = []
        LibPaths = []
        OtherArgs = []
      }

/// Detect if a project is a test project via MSBuild property or package references.
let isTestProject (proj: ProjectOptions) : bool =
  match proj.AllProperties.TryFind "IsTestProject" with
  | Some vals when vals |> Set.exists (fun v -> String.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) -> true
  | _ ->
    let testPackages = [ "Expecto"; "xunit"; "xunit.v3"; "NUnit"; "MSTest.TestFramework"; "Microsoft.NET.Test.Sdk" ]
    proj.PackageReferences
    |> List.exists (fun pr ->
      let name = Path.GetFileNameWithoutExtension(pr.FullPath)
      testPackages |> List.exists (fun tp -> name.StartsWith(tp, StringComparison.OrdinalIgnoreCase)))

/// Filter a solution's projects to only test projects.
let discoverTestProjects (projects: ProjectOptions list) : ProjectOptions list =
  projects |> List.filter isTestProject

/// Desktop-UI frameworks are enabled by MSBuild properties, not packages
/// (WPF/WinForms have no package — they are `<UseWPF>`/`<UseWindowsForms>` on a
/// `-windows` TFM; MAUI/WinUI add `<UseMaui>`/`<UseWinUI>` alongside packages).
/// Surface the active ones as classification markers so ProjectKind can see a
/// desktop UI it could never detect from package references alone.
let private activeUiPropertyMarkers (proj: ProjectOptions) : string list =
  [ "UseWPF"; "UseWindowsForms"; "UseMaui"; "UseWinUI" ]
  |> List.filter (fun prop ->
    match proj.AllProperties.TryFind prop with
    | Some vals -> vals |> Set.exists (fun v -> String.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
    | None -> false)

/// Classify a single project by its role (Executable, Library, or Test).
/// Uses MSBuild OutputType property and test package reference heuristics.
let classifyProject (proj: ProjectOptions) : ClassifiedProject =
  let role =
    match proj.AllProperties.TryFind "OutputType" with
    | Some vals when vals |> Set.exists (fun v -> String.Equals(v, "Exe", StringComparison.OrdinalIgnoreCase)) -> ProjectRole.Executable
    | _ ->
      if isTestProject proj then ProjectRole.Test
      else ProjectRole.Library
  let packageRefs = proj.PackageReferences |> List.map (fun pr -> Path.GetFileNameWithoutExtension(pr.FullPath))
  { Path = proj.ProjectFileName
    Role = role
    // Package refs plus active desktop-UI property markers (UseWPF/…), so a
    // WPF/WinForms/MAUI/WinUI project — whose UI framework is a property, not a
    // package — still classifies as native-GUI.
    PackageRefs = packageRefs @ activeUiPropertyMarkers proj }

/// Classify all projects in a solution, returning a map of path to classification.
let classifyProjects (projects: ProjectOptions list) : ClassifiedProject list =
  projects |> List.map classifyProject

/// Orders projects so every project appears AFTER all of its own project
/// references (a dependency-first topological sort by `ReferencedProjects`).
///
/// WHY: Ionide's `WorkspaceLoader` returns the explicitly-requested project(s)
/// first, followed by their transitive references in discovery order — NOT
/// dependency order. Feeding FSI `-r:` flags in that order (the dependent
/// project's assembly referenced BEFORE the assembly it depends on) makes FCS
/// resolve an ambiguous cross-assembly name incorrectly: verified live, when
/// `SageFs.Tests.dll` (declaring namespace `SageFs.Tests`) was referenced
/// before `SageFs.dll` (declaring `[<RequireQualifiedAccess>] PaneId` — a
/// union type living directly in namespace `SageFs`, with a case named
/// `Tests`), resolving `SageFs.Tests.EvalTimelineTests` failed with "the
/// union case 'Tests' ... requires the union type name ('PaneId')" instead of
/// finding the sibling namespace — a bogus ambiguity between the namespace
/// segment and the qualified-access union case. Referencing `SageFs.dll`
/// first (as a normal build's dependency order does) resolves it correctly.
/// (roast-4 #0, dogfood REPL gate.)
let private topoSortByProjectReferences (projects: ProjectOptions list) : ProjectOptions list =
  let byPath =
    projects
    |> List.map (fun p -> Path.GetFullPath p.ProjectFileName, p)
    |> Map.ofList
  let visited = System.Collections.Generic.HashSet<string>()
  let result = System.Collections.Generic.List<ProjectOptions>()
  let rec visit (p: ProjectOptions) =
    let key = Path.GetFullPath p.ProjectFileName
    match visited.Add(key) with
    | false -> ()
    | true ->
      for r in p.ReferencedProjects do
        match byPath.TryFind(Path.GetFullPath r.ProjectFileName) with
        | Some referenced -> visit referenced
        | None -> ()
      result.Add(p)
  for p in projects do
    visit p
  result |> List.ofSeq

let solutionToFsiArgs (logger: ILogger) (_useAsp: bool) (hotReload: bool) sln =
  let orderedProjects = topoSortByProjectReferences sln.Projects
  let projectDlls = orderedProjects |> Seq.map _.TargetPath

  let nugetDlls =
    orderedProjects |> Seq.collect _.PackageReferences |> Seq.map _.FullPath

  let otherDlls = sln.References

  let allDlls =
    projectDlls
    |> Seq.append nugetDlls
    |> Seq.append otherDlls
    |> Seq.distinct
    |> List.ofSeq

  // The -r: references each project passes its compiler (framework assemblies
  // and referenced projects' reference assemblies), resolved to outputs that
  // exist — after a Release-only build they point into obj/Debug and would
  // otherwise kill FSI at startup with a bare StopProcessingExn.
  //
  // WHY the file-name dedupe against allDlls: Ionide's OtherOptions carries
  // each project's OWN "-r:" flags as MSBuild originally emitted them —
  // including project-to-project references resolved to their COMPILE-TIME
  // "obj/<Config>/<TFM>/ref/X.dll" reference assemblies, which are NEVER
  // rewritten to the shadow-copy path the way projectDlls' TargetPath is.
  // Feeding FSI both "-r:<shadow>/X.dll" (allDlls, real IL, the assembly the
  // session actually executes) AND "-r:.../obj/.../ref/X.dll" (compilerRefs,
  // a distinct file with the SAME assembly identity) hands the compiler two
  // separate physical files for one logical assembly — a needless duplicate
  // reference (and, on a Release-only build, a stale/missing-DLL risk since
  // the ref/ path is never shadow-copied). allDlls already carries the one
  // copy FSI should execute against, so any compilerRefs entry naming the
  // same file is dropped.
  let allDllNames =
    allDlls
    |> Seq.map Path.GetFileName
    |> Set.ofSeq

  let compilerRefs =
    orderedProjects
    |> Seq.collect _.OtherOptions
    |> Seq.filter (fun s ->
      s.StartsWith("-r:", System.StringComparison.Ordinal)
      && s.EndsWith(".dll", System.StringComparison.Ordinal))
    |> Seq.map (fun s ->
      let path = s.Substring 3
      resolveSiblingConfigOutput path |> Option.defaultValue path)
    |> Seq.filter (fun path -> not (allDllNames.Contains(Path.GetFileName path)))
    |> Seq.distinct
    |> List.ofSeq

  match (allDlls @ compilerRefs) |> List.filter (File.Exists >> not) |> List.distinct with
  | [] -> ()
  | missing ->
    for dll in missing do
      logger.LogError (sprintf "Missing DLL: %s" dll)
    failwithf
      "Not all DLLs are found (%d missing: %s) — this project isn't built yet (both Debug and Release outputs were checked). \
       Recover WITHOUT leaving SageFs: run hard_reset_fsi_session with rebuild:true (or click HARD_RESET on the dashboard) — \
       SageFs builds the project and surfaces any compiler errors (e.g. FS0001) right here, so you never have to switch to a \
       terminal to find out why the build fails. (You can also build it yourself first: dotnet build.)"
      missing.Length
      (missing |> List.map Path.GetFileName |> String.concat ", ")
  // Flags from project OtherOptions that FSI should inherit for source-level
  // compatibility (e.g. --checknulls+ from <Nullable>enable</Nullable>).
  // We explicitly exclude --warnaserror (too strict for REPL) and --optimize
  // (irrelevant for interactive eval).
  let fsiSafeFlags =
    orderedProjects
    |> Seq.collect _.OtherOptions
    |> Seq.filter (fun s ->
      s.StartsWith("--checknulls", System.StringComparison.Ordinal)
      || s.StartsWith("--nowarn", System.StringComparison.Ordinal)
      || s.StartsWith("--langversion", System.StringComparison.Ordinal))
    |> Seq.distinct

  [|
    "fsi"
    // "--multiemit-" disables FSI multi-assembly mode, keeping all code in a single
    // assembly. This prevents the canonical F# pattern (type T + module T) from breaking
    // across submission boundaries. Always enable to ensure type references remain valid.
    "--multiemit-"
    yield! allDlls |> Seq.map (sprintf "-r:%s")
    yield! sln.LibPaths |> Seq.map (sprintf "--lib:%s")
    yield! sln.OtherArgs
    yield! fsiSafeFlags
    // Always include the projects' compiler references (framework assemblies
    // such as ASP.NET Core, and referenced projects) — resolved above.
    yield! compilerRefs |> Seq.map (sprintf "-r:%s")
  |]
