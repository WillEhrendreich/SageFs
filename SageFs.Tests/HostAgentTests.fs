module SageFs.Tests.HostAgentTests

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.HostAgent

let private noAssemblies () : System.Reflection.Assembly[] = [||]

let private nothingLoaded : AssemblySources = { Dynamic = noAssemblies; Loaded = noAssemblies }

let private emptyInit : AgentInit = { Projects = []; ResolveFrom = []; ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }

let private request (detours: DetourPolicy) (discovery: DiscoveryPolicy) : AfterEval =
  { EvaluatedCode = "let x = 1"; Detours = detours; Discovery = discovery; IsFileSave = false }

let private missingProject =
  Path.Combine(Path.GetTempPath(), "sagefs-host-agent-no-such-dir", "Missing.dll")

let private unknownTest : TestCase =
  { Id = TestId.create "no.such.test" TestFramework.Expecto
    FullName = "no.such.test"
    DisplayName = "no.such.test"
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

/// The shape-matrix fixture's own compiled output — a real, already-built,
/// namespace-qualified module (`module WebAppFixture.Shapes`) with a
/// BCL-only-signature function (`plainHandler`), used as the "compiled
/// project" a freshly-started session (no #load anywhere) has loaded. Picks
/// whichever of Debug/Release is newest, mirroring `SageFsBinary.path`.
let private webAppFixtureDll () =
  let root = DirectoryInfo(AppContext.BaseDirectory).Parent.Parent.Parent.Parent.FullName
  [ "Debug"; "Release" ]
  |> List.map (fun cfg -> Path.Combine(root, "SageFs.Tests", "fixtures", "WebAppFixture", "bin", cfg, "net11.0", "WebAppFixture.dll"))
  |> List.filter File.Exists
  |> List.sortByDescending File.GetLastWriteTimeUtc
  |> List.tryHead

/// A dynamically-emitted "file save" re-eval, shaped exactly like FSI's own
/// FLATTENED re-emit of a module file: the compiled type's namespace is
/// stripped, and only the type name survives (`Shapes`, not
/// `WebAppFixture.Shapes`) — so its FullName ("Shapes.plainHandler") is a
/// SUFFIX of the compiled copy's ("WebAppFixture.Shapes.plainHandler"),
/// which is exactly what `compatibleForDetour`'s EndsWith check requires.
let private flattenedPlainHandlerSave () : Assembly =
  let asmBuilder =
    AssemblyBuilder.DefineDynamicAssembly(AssemblyName("sagefs-test-compiled-copy-save"), AssemblyBuilderAccess.Run)
  let modBuilder = asmBuilder.DefineDynamicModule("MainModule")
  let typeBuilder =
    modBuilder.DefineType(
      "Shapes",
      TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  let methodBuilder =
    typeBuilder.DefineMethod(
      "plainHandler",
      MethodAttributes.Public ||| MethodAttributes.Static,
      typeof<string>,
      [| typeof<string> |])
  let il = methodBuilder.GetILGenerator()
  il.Emit(OpCodes.Ldstr, "B")
  il.Emit(OpCodes.Ret)
  typeBuilder.CreateType() |> ignore
  asmBuilder :> Assembly

[<Tests>]
let tests =
  testList "HostAgent" [
    testList "start" [
      testCase "a project that is not on disk is a typed load error, never an exception" <| fun _ ->
        let agent = Agent({ Projects = [ missingProject ]; ResolveFrom = []; ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }, nothingLoaded)
        match (agent.Started).AssemblyLoadErrors with
        | [ AssemblyLoadError.FileNotFound(path, _) ] -> Expect.equal "names the file" missingProject path
        | other -> failtestf "expected one FileNotFound, got %A" other

      testCase "a session with no projects starts clean" <| fun _ ->
        let agent = Agent(emptyInit, nothingLoaded)
        Expect.isEmpty "no errors" agent.Started.AssemblyLoadErrors

      // WHY — the compiled-app shape-matrix regression (--integration-host
      // "hot-reload shape matrix", cell localType): a session that loaded a
      // project's compiled assembly and NEVER ran a #load/interactive eval
      // still has an app that holds the COMPILED copy of everything that
      // project exposes — nothing else has redefined those names yet. A save
      // that redirects the compiled copy genuinely reaches the running
      // process, and must be reported as such. `startState` used to leave
      // `AppHolds` empty until some later non-file-save eval touched a name,
      // so this exact case reported `NoEffect`/`PatchIneffective` while the
      // app was already serving the new value — the dishonest-count failure
      // in the OTHER direction from the false-Patched bug.
      testCase "WHY — a compiled project's own methods are what the session holds at start, so a save that redirects one honestly reaches the running process" <| fun _ ->
        match webAppFixtureDll () with
        | None -> skiptest "WebAppFixture fixture is not built — run the fixture's own build first"
        | Some dll ->
          let mutable dynAsms: Assembly[] = [||]
          let sources: AssemblySources = { Dynamic = (fun () -> dynAsms); Loaded = noAssemblies }
          let agent = Agent({ Projects = [ dll ]; ResolveFrom = []; ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads }, sources)
          agent.Started.AssemblyLoadErrors
          |> Expect.isEmpty "the fixture must load cleanly for this test to mean anything"

          dynAsms <- [| flattenedPlainHandlerSave () |]
          let report =
            agent.AfterEval
              { EvaluatedCode = "module Shapes =\n  let plainHandler (who: string) = \"B\" + who"
                Detours = DetourPolicy.ApplyDetours
                Discovery = DiscoveryPolicy.WhenChanged
                IsFileSave = true }

          report.DetourReport.Redirected
          |> Expect.isNonEmpty "the compiled plainHandler must actually be re-pointed"
          report.DetourReport.ReachedRunningProcess
          |> Expect.isNonEmpty
            "the compiled copy IS what a freshly-started session holds — a save that redirects it must count as reaching the running process, not report NoEffect while the app already serves the new value"
    ]

    testList "AfterEval" [
      testCase "with nothing emitted yet there is nothing to report, whatever the policies" <| fun _ ->
        let agent = Agent(emptyInit, nothingLoaded)
        for detours in [ DetourPolicy.ApplyDetours; DetourPolicy.RegisterOnly ] do
          for discovery in [ DiscoveryPolicy.WhenChanged; DiscoveryPolicy.Forced ] do
            let report = agent.AfterEval(request detours discovery)
            Expect.isEmpty (sprintf "no methods (%A, %A)" detours discovery) report.UpdatedMethods
            Expect.isEmpty "no tests" report.LiveTest.DiscoveredTests

      testProperty "the report is a pure function of the request when nothing is emitted"
      <| fun (code: string) ->
        let agent = Agent(emptyInit, nothingLoaded)
        let ask () =
          agent.AfterEval { EvaluatedCode = code; Detours = DetourPolicy.RegisterOnly; Discovery = DiscoveryPolicy.WhenChanged; IsFileSave = false }
        ask () = ask ()
    ]

    testList "RunTest" [
      testAsync "a test the agent never discovered is NotRun, not an error" {
        let agent = Agent(emptyInit, nothingLoaded)
        let! result = agent.RunTest unknownTest
        Expect.equal "not run" TestResult.NotRun result
      }
    ]

    testList "TakeCoverage" [
      testCase "a process with nothing instrumented has no coverage to take" <| fun _ ->
        Expect.equal "none" NoCoverage (Agent(emptyInit, nothingLoaded).TakeCoverage())

      testCase "assemblies without the coverage tracker report none, and taking twice changes nothing" <| fun _ ->
        let agent = Agent(emptyInit, { nothingLoaded with Loaded = fun () -> [| typeof<string>.Assembly |] })
        Expect.equal "none" NoCoverage (agent.TakeCoverage())
        Expect.equal "still none" NoCoverage (agent.TakeCoverage())
    ]

    testList "LoadedAssemblyNames" [
      testCase "names exactly the assemblies the process has loaded, sorted and without duplicates" <| fun _ ->
        let assemblies = [| typeof<string>.Assembly; typeof<Expecto.TestCode>.Assembly; typeof<string>.Assembly |]
        let agent = Agent(emptyInit, { nothingLoaded with Loaded = fun () -> assemblies })
        let names = agent.LoadedAssemblyNames()
        Expect.contains "Expecto is named" "Expecto" names
        Expect.equal "sorted" (List.sort names) names
        Expect.equal "distinct" (List.distinct names) names

      testCase "a process that has loaded nothing names nothing" <| fun _ ->
        Expect.isEmpty "none" (Agent(emptyInit, nothingLoaded).LoadedAssemblyNames())
    ]

    testList "DiscoverLoaded" [
      testCase "a process with no test framework loaded discovers nothing" <| fun _ ->
        let agent = Agent(emptyInit, nothingLoaded)
        let discovery = agent.DiscoverLoaded()
        Expect.isEmpty "no tests" discovery.Tests
        Expect.isEmpty "no providers" discovery.Providers
    ]
  ]
