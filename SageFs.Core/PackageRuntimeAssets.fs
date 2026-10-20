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
