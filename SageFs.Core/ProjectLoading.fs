module SageFs.ProjectLoading

open System
open System.IO

open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo

open Ionide.ProjInfo.Types
open SageFs.Utils
open SageFs.Args

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

let loadSolution (logger: ILogger) (args: Arguments list) =
  let directory =
    args
    |> List.tryPick (function
      | Dir d -> Some d
      | _ -> None)
    |> Option.defaultWith Directory.GetCurrentDirectory

  let solutions =
    match
      args
      |> List.choose (function
        | Sln s -> Some s
        | _ -> None)
    with
    | [] ->
      Directory.EnumerateFiles directory
      |> Seq.filter (fun s -> s.EndsWith(".sln", System.StringComparison.Ordinal) || s.EndsWith(".slnx", System.StringComparison.Ordinal))
      |> Seq.toList
    | s -> s |> List.map Path.GetFullPath

  let projects =
    match
      args
      |> List.choose (function
        | Proj p -> Some p
        | _ -> None)
    with
    | [] ->
      Directory.EnumerateFiles directory
      |> Seq.filter (fun s -> s.EndsWith(".fsproj", System.StringComparison.Ordinal))
      |> Seq.toList
    | s -> s |> List.map Path.GetFullPath

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

    let startupFiles =
      args
      |> List.choose (function
        | Use f -> Some(Path.GetFullPath f)
        | _ -> None)

    let references =
      args
      |> List.choose (function
        | Reference r -> Some(Path.GetFullPath r)
        | _ -> None)

    let libPaths =
      args
      |> List.collect (function
        | Lib l -> List.map Path.GetFullPath l
        | _ -> [])

    let otherArgs =
      args
      |> List.collect (function
        | Other args -> args
        | _ -> [])

    {
      FsProjects = fcsProjectOptions
      Projects = projects |> Seq.toList
      StartupFiles = startupFiles
      References = references
      LibPaths = libPaths
      OtherArgs = otherArgs
    }

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

  if List.exists (File.Exists >> not) allDlls then
    let missing = allDlls |> List.filter (File.Exists >> not)
    for dll in missing do
      logger.LogError (sprintf "Missing DLL: %s" dll)
    failwithf "Not all DLLs are found (%d missing). Please build your project before running REPL" missing.Length

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
