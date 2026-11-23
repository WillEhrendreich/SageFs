namespace SageFs

open System
open System.IO
open System.Xml.Linq

[<RequireQualifiedAccess>]
type BuildPreflight =
  | Ready
  | NeedsRebuild of missing: string list
  | Unknown of reason: string

/// A build-output search. The root and the DLL name are both strings, so a
/// record names them: passing them the wrong way round is a compile error
/// rather than a search that quietly finds nothing.
type OutputSearch = { BinDir: string; DllName: string }

module BuildPreflight =
  let private xmlName (name: string) = XName.Get name

  let private hasCustomOutputPath (projectPath: string) (readText: string -> string) =
    try
      let doc = XDocument.Parse (readText projectPath)
      doc.Descendants (xmlName "OutputPath")
      |> Seq.exists (fun element -> not (String.IsNullOrWhiteSpace element.Value))
    with _ -> false

  let private conventionalGeneratedPaths (projectPath: string) =
    let dir = Path.GetDirectoryName(Path.GetFullPath projectPath)
    let name = Path.GetFileNameWithoutExtension projectPath
    [ Path.Combine(dir, "obj", "project.assets.json")
      Path.Combine(dir, "obj", name + ".fsproj.nuget.g.props") ]

  let private arcadeGeneratedPaths (projectPath: string) =
    let dir = Path.GetDirectoryName(Path.GetFullPath projectPath)
    let name = Path.GetFileNameWithoutExtension projectPath
    [ Path.Combine(dir, "artifacts", "obj", name, "project.assets.json")
      Path.Combine(dir, "artifacts", "obj", name, name + ".fsproj.nuget.g.props") ]

  let private preferredGeneratedPaths (projectPath: string) (fileExists: string -> bool) =
    let conventional = conventionalGeneratedPaths projectPath
    let arcade = arcadeGeneratedPaths projectPath
    match conventional @ arcade |> List.filter fileExists with
    | [] -> conventional
    | present ->
      if conventional |> List.exists (fun path -> List.contains path present) then conventional
      else arcade

  /// Where restore writes under a session's own artifacts path: `<artifacts>/obj/<project>/`, the layout the SDK uses when
  /// `ArtifactsPath` is set. A session built there has nothing under the project's own `obj`.
  let private sessionArtifactsGeneratedPaths (artifactsRoot: string) (projectPath: string) =
    let name = Path.GetFileNameWithoutExtension projectPath
    [ Path.Combine(artifactsRoot, "obj", name, "project.assets.json")
      Path.Combine(artifactsRoot, "obj", name, name + ".fsproj.nuget.g.props") ]

  /// Where a project's build outputs live. An artifacts path moves them out of
  /// the project's own bin; this is the one place that decides where to look,
  /// shared with the worker's own output-dir resolution.
  let outputBinDir (artifacts: ArtifactsDir) (projectPath: string) : string =
    let projectDir = Path.GetDirectoryName(Path.GetFullPath projectPath)
    match artifacts with
    | ArtifactsIn root -> Path.Combine(root, "bin", Path.GetFileNameWithoutExtension projectPath)
    | ProjectChooses -> Path.Combine(projectDir, "bin")

  /// The real filesystem implementation of `findOutputs`: any matching file
  /// anywhere under the output root, whatever config or TFM the build used.
  /// The spec is a record so the root and the DLL name cannot be swapped.
  let findOutputFiles (search: OutputSearch) : string list =
    if Directory.Exists search.BinDir then
      Directory.EnumerateFiles(search.BinDir, search.DllName, SearchOption.AllDirectories) |> Seq.toList
    else []

  /// `Ready` is a proof, not a label. A bin clear leaves the restore artifacts
  /// under obj in place, so checking only those says Ready for a project with
  /// no DLL to load; create_session then skips its recovery build and warmup
  /// faults with "Not all DLLs are found". The output check is inside the one
  /// constructor path, so no caller can hold a Ready that warmup cannot load.
  let checkUnder
    (artifacts: ArtifactsDir)
    (fileExists: string -> bool)
    (readText: string -> string)
    (findOutputs: OutputSearch -> string list)
    (target: SessionProjectTarget)
    : BuildPreflight =
    match target with
    | SessionProjectTarget.Bare -> BuildPreflight.Ready
    | SessionProjectTarget.Solution _ -> BuildPreflight.Unknown "solution preflight is not classified yet"
    | SessionProjectTarget.Project projectPath ->
      match fileExists projectPath with
      | false -> BuildPreflight.NeedsRebuild [ Path.GetFullPath projectPath ]
      | true when hasCustomOutputPath projectPath readText -> BuildPreflight.Unknown "the project declares a custom OutputPath"
      | true ->
        let required =
          match artifacts with
          | ArtifactsIn root -> sessionArtifactsGeneratedPaths root projectPath
          | ProjectChooses -> preferredGeneratedPaths projectPath fileExists
        let missing =
          required
          |> List.filter (fileExists >> not)
          |> List.distinct
        match missing with
        | _ :: _ -> BuildPreflight.NeedsRebuild missing
        | [] ->
          let binDir = outputBinDir artifacts projectPath
          let dllName = Path.GetFileNameWithoutExtension projectPath + ".dll"
          match findOutputs { BinDir = binDir; DllName = dllName } with
          | _ :: _ -> BuildPreflight.Ready
          | [] -> BuildPreflight.NeedsRebuild [ Path.Combine(binDir, dllName) ]

  let check
    (fileExists: string -> bool)
    (readText: string -> string)
    (findOutputs: OutputSearch -> string list)
    (target: SessionProjectTarget)
    : BuildPreflight =
    checkUnder ProjectChooses fileExists readText findOutputs target

  /// `checkIn` for a session whose builds write under `artifacts`.
  let checkInUnder
    (artifacts: ArtifactsDir)
    (fileExists: string -> bool)
    (readText: string -> string)
    (findOutputs: OutputSearch -> string list)
    (workingDir: string)
    (target: SessionProjectTarget)
    : BuildPreflight =
    match target with
    | SessionProjectTarget.Project path when not (Path.IsPathRooted path) ->
      checkUnder artifacts fileExists readText findOutputs (SessionProjectTarget.Project (Path.Combine(workingDir, path)))
    | SessionProjectTarget.Bare
    | SessionProjectTarget.Solution _
    | SessionProjectTarget.Project _ -> checkUnder artifacts fileExists readText findOutputs target

  let checkIn
    (fileExists: string -> bool)
    (readText: string -> string)
    (findOutputs: OutputSearch -> string list)
    (workingDir: string)
    (target: SessionProjectTarget)
    : BuildPreflight =
    checkInUnder ProjectChooses fileExists readText findOutputs workingDir target

  let describe = function
    | BuildPreflight.Ready -> "ready"
    | BuildPreflight.NeedsRebuild missing -> sprintf "needs rebuild: %s" (String.concat ", " missing)
    | BuildPreflight.Unknown reason -> sprintf "build state unknown: %s" reason
