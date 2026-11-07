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
  /// directory if needed. Public so a simulation can run the read and the write of an edit as two steps; the
  /// product only reaches it through `update`, which holds the lock across both.
  let commit (path: string) (m: Map<string, string>) : Result<unit, ConfigError> =
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

  /// One edit of a layer, as data, so the read-modify-write can be driven step by step.
  type LayerChange =
    | SetValue of key: string * raw: string
    | ClearValue of key: string

  /// The layer after one change. Clearing an absent key changes nothing.
  let applyChange (change: LayerChange) (layer: Map<string, string>) : Map<string, string> =
    match change with
    | SetValue(key, raw) -> Map.add key raw layer
    | ClearValue key -> Map.remove key layer

  /// Every edit reads a layer, changes it and writes it back, and two of them at once (two tabs saving different keys)
  /// would each write the layer they read and drop the other's key. The lock makes the three steps one. The process is
  /// the unit: the daemon is the only writer of its own layer files while it runs.
  let private gate = obj ()

  /// What a layer file that cannot be read is saved as before a write replaces it, so a hand-edited file with a typo
  /// keeps the person's other keys somewhere they can read them.
  let corruptCopyPath (path: string) : string = path + ".corrupt"

  /// Apply one change to a layer file under the lock. A layer that cannot be read is replaced, as it always was, and its
  /// bytes are kept beside it first (`corruptCopyPath`), so saving from the page repairs the file without destroying
  /// what was in it.
  let update (path: string) (change: LayerChange) : Result<unit, ConfigError> =
    lock gate (fun () ->
      let layer =
        match readLayerChecked path with
        | LayerRead.Layer layer -> Ok layer
        | LayerRead.Unreadable reason ->
          try
            File.Copy(path, corruptCopyPath path, true)
            Ok Map.empty
          with
          | :? IOException as ex -> Error (PersistFailed (sprintf "%s It could not be kept as %s: %s" reason (corruptCopyPath path) ex.Message))
          | :? UnauthorizedAccessException as ex -> Error (PersistFailed (sprintf "%s It could not be kept as %s: %s" reason (corruptCopyPath path) ex.Message))
      match layer with
      | Error why -> Error why
      | Ok layer ->
        let changed = applyChange change layer
        match changed = layer with
        | true -> Ok ()
        | false -> commit path changed)

  /// Set (or overwrite) one key in a layer, preserving every other key.
  let setKey (path: string) (key: string) (rawValue: string) : Result<unit, ConfigError> =
    update path (SetValue(key, rawValue))

  /// Clear one key from a layer so its value falls back to the next-lower
  /// layer. Clearing an absent key is a no-op success.
  let clearKey (path: string) (key: string) : Result<unit, ConfigError> =
    update path (ClearValue key)
