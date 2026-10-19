module SageFs.Tests.PackageRuntimeAssetsTests

// A package that ships a reference assembly (`ref/<tfm>/X.dll`) next to the file that runs (`lib/<tfm>/X.dll`) used
// to reach FSI as its reference assembly, because Ionide's `PackageReferences` and the `-r:` options are MSBuild's
// compile assets. FSI then failed every use of the package with "Reference assemblies cannot be loaded for execution"
// (SkiaSharp 4.153.1). These pin that a session runs the runtime asset MSBuild names in `RuntimeCopyLocalItems`.

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Ionide.ProjInfo.Types
open SageFs
open SageFs.PackageRuntimeAssets
open SageFs.Tests.SharedGenerators

let nugetRoot = Path.Combine(Path.GetTempPath(), "sagefs-package-runtime-assets", "packages")

let assetPath (package: string) (version: string) (folder: string) (file: string) =
  Path.Combine(nugetRoot, package.ToLowerInvariant(), version, folder, "net10.0", file)

let runtimeItem (package: string) (version: string) (path: string) : string * Map<string, string> =
  path,
  Map.ofList
    [ "NuGetPackageId", package
      "NuGetPackageVersion", version
      "AssetType", "runtime"
      "CopyLocal", "true"
      "DestinationSubPath", Path.GetFileName path ]

let project (references: PackageReference list) (otherOptions: string list) (runtime: (string * Map<string, string>) list) : ProjectOptions =
  { ProjectId = None
    ProjectFileName = Path.Combine(nugetRoot, "App", "App.fsproj")
    TargetFramework = "net10.0"
    SourceFiles = []
    OtherOptions = otherOptions
    ReferencedProjects = []
    PackageReferences = references
    LoadTime = DateTime.UtcNow
    TargetPath = Path.Combine(nugetRoot, "App", "bin", "App.dll")
    TargetRefPath = None
    ProjectOutputType = ProjectOutputType.Library
    ProjectSdkInfo =
      { IsTestProject = false
        Configuration = "Debug"
        IsPackable = false
        TargetFramework = "net10.0"
        TargetFrameworkIdentifier = ".NETCoreApp"
        TargetFrameworkVersion = "v10.0"
        MSBuildAllProjects = []
        MSBuildToolsVersion = ""
        ProjectAssetsFile = ""
        RestoreSuccess = true
        Configurations = []
        TargetFrameworks = []
        RunArguments = None
        RunCommand = None
        IsPublishable = None }
    Items = []
    Properties = []
    CustomProperties = []
    AllProperties = Map.empty
    AllItems =
      match runtime with
      | [] -> Map.empty
      | items -> Map.ofList [ "RuntimeCopyLocalItems", Set.ofList items ]
    Analyzers = [] }

let frameworkRef = "-r:" + Path.Combine(Path.GetTempPath(), "packs", "Microsoft.NETCore.App.Ref", "ref", "net10.0", "System.Runtime.dll")

/// How a generated package lays out the one assembly it ships.
type Layout =
  /// `ref/` to compile against, `lib/` to run.
  | RefAndLib
  /// `lib/` only: the compile asset is the runtime asset.
  | LibOnly
  /// A compile asset and no runtime asset at all (`ExcludeAssets="runtime"`).
  | CompileOnly

type GeneratedPackage = { Name: string; Version: string; File: string; Layout: Layout }

let compileAsset (p: GeneratedPackage) =
  match p.Layout with
  | RefAndLib
  | CompileOnly -> assetPath p.Name p.Version "ref" p.File
  | LibOnly -> assetPath p.Name p.Version "lib" p.File

let runtimeAsset (p: GeneratedPackage) =
  match p.Layout with
  | RefAndLib
  | LibOnly -> Some(assetPath p.Name p.Version "lib" p.File)
  | CompileOnly -> None

/// Packages with distinct names (one reference each, as MSBuild reports them); a small pool of file names, so two
/// packages often ship an assembly of the same name.
let genPackages : Gen<GeneratedPackage list> =
  gen {
    let! count = Gen.choose (0, 6)
    let! files = Gen.listOfLength count (Gen.elements [ "Shared.dll"; "Skia.dll"; "Other.dll" ])
    let! versions = Gen.listOfLength count (Gen.elements [ "1.0.0"; "4.153.1" ])
    let! layouts = Gen.listOfLength count (Gen.elements [ RefAndLib; LibOnly; CompileOnly ])
    return
      List.zip3 files versions layouts
      |> List.mapi (fun i (file, version, layout) -> { Name = sprintf "Pkg%d" i; Version = version; File = file; Layout = layout })
  }

let projectOf (packages: GeneratedPackage list) : ProjectOptions =
  let references =
    packages |> List.map (fun p -> { Name = p.Name; Version = p.Version; FullPath = compileAsset p })
  let options =
    [ yield "--debug:portable"
      yield frameworkRef
      for p in packages do
        yield "--define:TRACE"
        yield "-r:" + compileAsset p ]
  let runtime =
    packages |> List.choose (fun p -> runtimeAsset p |> Option.map (runtimeItem p.Name p.Version))
  project references options runtime

let skiaCompile = assetPath "SkiaSharp" "4.153.1" "ref" "SkiaSharp.dll"
let skiaRuntime = assetPath "SkiaSharp" "4.153.1" "lib" "SkiaSharp.dll"

/// The shape of the report: SkiaSharp compiles against ref/net10.0 and runs lib/net10.0.
let skiaProject () =
  project
    [ { Name = "SkiaSharp"; Version = "4.153.1"; FullPath = skiaCompile } ]
    [ "--debug:portable"; frameworkRef; "-r:" + skiaCompile; "--optimize-" ]
    [ runtimeItem "SkiaSharp" "4.153.1" skiaRuntime ]

[<Tests>]
let tests =
  testList "PackageRuntimeAssets" [
    testList "a package that ships a reference assembly" [
      test "its package reference names the file that runs, not the reference assembly" {
        (withRuntimeAssets (skiaProject ())).PackageReferences
        |> List.map _.FullPath
        |> Expect.equal "SkiaSharp should run lib/net10.0, as dotnet build's output does" [ skiaRuntime ]
      }

      test "its -r: option names the file that runs, and no other option moves" {
        (withRuntimeAssets (skiaProject ())).OtherOptions
        |> Expect.equal "only the SkiaSharp -r: changes" [ "--debug:portable"; frameworkRef; "-r:" + skiaRuntime; "--optimize-" ]
      }

      test "runAs names the runtime file" {
        { Name = "SkiaSharp"; Version = "4.153.1"; FullPath = skiaCompile }
        |> runAs (runtimeAssetsOf (skiaProject ()).AllItems)
        |> Expect.equal "the lib/ file is what runs" (RuntimeFile skiaRuntime)
      }

      test "the package id is matched without case, as NuGet does" {
        let items = Map.ofList [ "RuntimeCopyLocalItems", Set.ofList [ runtimeItem "skiasharp" "4.153.1" skiaRuntime ] ]
        { Name = "SkiaSharp"; Version = "4.153.1"; FullPath = skiaCompile }
        |> runAs (runtimeAssetsOf items)
        |> Expect.equal "a lower-case NuGetPackageId is the same package" (RuntimeFile skiaRuntime)
      }
    ]

    testList "what is not a package's runtime asset" [
      test "a file of the same name from another package is not used" {
        let other = assetPath "SkiaSharp.Fork" "4.153.1" "lib" "SkiaSharp.dll"
        let items = Map.ofList [ "RuntimeCopyLocalItems", Set.ofList [ runtimeItem "SkiaSharp.Fork" "4.153.1" other ] ]
        { Name = "SkiaSharp"; Version = "4.153.1"; FullPath = skiaCompile }
        |> runAs (runtimeAssetsOf items)
        |> Expect.equal "another package's SkiaSharp.dll is not this package's runtime asset" NoRuntimeAsset
      }

      test "the same package at another version is not used" {
        let older = assetPath "SkiaSharp" "3.119.0" "lib" "SkiaSharp.dll"
        let items = Map.ofList [ "RuntimeCopyLocalItems", Set.ofList [ runtimeItem "SkiaSharp" "3.119.0" older ] ]
        { Name = "SkiaSharp"; Version = "4.153.1"; FullPath = skiaCompile }
        |> runAs (runtimeAssetsOf items)
        |> Expect.equal "a different version is a different package" NoRuntimeAsset
      }

      test "a runtime item that no package owns is not a package's runtime asset" {
        let items = Map.ofList [ "RuntimeCopyLocalItems", Set.ofList [ skiaRuntime, Map.ofList [ "AssetType", "runtime" ] ] ]
        runtimeAssetsOf items |> Expect.isEmpty "an item without NuGetPackageId came from somewhere else"
      }

      test "a project with no runtime items is left as it is" {
        let compileOnly =
          project [ { Name = "SkiaSharp"; Version = "4.153.1"; FullPath = skiaCompile } ] [ "-r:" + skiaCompile ] []
        withRuntimeAssets compileOnly
        |> Expect.equal "nothing names a file to run instead" compileOnly
      }
    ]

    testList "properties" [
      testPropertyWithConfig propConfig "every package runs its runtime asset when it has one, and its compile asset otherwise" <|
        Prop.forAll (Arb.fromGen genPackages) (fun packages ->
          let result = withRuntimeAssets (projectOf packages)
          let expected = packages |> List.map (fun p -> runtimeAsset p |> Option.defaultValue (compileAsset p))
          let actualReferences = result.PackageReferences |> List.map _.FullPath
          let actualOptions = result.OtherOptions |> List.filter (fun o -> o.StartsWith "-r:" && o <> frameworkRef) |> List.map (fun o -> o.Substring 3)
          actualReferences = expected && actualOptions = expected)

      testPropertyWithConfig propConfig "no option other than a package's -r: changes, and none moves" <|
        Prop.forAll (Arb.fromGen genPackages) (fun packages ->
          let before = projectOf packages
          let after = withRuntimeAssets before
          let packageRefs = packages |> List.map (fun p -> "-r:" + compileAsset p) |> Set.ofList
          after.OtherOptions.Length = before.OtherOptions.Length
          && List.forall2 (fun b a -> packageRefs.Contains b || a = b) before.OtherOptions after.OtherOptions)

      testPropertyWithConfig propConfig "a package with no reference assembly is untouched" <|
        Prop.forAll (Arb.fromGen genPackages) (fun packages ->
          let libOnly = packages |> List.map (fun p -> { p with Layout = LibOnly })
          let before = projectOf libOnly
          withRuntimeAssets before = before)

      testPropertyWithConfig propConfig "applying it twice is applying it once" <|
        Prop.forAll (Arb.fromGen genPackages) (fun packages ->
          let once = withRuntimeAssets (projectOf packages)
          withRuntimeAssets once = once)
    ]
  ]
