namespace SageFs

open System
open System.IO
open System.Text.Json

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
  let readLayer (path: string) : Map<string, string> =
    try
      match File.Exists path with
      | false -> Map.empty
      | true ->
        let json = File.ReadAllText path
        match JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, string>>(json) with
        | null -> Map.empty
        | dict -> dict |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
    with _ -> Map.empty

  /// Atomically replace a layer file with `m` (tmp + move), creating the
  /// directory if needed.
  let private writeLayer (path: string) (m: Map<string, string>) : Result<unit, string> =
    try
      let dir = Path.GetDirectoryName path
      match String.IsNullOrEmpty dir || Directory.Exists dir with
      | true -> ()
      | false -> Directory.CreateDirectory dir |> ignore
      let dict = Collections.Generic.Dictionary<string, string>()
      for KeyValue(k, v) in m do
        dict.[k] <- v
      let json = JsonSerializer.Serialize(dict, JsonSerializerOptions(WriteIndented = true))
      let tmp = path + ".tmp"
      File.WriteAllText(tmp, json)
      File.Move(tmp, path, true)
      Ok ()
    with ex -> Error (sprintf "failed to write %s: %s" path ex.Message)

  /// Set (or overwrite) one key in a layer, preserving every other key.
  let setKey (path: string) (key: string) (rawValue: string) : Result<unit, string> =
    readLayer path |> Map.add key rawValue |> writeLayer path

  /// Clear one key from a layer so its value falls back to the next-lower
  /// layer. Clearing an absent key is a no-op success.
  let clearKey (path: string) (key: string) : Result<unit, string> =
    let m = readLayer path
    match Map.containsKey key m with
    | false -> Ok ()
    | true -> m |> Map.remove key |> writeLayer path
