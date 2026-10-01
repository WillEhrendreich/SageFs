namespace SageFs

open System
open System.IO

/// Persistence for the settings layers (unified-settings-design.md §2.3).
/// A layer file is a flat, machine-safe JSON object of `key -> rendered raw
/// value`; the typed `SettingValue` only ever exists after a descriptor's
/// `parse` turns one of these raw strings into a legal value. Two file layers:
/// global (`<SageFsDir>/settings.json`) and per-repo
/// (`<repoRoot>/.SageFs/settings.json`).
///
/// Every read is fail-safe (a missing or malformed file resolves to the empty
/// layer, never a throw) so resolution stays total; every write is atomic
/// (tmp + move) so a crashed write can never leave a half-written layer.
[<RequireQualifiedAccess>]
module SettingsStore =

  [<Literal>]
  let FileName = "settings.json"

  /// The global (machine-wide) layer file.
  let globalPath (sageFsDir: string) : string =
    Path.Combine(sageFsDir, FileName)

  /// The per-repo override layer file.
  let repoPath (repoRoot: string) : string =
    Path.Combine(repoRoot, ".SageFs", FileName)

  /// Read one layer file into a `key -> raw value` map. Missing or malformed
  /// files resolve to the empty map — resolution must never fail because a
  /// layer file is absent or corrupt.
  /// What reading a layer file found: a layer (an absent file is the empty layer), or why the
  /// file could not be used.
  [<RequireQualifiedAccess>]
  type LayerRead =
    | Layer of Map<string, string>
    | Unreadable of reason: string

  let readLayerChecked (path: string) : LayerRead =
    try
      match File.Exists path with
      | false -> LayerRead.Layer Map.empty
      | true ->
        match Json.deserialize<Collections.Generic.Dictionary<string, string>> Json.standard (File.ReadAllText path) with
        | Result.Ok dict -> LayerRead.Layer (dict |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)
        | Result.Error error -> LayerRead.Unreadable (sprintf "%s is not a settings layer: %s" path (JsonError.describe error))
    with
    | :? IOException as ex -> LayerRead.Unreadable (sprintf "%s could not be read: %s" path ex.Message)
    | :? UnauthorizedAccessException as ex -> LayerRead.Unreadable (sprintf "%s could not be read: %s" path ex.Message)

  /// `readLayerChecked` for resolution, which must stay total: an unreadable layer is the empty
  /// layer, and the reason goes to stderr so it is never silent. A later `setKey` rewrites the file.
  let readLayer (path: string) : Map<string, string> =
    match readLayerChecked path with
    | LayerRead.Layer layer -> layer
    | LayerRead.Unreadable reason ->
      eprintfn "SageFs: settings layer ignored: %s" reason
      Map.empty

  /// Atomically replace a layer file with `m` (tmp + move), creating the
  /// directory if needed.
  let private writeLayer (path: string) (m: Map<string, string>) : Result<unit, ConfigError> =
    try
      let dir = Path.GetDirectoryName path
      match String.IsNullOrEmpty dir || Directory.Exists dir with
      | true -> ()
      | false -> Directory.CreateDirectory dir |> ignore
      let dict = Collections.Generic.Dictionary<string, string>()
      for KeyValue(k, v) in m do
        dict.[k] <- v
      let json = Json.serialize (Json.indented Json.standard) dict
      let tmp = path + ".tmp"
      File.WriteAllText(tmp, json)
      File.Move(tmp, path, true)
      Ok ()
    with ex -> Error (PersistFailed (sprintf "failed to write %s: %s" path ex.Message))

  /// Set (or overwrite) one key in a layer, preserving every other key.
  let setKey (path: string) (key: string) (rawValue: string) : Result<unit, ConfigError> =
    readLayer path |> Map.add key rawValue |> writeLayer path

  /// Clear one key from a layer so its value falls back to the next-lower
  /// layer. Clearing an absent key is a no-op success.
  let clearKey (path: string) (key: string) : Result<unit, ConfigError> =
    let m = readLayer path
    match Map.containsKey key m with
    | false -> Ok ()
    | true -> m |> Map.remove key |> writeLayer path
