namespace SageFs

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open SageFs.ProjectLoading

open SageFs.WarmUp

module internal WarmupReplayCache =
  [<Literal>]
  let SchemaVersion = 3

  type FileStamp = {
    Path: string
    Exists: bool
    Length: int64
    LastWriteTimeUtcTicks: int64
  }

  type Fingerprint = {
    SchemaVersion: int
    AutoOpenNamespaces: bool
    FsiArgs: string list
    StartupFiles: FileStamp list
    SourceFiles: FileStamp list
    AssemblyFiles: FileStamp list
  }

  type NameToOpen = {
    Name: string
    Kind: OpenableKind
  }

  type ReplayPlan = {
    Fingerprint: Fingerprint
    SourceFilesScanned: int
    AssembliesLoaded: LoadedAssembly list
    NamesToOpen: NameToOpen list
    /// Non-fatal problems found during discovery that the user should see
    /// (e.g. project DLL missing so its namespaces could not be scanned, or
    /// auto-open was ON but zero namespaces were discovered). Surfaced in the
    /// dashboard so warmup never fails silently.
    DiscoveryWarnings: string list
  }

  let private jsonOptions =
    let options =
      JsonSerializerOptions(
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
      )

    options.Converters.Add(JsonFSharpConverter())
    options

  let private normalizePath (path: string) =
    match String.IsNullOrWhiteSpace path with
    | true -> path
    | false -> Path.GetFullPath path

  let private stampFile (path: string) =
    let fullPath = normalizePath path

    match File.Exists fullPath with
    | true ->
      let info = FileInfo fullPath

      {
        Path = fullPath
        Exists = true
        Length = info.Length
        LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks
      }
    | false ->
      {
        Path = fullPath
        Exists = false
        Length = -1L
        LastWriteTimeUtcTicks = 0L
      }

  let sourceFilesForSolution (sln: Solution) =
    sln.FsProjects
    |> Seq.collect (fun project -> project.SourceFiles)
    |> Seq.filter (fun file ->
      file.EndsWith(".fs", StringComparison.Ordinal)
      || file.EndsWith(".fsx", StringComparison.Ordinal))
    |> Seq.distinct
    |> Seq.map normalizePath
    |> Seq.toList

  let assemblyFilesForSolution (sln: Solution) =
    sln.Projects
    |> List.map (fun project -> normalizePath project.TargetPath)

  let buildFingerprint
    autoOpenNamespaces
    (fsiArgs: string[])
    (startupFiles: string list)
    (sourceFiles: string list)
    (assemblyFiles: string list) =
    {
      SchemaVersion = SchemaVersion
      AutoOpenNamespaces = autoOpenNamespaces
      FsiArgs = fsiArgs |> Array.toList
      StartupFiles = startupFiles |> List.map stampFile
      SourceFiles = sourceFiles |> List.map stampFile
      AssemblyFiles = assemblyFiles |> List.map stampFile
    }

  let buildFingerprintForSolution autoOpenNamespaces (fsiArgs: string[]) (sln: Solution) =
    buildFingerprint
      autoOpenNamespaces
      fsiArgs
      sln.StartupFiles
      (sourceFilesForSolution sln)
      (assemblyFilesForSolution sln)

  let createPlan fingerprint sourceFilesScanned assembliesLoaded namesToOpen discoveryWarnings =
    {
      Fingerprint = fingerprint
      SourceFilesScanned = sourceFilesScanned
      AssembliesLoaded = assembliesLoaded
      NamesToOpen =
        namesToOpen
        |> List.map (fun (name, kind) ->
          {
            Name = name
            Kind = kind
          })
      DiscoveryWarnings = discoveryWarnings
    }

  let namePairs (plan: ReplayPlan) =
    plan.NamesToOpen
    |> List.map (fun entry -> entry.Name, entry.Kind)

  let tryGetCachePath (sln: Solution) =
    sln.Projects
    |> List.tryHead
    |> Option.bind (fun project ->
      let projectDir = Path.GetDirectoryName(project.ProjectFileName)

      match String.IsNullOrWhiteSpace projectDir with
      | true -> None
      | false ->
        Path.Combine(projectDir, ".SageFs", "warmup-replay-cache.json")
        |> normalizePath
        |> Some)

  let tryLoad (path: string) =
    try
      match File.Exists path with
      | false -> None
      | true ->
        let json = File.ReadAllText path
        let plan = JsonSerializer.Deserialize<ReplayPlan>(json, jsonOptions)

        match isNull (box plan) with
        | true -> None
        | false -> Some plan
    with _ ->
      None

  let tryLoadValidPlan (path: string) (fingerprint: Fingerprint) =
    match tryLoad path with
    | Some plan when plan.Fingerprint = fingerprint -> Some plan
    | Some _
    | None -> None

  let save (path: string) (plan: ReplayPlan) =
    let directory = Path.GetDirectoryName path

    match String.IsNullOrWhiteSpace directory with
    | true -> ()
    | false -> Directory.CreateDirectory(directory) |> ignore

    JsonSerializer.Serialize(plan, jsonOptions)
    |> fun json -> File.WriteAllText(path, json)

  let trySave (path: string) (plan: ReplayPlan) =
    try
      save path plan
      Ok ()
    with ex ->
      Error ex.Message
