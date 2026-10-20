/// Which file of a NuGet package a session runs.
///
/// A package can ship a reference assembly to compile against (`ref/<tfm>/X.dll`) and a different file to run
/// (`lib/<tfm>/X.dll`). The runtime refuses to execute a reference assembly ("Reference assemblies cannot be loaded
/// for execution"), and FSI runs what it is given with `-r:`. Ionide's `PackageReferences` and the `-r:` options both
/// come from MSBuild's `Reference` items, which are the compile assets. MSBuild names the runtime assets too, as
/// `RuntimeCopyLocalItems`, resolved from the same `project.assets.json`, so a session takes the file to run from there.
/// Most packages ship no `ref/` folder, and for those the two are the same file.
module SageFs.PackageRuntimeAssets

open System
open System.IO
open System.Text.Json

open Ionide.ProjInfo.Types

/// A file MSBuild says one of a package's assemblies runs from.
type RuntimeAsset = { Package: string; Version: string; Path: string }

/// What a package reference's compile asset is run as.
type RunAs =
  /// The compile asset is also the file that runs: the package ships no separate reference assembly.
  | SameFile
  /// The package compiles against a reference assembly and runs this file instead.
  | RuntimeFile of path: string
  /// MSBuild named no runtime asset of this name for the package (a reference with its runtime assets excluded).
  | NoRuntimeAsset

/// The runtime assets in a project's items: the `RuntimeCopyLocalItems` that come from a package.
let runtimeAssetsOf (allItems: Map<string, Set<string * Map<string, string>>>) : RuntimeAsset list =
  allItems
  |> Map.tryFind "RuntimeCopyLocalItems"
  |> Option.defaultValue Set.empty
  |> Set.toList
  |> List.choose (fun (path, metadata) ->
    match Map.tryFind "NuGetPackageId" metadata, Map.tryFind "NuGetPackageVersion" metadata with
    | Some package, Some version when not (String.IsNullOrEmpty package) -> Some { Package = package; Version = version; Path = path }
    | _ -> None)

// ── The same fact, read out of a restore record ───────────────────────────────────────────────────────────
//
// `runtimeAssetsOf` above reads it out of MSBuild's items, which the manual-parse fallback does not have: it
// never runs an MSBuild evaluation. A restore writes the same information down in `obj/project.assets.json`,
// and that record is on disk for every project that has been restored.
//
// The fallback needs it because a CLASS LIBRARY's build output does not copy its packages: since .NET Core
// 3.0 only an executable's or a test project's output holds them. So a library's bin/ holds the project's own
// assembly and nothing else, the session cannot use the package at all, and the project's own code that calls
// it fails at run time.

/// The restore record of a project: `obj/project.assets.json`, or the Arcade layout's
/// `artifacts/obj/<name>/project.assets.json`. A project built under a different intermediate path
/// has neither, and then there is nothing to read.
let assetsFilesOf (projectPath: string) (fileExists: string -> bool) : string list =
  let dir = Path.GetDirectoryName (Path.GetFullPath projectPath)
  let name = Path.GetFileNameWithoutExtension projectPath
  [ Path.Combine(dir, "obj", "project.assets.json")
    Path.Combine(dir, "artifacts", "obj", name, "project.assets.json") ]
  |> List.filter fileExists

let private propertyOf (element: JsonElement) (name: string) : JsonElement option =
  let mutable found = Unchecked.defaultof<JsonElement>
  if element.TryGetProperty(name, &found) then Some found else None

let private stringOf (element: JsonElement) : string option =
  match element.ValueKind with
  | JsonValueKind.String -> Some(element.GetString())
  | _ -> None

let private properties (element: JsonElement) : (string * JsonElement) list =
  match element.ValueKind with
  | JsonValueKind.Object -> element.EnumerateObject() |> Seq.map (fun (p: JsonProperty) -> p.Name, p.Value) |> Seq.toList
  | _ -> []

/// `<id>/<version>` split into the two.
let private splitIdentity (identity: string) : string * string =
  match identity.LastIndexOf '/' with
  | -1 -> identity, ""
  | at -> identity.Substring(0, at), identity.Substring(at + 1)

/// The runtime assets a restore record names, for the frameworks asked for. An empty list means every
/// framework the record has.
///
/// The record names, for each package it resolved: the runtime assets it contributes to each framework
/// (`targets/<tfm>/<id>/<version>.runtime`), the package's own path under a package folder
/// (`libraries/<id>/<version>.path`), and the package folders to resolve it against (`packageFolders`).
///
/// `_._` is NuGet's "nothing here for this framework" placeholder, and it is not a file. A package whose
/// folder the cache does not hold has nothing the session could load, so it is left out.
let runtimeAssetsOfRestore (tfms: string list) (json: string) (directoryExists: string -> bool) : RuntimeAsset list =
  try
    use document = JsonDocument.Parse json
    let root = document.RootElement
    let folders =
      match propertyOf root "packageFolders" with
      | Some value -> properties value |> List.map fst
      | None -> []
    let libraries =
      match propertyOf root "libraries" with
      | Some value -> properties value |> Map.ofList
      | None -> Map.empty
    let targets =
      match propertyOf root "targets" with
      | Some value -> properties value |> Map.ofList
      | None -> Map.empty
    let frameworks = if tfms = [] then Map.keys targets |> Seq.toList else tfms
    frameworks
    |> List.collect (fun tfm ->
      match Map.tryFind tfm targets with
      | None -> []
      | Some target ->
        properties target
        |> List.collect (fun (identity, package) ->
          match propertyOf package "runtime" with
          | None -> []
          | Some runtime ->
            let name, version = splitIdentity identity
            let relative =
              match Map.tryFind identity libraries with
              | Some library -> (match propertyOf library "path" |> Option.bind stringOf with | Some p -> p | None -> "")
              | None -> ""
            if relative = "" then []
            else
              match folders |> List.map (fun folder -> Path.Combine(folder, relative)) |> List.tryFind directoryExists with
              | None -> []
              | Some folder ->
                properties runtime
                |> List.choose (fun (file, _) ->
                  if file = "_._" then None
                  else Some { Package = name; Version = version; Path = Path.Combine(folder, file) })))
  with
  // A record that is not JSON names no packages. It is a case, not a wildcard: the session keeps
  // the references it has instead of failing to start.
  | :? JsonException -> []

let samePath (a: string) (b: string) : bool =
  String.Equals(Path.GetFullPath a, Path.GetFullPath b, StringComparison.OrdinalIgnoreCase)

/// What one package reference runs as, given the project's runtime assets: the runtime asset of the same package, at
/// the same version, with the same file name. NuGet compares package ids without case.
let runAs (runtime: RuntimeAsset list) (reference: PackageReference) : RunAs =
  let runsFor (asset: RuntimeAsset) =
    String.Equals(asset.Package, reference.Name, StringComparison.OrdinalIgnoreCase)
    && String.Equals(asset.Version, reference.Version, StringComparison.OrdinalIgnoreCase)
    && String.Equals(Path.GetFileName asset.Path, Path.GetFileName reference.FullPath, StringComparison.OrdinalIgnoreCase)
  match runtime |> List.tryFind runsFor with
  | None -> NoRuntimeAsset
  | Some asset when samePath asset.Path reference.FullPath -> SameFile
  | Some asset -> RuntimeFile asset.Path

/// The project with every package reference that ships a reference assembly pointed at the file that runs instead,
/// in `PackageReferences` and in the `-r:` options, which name the same compile assets.
let withRuntimeAssets (project: ProjectOptions) : ProjectOptions =
  let runtime = runtimeAssetsOf project.AllItems
  let swaps =
    project.PackageReferences
    |> List.choose (fun reference ->
      match runAs runtime reference with
      | RuntimeFile path -> Some(reference.FullPath, path)
      | SameFile
      | NoRuntimeAsset -> None)
  let runtimeFor (compile: string) =
    swaps |> List.tryFind (fun (from, _) -> samePath from compile) |> Option.map snd
  match swaps with
  | [] -> project
  | _ ->
    { project with
        PackageReferences =
          project.PackageReferences
          |> List.map (fun reference ->
            match runtimeFor reference.FullPath with
            | Some path -> { reference with FullPath = path }
            | None -> reference)
        OtherOptions =
          project.OtherOptions
          |> List.map (fun option ->
            match option.StartsWith("-r:", StringComparison.Ordinal) with
            | false -> option
            | true ->
              match runtimeFor (option.Substring 3) with
              | Some path -> "-r:" + path
              | None -> option) }
