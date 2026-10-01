/// Small, offline fixture projects for the cases where a project pins a version of something
/// the FSI host also carries (docs/how-isolation-works.md, "What this does not solve").
///
/// Nothing here touches the network. Every package a fixture needs comes from one of three places:
/// a package the fixture builds itself (`dotnet pack` into the workspace's own feed), the F#
/// library-packs folder that ships inside every installed SDK, or an assembly copied out of an
/// installed SDK or runtime and wrapped in a package. The workspace's own `RestorePackagesPath`
/// keeps a fixture's packages out of the real global NuGet cache, and the real cache is not a
/// fallback folder, so a fixture built twice never restores the first build's package.
///
/// What can NOT be made hermetic: a fixture that needs two installed SDKs with different F#
/// toolchains (`sdkPair`), and the System.Text.Json fixture, which starts from a copy of the
/// package's assembly in the real NuGet cache (read only, never written). A machine without them
/// fails those cases loudly instead of skipping.
module SageFs.Tests.ProjectPinFixtures

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks

/// One SDK `dotnet --list-sdks` reports.
type InstalledSdk =
  { Version: string
    Major: int
    /// The folder the SDK lives in, e.g. `~/.dotnet/sdk/10.0.401`.
    SdkDir: string }

/// The two installed SDKs with different majors, oldest and newest. The older one is the one a
/// fixture pins with `global.json` when it wants a host whose FSharp.Core and compiler service
/// are older than what the newer SDK's packages carry.
type SdkPair = { Older: InstalledSdk; Newer: InstalledSdk }

/// The result of running a process to completion.
type ProcessRun = { ExitCode: int; Output: string }

let runCapture (fileName: string) (workingDir: string) (args: string list) : Task<ProcessRun> = task {
  let psi = ProcessStartInfo()
  psi.FileName <- fileName
  psi.WorkingDirectory <- workingDir
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  for arg in args do
    psi.ArgumentList.Add arg
  use proc = Process.Start psi
  let stderrTask = proc.StandardError.ReadToEndAsync()
  let! stdout = proc.StandardOutput.ReadToEndAsync()
  let! stderr = stderrTask
  do! proc.WaitForExitAsync()
  return { ExitCode = proc.ExitCode; Output = stdout + Environment.NewLine + stderr }
}

/// Runs `dotnet <args>` and fails the calling test with the whole output when it exits non-zero.
let dotnetOrFail (workingDir: string) (args: string list) : Task<unit> = task {
  let! run = runCapture "dotnet" workingDir args
  match run.ExitCode with
  | 0 -> ()
  | code ->
    failwithf "dotnet %s failed in %s (exit %d):%s%s" (String.concat " " args) workingDir code Environment.NewLine run.Output
}

let private sdkLine = Regex(@"^(\S+) \[(.+)\]\s*$", RegexOptions.Compiled)

let private numericVersion (version: string) : Version =
  match Version.TryParse(version.Split('-').[0]) with
  | true, parsed -> parsed
  | false, _ -> Version(0, 0)

let installedSdks () : Task<InstalledSdk list> = task {
  let! run = runCapture "dotnet" (Path.GetTempPath()) [ "--list-sdks" ]
  return
    run.Output.Split('\n')
    |> Array.choose (fun line ->
      let m = sdkLine.Match(line.TrimEnd('\r'))
      match m.Success with
      | false -> None
      | true ->
        let version = m.Groups[1].Value
        Some
          { Version = version
            Major = (numericVersion version).Major
            SdkDir = Path.Combine(m.Groups[2].Value, version) })
    |> Array.sortBy (fun sdk -> numericVersion sdk.Version, sdk.Version)
    |> Array.toList
}

/// The oldest and newest installed SDK, or why this machine can't build the fixture. A machine
/// with fewer than two SDK majors cannot show a project that is newer than its host.
let sdkPair () : Task<Result<SdkPair, string>> = task {
  let! sdks = installedSdks ()
  let byMajor = sdks |> List.groupBy (fun sdk -> sdk.Major) |> List.sortBy fst
  match byMajor with
  | (_, older :: _) :: rest when not (List.isEmpty rest) ->
    let _, newerAll = List.last rest
    return Result.Ok { Older = older; Newer = List.last newerAll }
  | _ ->
    return
      Result.Error(
        sprintf
          "This case needs two installed .NET SDKs with different majors (it pins the older as the host and builds against the newer). Installed: [%s]. Install a second SDK major; this case cannot be made hermetic on one SDK."
          (sdks |> List.map (fun sdk -> sdk.Version) |> String.concat ", "))
}

let tfmOf (sdk: InstalledSdk) : string = sprintf "net%d.0" sdk.Major

/// The FSharp.Core package that ships inside the SDK, so a restore never needs the network.
let fsharpCoreLibraryPack (sdk: InstalledSdk) : string =
  Directory.GetFiles(Path.Combine(sdk.SdkDir, "FSharp", "library-packs"), "FSharp.Core.*.nupkg")
  |> Array.head

/// The FSharp.Core version a library pack carries, read from its file name.
let fsharpCoreVersionOf (sdk: InstalledSdk) : string =
  let name = Path.GetFileNameWithoutExtension(fsharpCoreLibraryPack sdk)
  name.Substring("FSharp.Core.".Length)

let private globalPackagesFolder () : string =
  match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
  | null
  | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
  | folder -> folder

/// A temp folder with its own NuGet feed and restore folder.
type Workspace = { Root: string; Feed: string }

let private utf8 = UTF8Encoding false

let createWorkspace (prefix: string) : Workspace =
  let root = Directory.CreateTempSubdirectory(prefix).FullName
  let feed = Directory.CreateDirectory(Path.Combine(root, "feed")).FullName
  // Only the workspace's feed is a source, and there is no fallback folder. A fallback folder (the real
  // global cache) wins over the feed for an exact id and version, so a fixture package that shares a name
  // with something an earlier run left in the cache would silently restore the OLD build.
  let nugetConfig =
    sprintf
      """<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="fixture-feed" value="%s" />
  </packageSources>
</configuration>
"""
      feed
  File.WriteAllText(Path.Combine(root, "nuget.config"), nugetConfig, utf8)
  // Stops MSBuild's upward search so nothing above the temp dir changes the build, and gives the
  // workspace its own restore folder.
  let buildProps =
    """<Project>
  <PropertyGroup>
    <RestorePackagesPath>$(MSBuildThisFileDirectory)restored</RestorePackagesPath>
    <NuGetAudit>false</NuGetAudit>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
"""
  File.WriteAllText(Path.Combine(root, "Directory.Build.props"), buildProps, utf8)
  File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project />", utf8)
  { Root = root; Feed = feed }

let deleteWorkspace (ws: Workspace) : unit =
  try Directory.Delete(ws.Root, true) with _ -> ()

/// Pins the SDK that builds, and hosts sessions on, everything under `dir`.
let pinSdk (dir: string) (sdk: InstalledSdk) : unit =
  let json = sprintf """{ "sdk": { "version": "%s", "rollForward": "disable" } }""" sdk.Version
  Directory.CreateDirectory dir |> ignore
  File.WriteAllText(Path.Combine(dir, "global.json"), json, utf8)

let writeFile (path: string) (text: string) : unit =
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  File.WriteAllText(path, text, utf8)

/// An F# library project's text. `packageReferences` are (id, version) pairs.
let fsharpProject (tfm: string) (extraProperties: string) (packageReferences: (string * string) list) (compileFiles: string list) : string =
  let refs =
    packageReferences
    |> List.map (fun (id, version) -> sprintf """    <PackageReference Include="%s" Version="%s" />""" id version)
    |> String.concat "\n"
  let compiles =
    compileFiles |> List.map (sprintf """    <Compile Include="%s" />""") |> String.concat "\n"
  sprintf
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>%s</TargetFramework>
%s
  </PropertyGroup>
  <ItemGroup>
%s
  </ItemGroup>
  <ItemGroup>
%s
  </ItemGroup>
</Project>
"""
    tfm
    extraProperties
    compiles
    refs

/// What a hand-built package needs: an id, a version, files by package path, and dependencies.
type PackageContents =
  { Id: string
    Version: string
    /// (path inside the package, bytes)
    Files: (string * byte[]) list
    /// (id, exact version) pairs, under the netstandard2.0 group.
    Dependencies: (string * string) list }

let writeNupkg (feed: string) (contents: PackageContents) : unit =
  let dependencies =
    contents.Dependencies
    |> List.map (fun (id, version) -> sprintf """        <dependency id="%s" version="[%s]" />""" id version)
    |> String.concat "\n"
  let nuspec =
    sprintf
      """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>%s</id>
    <version>%s</version>
    <authors>sagefs fixture</authors>
    <description>a fixture package built by the SageFs tests</description>
    <dependencies>
      <group targetFramework=".NETStandard2.0">
%s
      </group>
    </dependencies>
  </metadata>
</package>
"""
      contents.Id
      contents.Version
      dependencies
  let path = Path.Combine(feed, sprintf "%s.%s.nupkg" contents.Id contents.Version)
  match File.Exists path with
  | true -> File.Delete path
  | false -> ()
  use stream = File.Create path
  use zip = new ZipArchive(stream, ZipArchiveMode.Create)
  let add (name: string) (bytes: byte[]) =
    let entry = zip.CreateEntry(name, CompressionLevel.Fastest)
    use entryStream = entry.Open()
    entryStream.Write(bytes, 0, bytes.Length)
  add (sprintf "%s.nuspec" contents.Id) (utf8.GetBytes nuspec)
  for name, bytes in contents.Files do
    add name bytes

/// Puts a package that ships inside an SDK (the F# library pack) into the workspace's feed, so a
/// project restored by ANOTHER SDK can still find the FSharp.Core that a package was built against.
let copyFSharpCorePack (ws: Workspace) (sdk: InstalledSdk) : unit =
  let source = fsharpCoreLibraryPack sdk
  File.Copy(source, Path.Combine(ws.Feed, Path.GetFileName source), true)

/// Builds an F# library with `dotnet pack` into the workspace's feed. `sdk` pins the SDK that
/// builds it (and so the FSharp.Core it is compiled against).
let packLibrary
  (ws: Workspace)
  (sdk: InstalledSdk)
  (tfm: string)
  (packageId: string)
  (version: string)
  (assemblyVersion: string)
  (source: string)
  : Task<unit> = task {
  let dir = Path.Combine(ws.Root, "src", sprintf "%s-%s" packageId version)
  pinSdk dir sdk
  let properties =
    sprintf
      """    <PackageId>%s</PackageId>
    <Version>%s</Version>
    <AssemblyVersion>%s</AssemblyVersion>
    <AssemblyName>%s</AssemblyName>
    <NoWarn>NU1510;NU5104</NoWarn>"""
      packageId
      version
      assemblyVersion
      packageId
  writeFile (Path.Combine(dir, packageId + ".fsproj")) (fsharpProject tfm properties [] [ "Lib.fs" ])
  writeFile (Path.Combine(dir, "Lib.fs")) source
  // Debug on purpose: an optimized build inlines small FSharp.Core functions (Async.map among them) into
  // the library, so it would carry no call into FSharp.Core to go missing. A Debug build keeps the call,
  // the same reason #141's `task { use }` only failed in Debug.
  do! dotnetOrFail dir [ "pack"; "-c"; "Debug"; "-o"; ws.Feed; "--nologo"; "-v:q" ]
}

/// Builds a project (not a package) so its output exists before a session is created on it.
let buildProject (projectPath: string) : Task<unit> =
  dotnetOrFail (Path.GetDirectoryName projectPath) [ "build"; projectPath; "--nologo"; "-v:q" ]

/// Wraps an assembly an SDK or runtime ships (`FSharp.Compiler.Service.dll`) as a package whose
/// version is `version`, so a project can pin "the newer one" without the network.
let packAssembly (ws: Workspace) (packageId: string) (version: string) (tfm: string) (dllPath: string) (dependencies: (string * string) list) : unit =
  writeNupkg
    ws.Feed
    { Id = packageId
      Version = version
      Files = [ sprintf "lib/%s/%s.dll" tfm packageId, File.ReadAllBytes dllPath ]
      Dependencies = dependencies }

/// A copy of the System.Text.Json this runtime ships, stamped as `version` and given one extra
/// public type, `System.Text.Json.PinMarker.Value()`. That is what a project that pins a NEWER
/// System.Text.Json than the shared framework's looks like to the code that calls it: the same
/// assembly name, a higher version, and a member the framework's copy does not have.
let stampedSystemTextJson (version: Version) (marker: string) : byte[] =
  // The runtime's own copy is ReadyToRun (mixed-mode), which Cecil cannot write back. The package copy
  // of the same library is plain IL. Any restore of this repo leaves one in the NuGet cache.
  let packageCopy =
    let root = Path.Combine(globalPackagesFolder (), "system.text.json")
    let candidates =
      match Directory.Exists root with
      | false -> []
      | true ->
        Directory.GetDirectories root
        |> Array.collect (fun versionDir ->
          Directory.GetFiles(versionDir, "System.Text.Json.dll", SearchOption.AllDirectories)
          |> Array.filter (fun path -> path.Contains "/lib/" || path.Contains "\\lib\\"))
        // Newest package version first in sort order, and within one version a modern net TFM (which has no
        // extra dependencies) after netstandard, so `tryLast` takes the newest version's modern build.
        |> Array.sortBy (fun path ->
          let tfmDir = Path.GetFileName(Path.GetDirectoryName path)
          let versionDir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName path)))
          let modern = Regex.IsMatch(tfmDir, @"^net\d+\.\d+$")
          numericVersion versionDir, versionDir, modern, tfmDir)
        |> Array.toList
    match List.tryLast candidates with
    | Some path -> path
    | None ->
      failwithf
        "No System.Text.Json package in the NuGet cache at %s. This case stamps a copy of the package's assembly as a newer version; restore the repo once (dotnet restore) so the cache has one. It cannot be made hermetic without a copy of the assembly to start from."
        root
  use assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly packageCopy
  assembly.Name.Version <- version
  let main = assembly.MainModule
  let markerType =
    Mono.Cecil.TypeDefinition(
      "System.Text.Json",
      "PinMarker",
      Mono.Cecil.TypeAttributes.Public
      ||| Mono.Cecil.TypeAttributes.Abstract
      ||| Mono.Cecil.TypeAttributes.Sealed
      ||| Mono.Cecil.TypeAttributes.BeforeFieldInit,
      main.TypeSystem.Object)
  let value =
    Mono.Cecil.MethodDefinition(
      "Value",
      Mono.Cecil.MethodAttributes.Public ||| Mono.Cecil.MethodAttributes.Static ||| Mono.Cecil.MethodAttributes.HideBySig,
      main.TypeSystem.String)
  let il = value.Body.GetILProcessor()
  il.Emit(Mono.Cecil.Cil.OpCodes.Ldstr, marker)
  il.Emit(Mono.Cecil.Cil.OpCodes.Ret)
  markerType.Methods.Add value
  main.Types.Add markerType
  use output = new MemoryStream()
  assembly.Write output
  output.ToArray()
