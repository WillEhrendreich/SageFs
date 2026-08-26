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
          |> Seq.distinct
          |> Seq.toList
          |> fun dlls ->
          // ASP.NET Core framework DLLs (Microsoft.AspNetCore.*, etc.) are NOT in
          // bin/ — they come from the shared framework. MSBuild's FrameworkReference
          // normally adds them; the manual fallback must add them explicitly or FSI
          // fails with "type ... is defined in an assembly that is not referenced".
          let aspNetShared =
            let dotnetRoot =
              Environment.GetEnvironmentVariable("DOTNET_ROOT")
              |> Option.ofObj
              |> Option.defaultWith (fun () ->
                // typeof<obj>.Assembly.Location = .../shared/Microsoft.NETCore.App/<ver>/System.Private.CoreLib.dll
                // ../../../ = dotnet root
                let runtimeDir = Path.GetDirectoryName(typeof<obj>.Assembly.Location)
                Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..")))
            let aspNetDir = Path.Combine(dotnetRoot, "shared", "Microsoft.AspNetCore.App")
            match Directory.Exists aspNetDir with
            | false -> []
            | true ->
              Directory.EnumerateDirectories aspNetDir
              |> Seq.sortDescending
              |> Seq.tryHead
              |> Option.map (fun verDir -> Directory.EnumerateFiles(verDir, "*.dll", SearchOption.TopDirectoryOnly) |> Seq.toList)
              |> Option.defaultValue []
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
            |> List.filter isManagedRef
            |> List.distinct
          logger.LogInfo (sprintf "  Collected %d reference DLL(s) from %s (%d ASP.NET shared framework)" combined.Length binDir aspNetShared.Length)
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
      // resolves the project's own dependency versions (Falco, Marten, ...)
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
        OtherArgs = []
      }
    | _ ->
      let fcsProjectOptions = List.ofSeq <| FCS.mapManyOptions loadedProjects
      {
        FsProjects = fcsProjectOptions
        Projects = loadedProjects
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

let solutionToFsiArgs (logger: ILogger) (_useAsp: bool) (hotReload: bool) sln =
  let projectDlls = sln.Projects |> Seq.map _.TargetPath

  let nugetDlls =
    sln.Projects |> Seq.collect _.PackageReferences |> Seq.map _.FullPath

  let otherDlls = sln.References

  let allDlls =
    projectDlls
    |> Seq.append nugetDlls
    |> Seq.append otherDlls
    |> Seq.distinct
    |> List.ofSeq

  match List.exists (File.Exists >> not) allDlls with
  | true ->
    let missing = allDlls |> List.filter (File.Exists >> not)
    for dll in missing do
      logger.LogError (sprintf "Missing DLL: %s" dll)
    failwithf "Not all DLLs are found (%d missing). Please build your project before running REPL" missing.Length
  | false -> ()
  // Flags from project OtherOptions that FSI should inherit for source-level
  // compatibility (e.g. --checknulls+ from <Nullable>enable</Nullable>).
  // We explicitly exclude --warnaserror (too strict for REPL) and --optimize
  // (irrelevant for interactive eval).
  let fsiSafeFlags =
    sln.Projects
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
    // Always include framework DLL references from project OtherOptions
    // (e.g. ASP.NET Core, MVC) — harmless if unused, essential if needed
    yield!
      sln.Projects
      |> Seq.collect _.OtherOptions
      |> Seq.filter (fun s ->
        s.StartsWith("-r", System.StringComparison.Ordinal)
        && s.EndsWith(".dll", System.StringComparison.Ordinal)
        )
  |]
