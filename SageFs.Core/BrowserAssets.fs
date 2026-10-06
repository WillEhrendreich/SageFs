namespace SageFs

open System
open System.Diagnostics
open System.IO
open System.IO.Enumeration
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

module BrowserAssets =
  [<CLIMutable>]
  type RawBuildCommand = {
    Executable: string
    Arguments: string list
    WorkingDirectory: string
  }

  [<CLIMutable>]
  type RawAssetRoot = {
    Directory: string
    Pattern: string
  }

  [<CLIMutable>]
  type RawConfig = {
    Build: RawBuildCommand
    SourceRoots: string list
    ExcludedSourceRoots: string list option
    AssetRoots: RawAssetRoot list
  }

  [<RequireQualifiedAccess>]
  type ConfigError =
    | Required of field: string
    | InvalidPath of field: string * path: string
    | InvalidPattern of pattern: string
    | InvalidArgument of index: int

  type BuildCommand = private {
    Executable: string
    Arguments: string list
    WorkingDirectory: string
  }

  type AssetRoot = private {
    Directory: string
    Pattern: string
  }

  type Config = private {
    Command: BuildCommand
    Sources: string list
    ExcludedSources: string list
    Assets: AssetRoot list
  }

  [<RequireQualifiedAccess>]
  type BuildFailure =
    | SnapshotFailed of message: string
    | AssetDirectoryMissing of directories: string list
    | BuildFailed of exitCode: int * output: string
    | BuildStartFailed of message: string
    | Cancelled

  type AssetChange = {
    ContentHash: string
    AssetCount: int
  }

  [<RequireQualifiedAccess>]
  type BuildResult =
    | Changed of AssetChange
    | Unchanged
    | Failed of BuildFailure

  type Snapshot = private {
    Files: Map<string, string>
    MissingDirectories: string list
  }

  type Dependencies = {
    Snapshot: Config -> Result<Snapshot, BuildFailure>
    Build: BuildCommand -> CancellationToken -> Task<Result<unit, BuildFailure>>
  }

  let pathComparison =
    if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
    else StringComparison.Ordinal

  let parsePath baseDirectory field (path: string) =
    if String.IsNullOrWhiteSpace path then Error [ ConfigError.Required field ]
    else
      try
        Ok (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, baseDirectory)))
      with
      | :? ArgumentException
      | :? NotSupportedException
      | :? PathTooLongException -> Error [ ConfigError.InvalidPath (field, path) ]

  let collectResults results =
    let successes, failures =
      results
      |> List.fold (fun (values, errors) result ->
        match result with
        | Ok value -> value :: values, errors
        | Error reasons -> values, List.rev reasons @ errors) ([], [])
    match failures with
    | [] -> Ok (List.rev successes)
    | reasons -> Error (List.rev reasons)

  let parse baseDirectory (raw: RawConfig) : Result<Config, ConfigError list> =
    if isNull (box raw) then Error [ ConfigError.Required "browserAssets" ]
    elif isNull (box raw.Build) then Error [ ConfigError.Required "build" ]
    elif String.IsNullOrWhiteSpace raw.Build.Executable then Error [ ConfigError.Required "build.executable" ]
    elif raw.Build.Executable.Contains '\000' then Error [ ConfigError.InvalidPath ("build.executable", raw.Build.Executable) ]
    elif isNull (box raw.Build.Arguments) then Error [ ConfigError.Required "build.arguments" ]
    else
      let invalidArguments =
        raw.Build.Arguments
        |> List.indexed
        |> List.choose (fun (index, value) ->
          if isNull value || value.Contains '\000' then Some (ConfigError.InvalidArgument index)
          else None)
      let roots =
        if isNull (box raw.SourceRoots) || List.isEmpty raw.SourceRoots then
          Error [ ConfigError.Required "sourceRoots" ]
        else raw.SourceRoots |> List.map (parsePath baseDirectory "sourceRoots") |> collectResults
      let excludedRoots =
        match raw.ExcludedSourceRoots with
        | None -> Ok []
        | Some paths when isNull (box paths) -> Ok []
        | Some paths -> paths |> List.map (parsePath baseDirectory "excludedSourceRoots") |> collectResults
      let assets =
        if isNull (box raw.AssetRoots) || List.isEmpty raw.AssetRoots then
          Error [ ConfigError.Required "assetRoots" ]
        else
          raw.AssetRoots
          |> List.map (fun root ->
            if isNull (box root) then Error [ ConfigError.Required "assetRoots" ]
            elif String.IsNullOrWhiteSpace root.Pattern || root.Pattern.IndexOfAny [| '/'; '\\'; '\000' |] >= 0 then
              Error [ ConfigError.InvalidPattern root.Pattern ]
            else
              parsePath baseDirectory "assetRoots.directory" root.Directory
              |> Result.map (fun directory -> { Directory = directory; Pattern = root.Pattern }: AssetRoot))
          |> collectResults
      match parsePath baseDirectory "build.workingDirectory" raw.Build.WorkingDirectory, roots, excludedRoots, assets, invalidArguments with
      | Ok workingDirectory, Ok sources, Ok excludedSources, Ok assetRoots, [] ->
        Ok {
          Command = { Executable = raw.Build.Executable; Arguments = raw.Build.Arguments; WorkingDirectory = workingDirectory }
          Sources = List.distinct sources
          ExcludedSources = List.distinct excludedSources
          Assets = List.distinct assetRoots
        }
      | workingDirectory, sources, excludedSources, assetRoots, arguments ->
        let errors result = match result with Ok _ -> [] | Error reasons -> reasons
        Error (errors workingDirectory @ errors sources @ errors excludedSources @ errors assetRoots @ arguments)

  let sourceRoots config = config.Sources
  let excludedSourceRoots config = config.ExcludedSources
  let buildCommand config = config.Command
  let commandExecutable (command: BuildCommand) = command.Executable
  let commandArguments (command: BuildCommand) = command.Arguments
  let commandWorkingDirectory (command: BuildCommand) = command.WorkingDirectory

  let containsPath root (path: string) =
    path.Equals(root, pathComparison)
    || path.StartsWith(Path.TrimEndingDirectorySeparator root + string Path.DirectorySeparatorChar, pathComparison)

  let isExcludedSource config path =
    match parsePath config.Command.WorkingDirectory "sourceFile" path with
    | Error _ -> false
    | Ok fullPath ->
      config.Sources |> List.exists (fun root -> containsPath root fullPath)
      && config.ExcludedSources |> List.exists (fun root -> containsPath root fullPath)

  let matchesSource config path =
    match parsePath config.Command.WorkingDirectory "sourceFile" path with
    | Error _ -> false
    | Ok fullPath ->
      config.Sources
      |> List.exists (fun root ->
        let relative = Path.GetRelativePath(root, fullPath)
        let generated =
          relative.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
          |> Array.exists (fun segment ->
            segment.Equals("bin", pathComparison) || segment.Equals("obj", pathComparison))
        containsPath root fullPath && not generated)
      && not (config.Assets |> List.exists (fun root -> containsPath root.Directory fullPath))
      && not (isExcludedSource config fullPath)

  let contentHash (content: byte array) =
    SHA256.HashData content |> Convert.ToHexString

  let snapshotEntries (entries: (string * byte array) seq) =
    { Files = entries |> Seq.map (fun (path, content) -> path.Replace('\\', '/'), contentHash content) |> Map.ofSeq
      MissingDirectories = [] }

  let fingerprint snapshot =
    use content = new MemoryStream()
    use writer = new BinaryWriter(content, Encoding.UTF8, true)
    for KeyValue(path, hash) in snapshot.Files do
      writer.Write path
      writer.Write hash
    writer.Flush()
    { ContentHash = contentHash (content.ToArray())
      AssetCount = snapshot.Files.Count }

  let completeBuild baseline observed =
    match observed with
    | Error reason -> baseline, BuildResult.Failed reason
    | Ok next when not (List.isEmpty next.MissingDirectories) ->
      baseline, BuildResult.Failed (BuildFailure.AssetDirectoryMissing next.MissingDirectories)
    | Ok next when fingerprint next = fingerprint baseline -> next, BuildResult.Unchanged
    | Ok next -> next, BuildResult.Changed (fingerprint next)

  let snapshotFiles config =
    try
      let files, missing =
        config.Assets
        |> List.fold (fun (files, missing) root ->
          if not (Directory.Exists root.Directory) then files, root.Directory :: missing
          else
            let matched =
              Directory.EnumerateFiles(root.Directory, "*", SearchOption.AllDirectories)
              |> Seq.filter (fun path ->
                FileSystemName.MatchesSimpleExpression(root.Pattern.AsSpan(), (Path.GetFileName path).AsSpan(), OperatingSystem.IsWindows()))
              |> Seq.map (fun path ->
                use content = File.OpenRead path
                let hash = SHA256.HashData content |> Convert.ToHexString
                Path.GetRelativePath(config.Command.WorkingDirectory, path).Replace('\\', '/'), hash)
              |> Seq.toList
            (matched @ files), missing) ([], [])
      Ok { Files = Map.ofList files; MissingDirectories = List.rev missing }
    with ex -> Error (BuildFailure.SnapshotFailed ex.Message)

  let runBuild (command: BuildCommand) (cancellation: CancellationToken) = task {
    use compiler = new Process()
    compiler.StartInfo <- ProcessStartInfo(
      FileName = command.Executable,
      WorkingDirectory = command.WorkingDirectory,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true)
    command.Arguments |> List.iter compiler.StartInfo.ArgumentList.Add
    ProcessEnvironment.applyTo compiler.StartInfo []
    try
      cancellation.ThrowIfCancellationRequested()
      if not (compiler.Start()) then
        return Error (BuildFailure.BuildStartFailed "The asset build process did not start.")
      else
        let output = compiler.StandardOutput.ReadToEndAsync()
        let errors = compiler.StandardError.ReadToEndAsync()
        try
          do! compiler.WaitForExitAsync cancellation
          let! stdout = output
          let! stderr = errors
          if compiler.ExitCode = 0 then return Ok ()
          else return Error (BuildFailure.BuildFailed (compiler.ExitCode, stdout + stderr))
        with :? OperationCanceledException ->
          try
            if not compiler.HasExited then compiler.Kill true
          with
          | :? InvalidOperationException
          | :? System.ComponentModel.Win32Exception -> ()
          do! compiler.WaitForExitAsync()
          return Error BuildFailure.Cancelled
    with
    | :? OperationCanceledException -> return Error BuildFailure.Cancelled
    | ex -> return Error (BuildFailure.BuildStartFailed ex.Message)
  }

  let systemDependencies = { Snapshot = snapshotFiles; Build = runBuild }

  let createRunner dependencies config =
    let gate = new SemaphoreSlim(1, 1)
    let baseline = ref (dependencies.Snapshot config)
    fun (cancellation: CancellationToken) -> task {
      try
        do! gate.WaitAsync cancellation
        try
          let initial =
            match baseline.Value with
            | Ok snapshot -> Ok snapshot
            | Error _ -> dependencies.Snapshot config
          match initial with
          | Error failure -> return BuildResult.Failed failure
          | Ok previous ->
            let! built = dependencies.Build config.Command cancellation
            let observed = built |> Result.bind (fun () -> dependencies.Snapshot config)
            let next, result = completeBuild previous observed
            baseline.Value <- Ok next
            return result
        finally
          gate.Release() |> ignore
      with
      | :? OperationCanceledException -> return BuildResult.Failed BuildFailure.Cancelled
      | ex -> return BuildResult.Failed (BuildFailure.BuildStartFailed ex.Message)
    }
