module SageFs.Tests.LoadedProjectReportingTests

// Regression coverage for a confirmed root cause: a session that loaded
// successfully via the manual-parse fallback (ProjectLoading.fs:348-372,
// used when Ionide's workspace loader silently returns zero projects)
// reported `loadedProjects: []` on every surface — /api/sessions, the VS
// Code tree, and the dashboard's project picker/Run button — because
// `ProjectRoles`/`ProjectTargets` (ActorCreation.fs) read `sln.Projects`
// only, and the fallback path fills `sln.FsProjects` instead. See the
// precedent `projectDirectories` (ActorCreation.fs), which already merges
// both sources for the file watcher for exactly this reason.

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ProjectLoading
open Ionide.ProjInfo.Types
open FSharp.Compiler.CodeAnalysis

let private quietLogger =
  { new Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

/// A normal (Ionide-loaded) ProjectOptions fixture. Mirrors
/// ActorCreationTests.mkProject — kept local so this file has no
/// cross-file test dependency.
let private mkProject (fileName: string) (outputType: string option) (isTestProject: bool) : ProjectOptions =
  let allProps =
    [ match outputType with
      | Some v -> yield "OutputType", Set.singleton v
      | None -> ()
      yield "IsTestProject", Set.singleton (string isTestProject) ]
    |> Map.ofList
  { ProjectId = None
    ProjectFileName = fileName
    TargetFramework = "net10.0"
    SourceFiles = []
    OtherOptions = []
    ReferencedProjects = []
    PackageReferences = []
    LoadTime = DateTime.UtcNow
    TargetPath = fileName + ".dll"
    TargetRefPath = None
    ProjectOutputType = ProjectOutputType.Library
    ProjectSdkInfo =
      { IsTestProject = isTestProject
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
    AllProperties = allProps
    AllItems = Map.empty
    Analyzers = [] }

let private tempDir () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-loaded-project-reporting-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  dir

/// Writes a minimal buildable-looking fsproj + one source file, then parses
/// it the same way the manual-parse fallback does, producing a real
/// FSharpProjectOptions with no Ionide ProjectOptions counterpart — exactly
/// the shape `sln.FsProjects` has on the fallback path.
let private manualParseFallback (dir: string) (fsprojXml: string) : FSharpProjectOptions =
  let projPath = Path.Combine(dir, "App.fsproj")
  File.WriteAllText(Path.Combine(dir, "Program.fs"), "module Program\nlet x = 1\n")
  File.WriteAllText(projPath, fsprojXml)
  match ManualProjectParse.parseFsproj quietLogger projPath with
  | [ options ] -> options
  | other -> failwithf "expected exactly one FSharpProjectOptions, got %d" other.Length

let private exeFsproj =
  "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
  "  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
  "  <ItemGroup><Compile Include=\"Program.fs\" /></ItemGroup>\n" +
  "</Project>\n"

let private libFsproj =
  "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
  "  <ItemGroup><Compile Include=\"Program.fs\" /></ItemGroup>\n" +
  "</Project>\n"

let private testFsproj =
  "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>\n" +
  "  <ItemGroup>\n" +
  "    <PackageReference Include=\"Expecto\" Version=\"11.0.0-alpha8\" />\n" +
  "    <Compile Include=\"Program.fs\" />\n" +
  "  </ItemGroup>\n" +
  "</Project>\n"

[<Tests>]
let tests =
  testList "LoadedProjectReporting" [

    testCase "empty solution classifies to nothing" (fun () ->
      classifiedProjectsOf emptySolution |> Expect.isEmpty "no projects at all")

    testCase "normal (Ionide) path is unchanged by classifiedProjectsOf" (fun () ->
      let exeProj = mkProject "/repo/App/App.fsproj" (Some "Exe") false
      let libProj = mkProject "/repo/Lib/Lib.fsproj" None false
      let sln = { emptySolution with Projects = [ exeProj; libProj ] }
      let viaUnified = classifiedProjectsOf sln
      let viaDirect = classifyProjects sln.Projects
      viaUnified |> Expect.equal "classifiedProjectsOf must not change the normal-path result" viaDirect
      viaUnified |> List.map (fun cp -> cp.Role)
      |> Expect.equal "roles read straight from AllProperties" [ ProjectRole.Executable; ProjectRole.Library ])

    testCase "fallback-only solution (Projects empty, FsProjects populated) is classified — the regression" (fun () ->
      let dir = tempDir ()
      try
        let fp = manualParseFallback dir exeFsproj
        let sln = { emptySolution with Projects = []; FsProjects = [ fp ] }
        let result = classifiedProjectsOf sln
        result |> Expect.hasLength "the fallback project must be reported, not dropped" 1
        result.Head.Path |> Expect.equal "path comes from the FSharpProjectOptions" fp.ProjectFileName
        result.Head.Role |> Expect.equal "OutputType=Exe in the fsproj XML" ProjectRole.Executable
      finally
        Directory.Delete(dir, true))

    testCase "fallback project with no OutputType is classified Library, never guessed Executable" (fun () ->
      let dir = tempDir ()
      try
        let fp = manualParseFallback dir libFsproj
        let sln = { emptySolution with Projects = []; FsProjects = [ fp ] }
        let result = classifiedProjectsOf sln
        result |> Expect.hasLength "one fallback project" 1
        result.Head.Role |> Expect.equal "unknown OutputType must fall back to Library, not a guessed Run button" ProjectRole.Library
      finally
        Directory.Delete(dir, true))

    testCase "fallback project with IsTestProject/Expecto is classified Test" (fun () ->
      let dir = tempDir ()
      try
        let fp = manualParseFallback dir testFsproj
        let sln = { emptySolution with Projects = []; FsProjects = [ fp ] }
        let result = classifiedProjectsOf sln
        result.Head.Role |> Expect.equal "IsTestProject + Expecto package" ProjectRole.Test
        result.Head.PackageRefs |> Expect.contains "package refs come from raw fsproj PackageReference Include" "Expecto"
      finally
        Directory.Delete(dir, true))

    testCase "both populated: no duplicates, normal data wins for the shared path" (fun () ->
      let dir = tempDir ()
      try
        let fp = manualParseFallback dir exeFsproj
        // Same path as the fallback entry, but via the (richer) normal path,
        // classified Library so the test can tell which source won.
        let normalProj = mkProject fp.ProjectFileName None false
        let otherFallback = manualParseFallback (tempDir ()) libFsproj
        let sln = { emptySolution with Projects = [ normalProj ]; FsProjects = [ fp; otherFallback ] }
        let result = classifiedProjectsOf sln
        result |> Expect.hasLength "one deduped shared-path entry + one fallback-only entry" 2
        let shared = result |> List.find (fun cp -> cp.Path = fp.ProjectFileName)
        shared.Role |> Expect.equal "the normal-path classification must win for a path present in both" ProjectRole.Library
      finally
        Directory.Delete(dir, true))

    testCase "normal path projectTargetsOf is unchanged" (fun () ->
      let exeProj = mkProject "/repo/App/App.fsproj" (Some "Exe") false
      let sln = { emptySolution with Projects = [ exeProj ] }
      projectTargetsOf sln
      |> Expect.equal "unchanged: (ProjectFileName, TargetPath) straight from Projects" [ exeProj.ProjectFileName, exeProj.TargetPath ]
      )

    testCase "fallback projectTargetsOf finds the built assembly by expected file name" (fun () ->
      let dir = tempDir ()
      try
        let fp = manualParseFallback dir exeFsproj
        let expectedDll = Path.Combine(dir, "bin", "Debug", "net10.0", "App.dll")
        Directory.CreateDirectory(Path.GetDirectoryName expectedDll) |> ignore
        File.WriteAllBytes(expectedDll, [| 0uy |])
        let sln = { emptySolution with Projects = []; FsProjects = [ fp ]; References = [ expectedDll ] }
        projectTargetsOf sln
        |> Expect.equal "matched by App.dll among the collected reference DLLs" [ fp.ProjectFileName, expectedDll ]
      finally
        Directory.Delete(dir, true))

    testCase "fallback projectTargetsOf omits a project with no matching built assembly, rather than guessing" (fun () ->
      let dir = tempDir ()
      try
        let fp = manualParseFallback dir exeFsproj
        let sln = { emptySolution with Projects = []; FsProjects = [ fp ]; References = [ "/some/other/Unrelated.dll" ] }
        projectTargetsOf sln |> Expect.isEmpty "no known target — omit, never guess a wrong one"
      finally
        Directory.Delete(dir, true))
  ]
