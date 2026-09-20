/// The IO edge of RuntimeCompat: reads what a project's build left on disk and what runtimes are installed, then
/// asks the pure decision core how a process for those projects should be launched. Used both for the worker
/// (in-process FSI sessions) and for the isolated FSI host (whose host major is its SDK's target framework).
module SageFs.RuntimeSelection

open System
open System.IO

/// The runtime a project needs, from the newest `<project>.runtimeconfig.json` its build left under bin/
/// (an unbuilt project has none).
let projectRuntimeRequirement (projectPath: string) : Result<RuntimeCompat.RuntimeRequirement, RuntimeCompat.RuntimeConfigError> =
  let binDir = Path.Combine(Path.GetDirectoryName projectPath, "bin")
  let configName = Path.GetFileNameWithoutExtension projectPath + ".runtimeconfig.json"
  match Directory.Exists binDir with
  | false -> Error(RuntimeCompat.ProjectNotBuilt(Path.GetFileName projectPath))
  | true ->
    let newest =
      Directory.EnumerateFiles(binDir, configName, SearchOption.AllDirectories)
      |> Seq.sortByDescending File.GetLastWriteTimeUtc
      |> Seq.tryHead
    match newest with
    | None -> Error(RuntimeCompat.RuntimeConfigNotFound(configName, binDir))
    | Some configPath ->
      try RuntimeCompat.parseRuntimeRequirement (File.ReadAllText configPath)
      with ex -> Error(RuntimeCompat.RuntimeConfigUnreadable(configPath, ex.Message))

/// The runtime majors installed next to the runtime this process is running on.
let installedRuntimeMajors () : int list =
  let runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)
  let sharedDir = Path.GetDirectoryName runtimeDir
  match Directory.Exists sharedDir with
  | false -> [ Environment.Version.Major ]
  | true ->
    Directory.EnumerateDirectories sharedDir
    |> Seq.choose (fun dir ->
      match Version.TryParse((Path.GetFileName dir).Split('-').[0]) with
      | true, version -> Some version.Major
      | false, _ -> None)
    |> Seq.distinct
    |> Seq.toList

/// How a process whose default runtime is `hostMajor` should be launched for these projects: the highest
/// requirement across the projects, decided against what this machine has.
let resolveRuntimeChoiceFor (hostMajor: int) (projects: string list) : RuntimeCompat.RuntimeChoice =
  let requirements = projects |> List.map projectRuntimeRequirement
  let known = requirements |> List.choose (function Ok r -> Some r | Error _ -> None)
  let requirement =
    match known with
    | [] ->
      match requirements with
      | Error reason :: _ -> Error reason
      | _ -> Error RuntimeCompat.NoProjects
    | _ ->
      Ok(known |> List.maxBy (fun r -> r.Major, (match r.Stability with RuntimeCompat.Stable -> 0 | RuntimeCompat.Prerelease -> 1)))
  RuntimeCompat.decide hostMajor (installedRuntimeMajors ()) requirement

/// The worker's own case: it runs on the runtime of this process.
let resolveRuntimeChoice (projects: string list) : RuntimeCompat.RuntimeChoice =
  resolveRuntimeChoiceFor Environment.Version.Major projects
