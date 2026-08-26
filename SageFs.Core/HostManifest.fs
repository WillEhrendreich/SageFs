namespace SageFs

open System
open System.IO
open System.Text.Json

/// The vetted-manifest mechanism for the FSI host process directory.
///
/// The host's directory must contain ONLY the assemblies listed in its
/// `host-manifest.json` (plus the manifest itself). This is the structural
/// guarantee that the host's probing world contains no dashboard-only deps
/// (Falco, OpenTelemetry, ...) — the source of the 0x80131040 collisions.
///
/// Both the host's startup check (fail-closed: refuse to start if the dir
/// contains anything unexpected) and the CI test use these pure functions.
module HostManifest =

  /// The manifest file name, expected at the host's directory root.
  let manifestFileName = "host-manifest.json"

  /// The parsed manifest: the exact set of allowed file names.
  type Manifest = {
    /// e.g. "0.6.284"
    Version: string
    /// e.g. "net10.0"
    TargetFramework: string
    /// Exact file names allowed in the host directory (DLLs, exe, runtimeconfig,
    /// deps.json, the manifest itself is always allowed).
    AllowedFiles: string list
  }

  /// Parse a manifest from raw JSON. `Error` on malformed content — a bad
  /// manifest is a fail-closed condition (never guess).
  let parse (json: string) : Result<Manifest, string> =
    try
      use doc = JsonDocument.Parse json
      let root = doc.RootElement
      let getStr (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        match root.TryGetProperty(name, &value) with
        | true when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None
      let version =
        match getStr "version" with
        | Some v -> v
        | None -> failwith "missing 'version'"
      let tfm =
        match getStr "targetFramework" with
        | Some v -> v
        | None -> failwith "missing 'targetFramework'"
      let allowed =
        let mutable value = Unchecked.defaultof<JsonElement>
        match root.TryGetProperty("allowedFiles", &value) with
        | true when value.ValueKind = JsonValueKind.Array ->
          value.EnumerateArray()
          |> Seq.choose (fun e ->
            match e.ValueKind with
            | JsonValueKind.String -> Some(e.GetString())
            | _ -> None)
          |> Seq.toList
        | _ -> failwith "missing 'allowedFiles' array"
      Ok { Version = version; TargetFramework = tfm; AllowedFiles = allowed }
    with ex ->
      Error(sprintf "invalid host-manifest.json: %s" ex.Message)

  /// Load and parse the manifest from a directory.
  let load (dir: string) : Result<Manifest, string> =
    let path = Path.Combine(dir, manifestFileName)
    match File.Exists path with
    | false -> Error(sprintf "host-manifest.json not found in %s" dir)
    | true ->
      try File.ReadAllText path |> parse
      with ex -> Error(sprintf "failed to read host-manifest.json: %s" ex.Message)

  /// Verify a directory against a manifest. Fail-closed: ANY file in the
  /// directory that is not in the allowed set (and not the manifest itself)
  /// is a violation. Returns the offending file names.
  let verify (manifest: Manifest) (dir: string) : Result<unit, string list> =
    let actual =
      try Directory.GetFiles(dir) |> Array.map Path.GetFileName |> Array.toList
      with _ -> []
    let offenders =
      actual
      |> List.filter (fun f -> f <> manifestFileName)
      |> List.filter (fun f -> not (List.contains f manifest.AllowedFiles))
    match offenders with
    | [] -> Ok()
    | bad -> Error bad

  /// Full check: load the manifest from the dir, then verify the dir against it.
  let check (dir: string) : Result<unit, string> =
    match load dir with
    | Error e -> Error e
    | Ok manifest ->
      match verify manifest dir with
      | Ok () -> Ok()
      | Error offenders ->
        Error(
          sprintf
            "host directory %s contains %d file(s) outside the vetted manifest: %s"
            dir (List.length offenders) (String.Join(", ", offenders)))
