module SageFs.ProjectLoading

open System
open System.IO

open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo

open Ionide.ProjInfo.Types
open SageFs.Utils

type FileName = string
type DllName = string
type DirName = string

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
    let defaultLoader: IWorkspaceLoader = WorkspaceLoader.Create(toolsPath, [])

    logger.LogInfo "Loading solution and project references..."
    let slnProjects =
      solutions
      |> List.collect (fun s ->
        logger.LogInfo (sprintf "  Loading %s..." (Path.GetFileName s))
        defaultLoader.LoadSln s |> Seq.toList)

    let projects =
      slnProjects
      |> Seq.append (defaultLoader.LoadProjects projects)

    logger.LogInfo (sprintf "  Loaded %d project(s)." (Seq.length projects))

    let fcsProjectOptions = List.ofSeq <| FCS.mapManyOptions projects

    {
      FsProjects = fcsProjectOptions
      Projects = projects |> Seq.toList
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
    let testPackages = [ "Expecto"; "xunit"; "NUnit"; "MSTest.TestFramework"; "Microsoft.NET.Test.Sdk" ]
    proj.PackageReferences
    |> List.exists (fun pr ->
      let name = Path.GetFileNameWithoutExtension(pr.FullPath)
      testPackages |> List.exists (fun tp -> name.StartsWith(tp, StringComparison.OrdinalIgnoreCase)))

/// Filter a solution's projects to only test projects.
let discoverTestProjects (projects: ProjectOptions list) : ProjectOptions list =
  projects |> List.filter isTestProject

let solutionToFsiArgs (logger: ILogger) (_useAsp: bool) sln =
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

  [|
    "fsi"
    yield! allDlls |> Seq.map (sprintf "-r:%s")
    yield! sln.LibPaths |> Seq.map (sprintf "--lib:%s")
    yield! sln.OtherArgs
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
