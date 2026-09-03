namespace SageFs

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open SageFs.ProjectLoading

open SageFs.WarmUp

module internal WarmupReplayCache =
  [<Literal>]
  let SchemaVersion = 4

  type FileStamp = {
    Path: string
    Exists: bool
    Length: int64
    LastWriteTimeUtcTicks: int64
  }

  /// Project-definition files (.fsproj) the warmup plan depends on. Their
  /// CONTENT is hashed, not just stamped: a PackageReference version bump, a
  /// project-to-project reference added/removed, or an SDK change can leave a
  /// file's path/length/mtime untouched while completely changing which
  /// namespaces the built assembly exposes. Serving a stale plan against a
  /// different dependency set is exactly the failure the fingerprint exists to
  /// prevent, so project content is part of the key.
  type ProjectFileStamp = {
    Path: string
    Exists: bool
    ContentHash: string
  }

  type Fingerprint = {
    SchemaVersion: int
    AutoOpenNamespaces: bool
    FsiArgs: string list
    StartupFiles: FileStamp list
    SourceFiles: FileStamp list
    AssemblyFiles: FileStamp list
    ProjectFiles: ProjectFileStamp list
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
    /// The project-definition (.fsproj/.sln) files whose content produced the
    /// plan. Persisted alongside the fingerprint so a dependency-set change
    /// (PackageReference version bump, added project reference) that rewrites
    /// a project file — while leaving file stamps identical — can never serve
    /// a stale plan: the content hash in the fingerprint moves, the cache
    /// misses, and discovery reruns.
    ProjectFileNames: string list
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

  let private sha256Hex (data: byte[]) : string =
    use sha = System.Security.Cryptography.SHA256.Create()
    sha.ComputeHash(data)
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""

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

  /// Content stamp for a project-definition file: a SHA-256 of the bytes when
  /// present, so dependency-version changes that keep path/size/mtime intact
  /// still invalidate the replay cache.
  let private stampProjectFile (path: string) =
    let fullPath = normalizePath path

    match File.Exists fullPath with
    | true ->
      {
        Path = fullPath
        Exists = true
        ContentHash = sha256Hex (File.ReadAllBytes fullPath)
      }
    | false ->
      {
        Path = fullPath
        Exists = false
        ContentHash = ""
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

  /// Project-definition files whose content shapes the warmup plan: the
  /// .fsproj/.sln files backing the session's FSharpProjectOptions, plus every
  /// FSharpProjectOptions.ProjectFileName the loader produced (FCS
  /// mapManyOptions flattens a solution's project references, so transitive
  /// referenced .fsproj files appear here too).
  let projectFilesForSolution (sln: Solution) =
    sln.FsProjects
    |> Seq.map (fun project -> project.ProjectFileName)
    |> Seq.filter (fun path -> not (String.IsNullOrWhiteSpace path))
    |> Seq.distinct
    |> Seq.sort
    |> Seq.map normalizePath
    |> Seq.toList

  let buildFingerprint
    autoOpenNamespaces
    (fsiArgs: string[])
    (startupFiles: string list)
    (sourceFiles: string list)
    (assemblyFiles: string list)
    (projectFiles: string list) =
    {
      SchemaVersion = SchemaVersion
      AutoOpenNamespaces = autoOpenNamespaces
      FsiArgs = fsiArgs |> Array.toList
      StartupFiles = startupFiles |> List.map stampFile
      SourceFiles = sourceFiles |> List.map stampFile
      AssemblyFiles = assemblyFiles |> List.map stampFile
      ProjectFiles = projectFiles |> List.map stampProjectFile
    }

  let buildFingerprintForSolution autoOpenNamespaces (fsiArgs: string[]) (sln: Solution) =
    buildFingerprint
      autoOpenNamespaces
      fsiArgs
      sln.StartupFiles
      (sourceFilesForSolution sln)
      (assemblyFilesForSolution sln)
      (projectFilesForSolution sln)

  let createPlan fingerprint sourceFilesScanned assembliesLoaded projectFileNames namesToOpen discoveryWarnings =
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
      ProjectFileNames = projectFileNames
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
