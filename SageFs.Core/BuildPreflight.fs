namespace SageFs

open System
open System.IO
open System.Xml.Linq

[<RequireQualifiedAccess>]
type BuildPreflight =
  | Ready
  | NeedsRebuild of missing: string list
  | Unknown of reason: string

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

  /// `check` for a session whose builds write under `artifacts`: a project built into its artifacts path has its restore output
  /// there, so the files that prove it was built are looked for there and not under the project's own `obj`.
  let checkUnder
    (artifacts: ArtifactsDir)
    (fileExists: string -> bool)
    (readText: string -> string)
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
        | [] -> BuildPreflight.Ready
        | missing -> BuildPreflight.NeedsRebuild missing

  let check
    (fileExists: string -> bool)
    (readText: string -> string)
    (target: SessionProjectTarget)
    : BuildPreflight =
    checkUnder ProjectChooses fileExists readText target

  /// `checkIn` for a session whose builds write under `artifacts`.
  let checkInUnder
    (artifacts: ArtifactsDir)
    (fileExists: string -> bool)
    (readText: string -> string)
    (workingDir: string)
    (target: SessionProjectTarget)
    : BuildPreflight =
    match target with
    | SessionProjectTarget.Project path when not (Path.IsPathRooted path) ->
      checkUnder artifacts fileExists readText (SessionProjectTarget.Project (Path.Combine(workingDir, path)))
    | _ -> checkUnder artifacts fileExists readText target

  let checkIn
    (fileExists: string -> bool)
    (readText: string -> string)
    (workingDir: string)
    (target: SessionProjectTarget)
    : BuildPreflight =
    checkInUnder ProjectChooses fileExists readText workingDir target

  /// Whether the project's own build output exists. Restore artifacts under obj
  /// survive a `bin` clear, so checking only those reports Ready for a project
  /// with no DLLs to load; create_session then skips its recovery build and
  /// warmup faults with "Not all DLLs are found".
  let checkOutputs (fileExists: string -> bool) (target: SessionProjectTarget) : BuildPreflight =
    match target with
    | SessionProjectTarget.Bare -> BuildPreflight.Ready
    | SessionProjectTarget.Solution _ -> BuildPreflight.Unknown "solution preflight is not classified yet"
    | SessionProjectTarget.Project projectPath ->
      let basePreflight = check fileExists (fun _ -> "<Project />") target
      match basePreflight with
      | BuildPreflight.NeedsRebuild _ -> basePreflight
      | BuildPreflight.Unknown _ -> basePreflight
      | BuildPreflight.Ready ->
        let dir = Path.GetDirectoryName(Path.GetFullPath projectPath)
        let name = Path.GetFileNameWithoutExtension projectPath
        let outputs =
          [ "Debug"; "Release" ]
          |> List.collect (fun config -> [ "net11.0"; "net10.0" ] |> List.map (fun tfm -> Path.Combine(dir, "bin", config, tfm, name + ".dll")))
        match outputs |> List.filter fileExists with
        | _ :: _ -> BuildPreflight.Ready
        | [] -> BuildPreflight.NeedsRebuild (outputs |> List.filter (fileExists >> not))

  /// `checkOutputs` with relative project paths resolved against `workingDir`,
  /// the shape the daemon's create path already uses for its restore check.
  let checkOutputsIn (fileExists: string -> bool) (workingDir: string) (target: SessionProjectTarget) : BuildPreflight =
    match target with
    | SessionProjectTarget.Project path when not (Path.IsPathRooted path) ->
      checkOutputs fileExists (SessionProjectTarget.Project (Path.Combine(workingDir, path)))
    | SessionProjectTarget.Bare
    | SessionProjectTarget.Solution _
    | SessionProjectTarget.Project _ -> checkOutputs fileExists target

  /// Missing output DLLs for a set of targets, for the create-session path that
  /// must not skip its recovery build when bin was cleared but obj remains.
  let missingOutputsIn (fileExists: string -> bool) (workingDir: string) (targets: SessionProjectTarget list) : string list =
    targets
    |> List.collect (fun target ->
      match checkOutputsIn fileExists workingDir target with
      | BuildPreflight.NeedsRebuild missing -> missing
      | BuildPreflight.Ready | BuildPreflight.Unknown _ -> [])

  let describe = function
    | BuildPreflight.Ready -> "ready"
    | BuildPreflight.NeedsRebuild missing -> sprintf "needs rebuild: %s" (String.concat ", " missing)
    | BuildPreflight.Unknown reason -> sprintf "build state unknown: %s" reason
