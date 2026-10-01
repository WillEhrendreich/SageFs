/// A session that loaded through the manual-parse fallback is a real mode with
/// a real cause: MSBuild evaluation failed in-process, so there is no build
/// output and no target to run. It used to be inferred from an empty
/// `Projects` list, so health said Healthy and `run_app` said "not loaded in
/// this session", which sent the reader in a circle: the project IS in the
/// session. The mode is now a value on the solution and on each classified
/// project, and health and the run_app refusal both say why.
module SageFs.Tests.LoadModeTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ProjectLoading
open SageFs.WorkerProtocol
open SageFs.WarmUp
open FSharp.Compiler.CodeAnalysis

let private tempDir () =
  Directory.CreateTempSubdirectory("sagefs-loadmode-").FullName

let private exeFsproj =
  "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
  "  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
  "  <ItemGroup><Compile Include=\"Program.fs\" /></ItemGroup>\n" +
  "</Project>\n"

/// A real FSharpProjectOptions, made the way the fallback makes them.
let private manualParse (dir: string) : FSharpProjectOptions =
  let projPath = Path.Combine(dir, "App.fsproj")
  File.WriteAllText(Path.Combine(dir, "Program.fs"), "module Program\nlet x = 1\n")
  File.WriteAllText(projPath, exeFsproj)
  match ManualProjectParse.parseFsproj TestInfrastructure.quietLogger projPath with
  | [ options ] -> options
  | other -> failwithf "expected exactly one FSharpProjectOptions, got %d" other.Length

let private fallbackSolution (cause: FallbackCause) (options: FSharpProjectOptions) : Solution =
  { emptySolution with FsProjects = [ options ]; Mode = LoadMode.ManualFallback cause }

let private handle : WorkerHandle = { Pid = 4242; Port = Some 5000 }

let private roleVia (mode: LoadMode) : ClassifiedProject =
  { Path = "/repo/App/App.fsproj"
    Role = ProjectRole.Executable
    PackageRefs = []
    LoadMode = mode; Build = SageFs.BuildOptimization.Unoptimized }

let private healthyWarmup : WarmupContext =
  { SourceFilesScanned = 3
    AssembliesLoaded = [ { Name = "App"; Path = "/bin/App.dll"; NamespaceCount = 1; ModuleCount = 1 } ]
    NamespacesOpened = [ { Name = "App"; Kind = OpenableKind.Namespace; Source = "reflection"; DurationMs = FixtureDurations.instantOpenMs } ]
    FailedOpens = []
    PhaseTiming = FixtureDurations.warmupTotalOnly 42L
    StartedAt = DateTimeOffset.UtcNow }

let private causes =
  [ FallbackCause.Threw "MSB4019: the imported project was not found"
    FallbackCause.ReturnedNothing
    FallbackCause.SkippedNewerSdk (12, 11) ]

[<Tests>]
let tests =
  testList "Project load mode" [
    testCase "WHY — a solution the loader evaluated says so, and so does every project it classifies" <| fun _ ->
      let sln = { emptySolution with Projects = [ ShadowCopyTests.mkProjectOptions "/repo/App/bin/Debug/net10.0/App.dll" ] }
      sln.Mode |> Expect.equal "evaluated" LoadMode.Evaluated
      classifiedProjectsOf sln
      |> List.map (fun cp -> cp.LoadMode)
      |> Expect.allEqual "each project came through the loader" LoadMode.Evaluated

    testCase "WHY — a fallback solution's projects carry the cause it fell back for, so nothing downstream has to guess it" <| fun _ ->
      let dir = tempDir ()
      try
        for cause in causes do
          classifiedProjectsOf (fallbackSolution cause (manualParse dir))
          |> List.map (fun cp -> cp.LoadMode)
          |> Expect.equal (sprintf "%A is kept on the project" cause) [ LoadMode.ManualFallback cause ]
      finally
        Directory.Delete(dir, true)

    testCase "WHY — whatever the loader said, the description is never blank and always says what to do" <| fun _ ->
      for cause in causes @ [ FallbackCause.Threw ""; FallbackCause.Threw "  " ] do
        let text = FallbackCause.describe cause
        String.IsNullOrWhiteSpace text |> Expect.isFalse (sprintf "%A describes itself" cause)
        text |> Expect.stringContains "points at the build" "dotnet build"

    testCase "WHY — a Ready session whose project loaded through the fallback is Degraded, with the cause, not Healthy" <| fun _ ->
      for cause in causes do
        let roles = [ roleVia (LoadMode.ManualFallback cause) ]
        match SessionHealth.classify (SessionLifecycleStatus.Ready handle) roles (Some healthyWarmup) with
        | SessionHealth.Degraded reason ->
          reason |> Expect.stringContains "names the project" "App.fsproj"
          reason |> Expect.stringContains "carries the cause's description" (FallbackCause.describe cause)
        | other -> failtestf "expected Degraded for %A, got %A" cause other

    testCase "WHY — an evaluated session is unaffected: still Healthy when warmup loaded something" <| fun _ ->
      SessionHealth.classify (SessionLifecycleStatus.Ready handle) [ roleVia LoadMode.Evaluated ] (Some healthyWarmup)
      |> Expect.equal "healthy" SessionHealth.Healthy

    testCase "WHY — a session still starting is Starting whatever its load mode, because health cannot be judged yet" <| fun _ ->
      SessionHealth.classify (SessionLifecycleStatus.Starting handle) [ roleVia (LoadMode.ManualFallback FallbackCause.ReturnedNothing) ] None
      |> Expect.equal "starting" SessionHealth.Starting

    testCase "WHY — run_app on a project the session holds through the fallback says WHY it cannot run, not 'create a session that includes it'" <| fun _ ->
      let project = "/repo/App/App.fsproj"
      let roles = [ { roleVia (LoadMode.ManualFallback (FallbackCause.Threw "MSB4019")) with Path = project } ]
      match AppRunner.resolveProjectAssembly [] roles project with
      | Ok _ -> failtest "a fallback project has no built assembly to run"
      | Result.Error message ->
        message |> Expect.stringContains "says the project is held" "App.fsproj"
        message |> Expect.stringContains "says why" "MSB4019"
        message.Contains "Create a session that includes it" |> Expect.isFalse "the project is already in the session"

    testCase "WHY — a project the session does not hold at all still gets the original refusal and remedy" <| fun _ ->
      match AppRunner.resolveProjectAssembly [] [] "/repo/Other/Other.fsproj" with
      | Ok _ -> failtest "nothing to run"
      | Result.Error message ->
        message |> Expect.stringContains "still says to create a session" "Create a session that includes it"
        message |> Expect.stringContains "still says what the session holds" "this session holds no projects"
  ]
