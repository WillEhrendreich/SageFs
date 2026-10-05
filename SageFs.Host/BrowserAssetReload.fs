module SageFs.BrowserAssetReload

open System
open System.IO
open System.Threading
open SageFs.Features.ReloadOutcome

[<Literal>]
let configFileName = "sagefs.browser-assets.json"

[<RequireQualifiedAccess>]
type ConfigLoadError =
  | InvalidJson of JsonError
  | InvalidConfiguration of BrowserAssets.ConfigError list
  | CannotRead of path: string * detail: string

module ConfigLoadError =
  let describe = function
    | ConfigLoadError.InvalidJson error -> JsonError.describe error
    | ConfigLoadError.InvalidConfiguration errors -> sprintf "Invalid browser asset configuration: %A" errors
    | ConfigLoadError.CannotRead(path, detail) -> sprintf "Cannot read %s: %s" path detail

let parseJson baseDirectory text =
  Json.deserialize<BrowserAssets.RawConfig> Json.camelCase text
  |> Result.mapError ConfigLoadError.InvalidJson
  |> Result.bind (fun raw ->
    BrowserAssets.parse baseDirectory raw
    |> Result.mapError ConfigLoadError.InvalidConfiguration)

let load workingDirectory =
  let path = Path.Combine(workingDirectory, configFileName)
  try
    File.ReadAllText path |> parseJson workingDirectory |> Result.map Some
  with
  | :? FileNotFoundException
  | :? DirectoryNotFoundException -> Ok None
  | :? IOException as error -> Error (ConfigLoadError.CannotRead(path, error.Message))
  | :? UnauthorizedAccessException as error -> Error (ConfigLoadError.CannotRead(path, error.Message))

[<RequireQualifiedAccess>]
type SaveRoute =
  | BrowserAssets of sourceFile: string
  | Existing of FileWatcher.FileChangeAction

let route config (change: FileWatcher.FileChange) =
  match config with
  | Some config when BrowserAssets.matchesSource config change.FilePath ->
    SaveRoute.BrowserAssets change.FilePath
  | Some _
  | None -> SaveRoute.Existing (FileWatcher.fileChangeAction change)

let outcome sourceFile = function
  | BrowserAssets.BuildResult.Changed assets ->
    ReloadOutcome.AssetsRebuilt(sourceFile, assets.AssetCount, assets.ContentHash)
    |> Features.ReloadBroadcast.eventOf
  | BrowserAssets.BuildResult.Unchanged -> Features.ReloadBroadcast.assetsUnchanged sourceFile
  | BrowserAssets.BuildResult.Failed reason ->
    ReloadOutcome.CompileFailed (sprintf "%s: %A" sourceFile reason)
    |> Features.ReloadBroadcast.eventOf

let createRunner config =
  let build command (cancellationToken: CancellationToken) = task {
    use budget = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
    budget.CancelAfter Timeouts.browserAssetBuild
    return! BrowserAssets.runBuild command budget.Token
  }
  BrowserAssets.createRunner { BrowserAssets.systemDependencies with Build = build } config

let rebuild run (sourceFile: string) (cancellationToken: CancellationToken) = async {
  let fileName = Path.GetFileName sourceFile
  DevReload.broadcastCompiling (Some fileName)
  let! result = run cancellationToken |> Async.AwaitTask
  let event = outcome fileName result
  Features.ReloadBroadcast.broadcastEvent event
  return event
}
