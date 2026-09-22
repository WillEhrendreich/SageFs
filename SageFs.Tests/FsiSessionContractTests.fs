module SageFs.Tests.FsiSessionContractTests

open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open FSharp.Compiler.Interactive.Shell
open SageFs.FsiHostBuild
open SageFs.FsiHostClient
open SageFs.FsiSession
open SageFs.HostAgent
open SageFs.RemoteFsiSession
open SageFs.Tests.TestInfrastructure

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private dotnet =
  match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
  | null
  | "" -> "dotnet"
  | path -> path

// --multiemit- is what a hot-reload session runs with: every eval lands in ONE assembly, so redefinitions can be paired.
let private fsiArgs = [ "fsi"; "--noninteractive"; "--nologo"; "--readline-"; "--multiemit-" ]

/// A fresh in-process session behind the port.
let private newInProcess () : Async<IFsiSession> =
  async {
    let config = FsiEvaluationSession.GetDefaultConfiguration()
    let session =
      FsiEvaluationSession.Create(
        config,
        List.toArray fsiArgs,
        new StreamReader(Stream.Null),
        TextWriter.Null,
        TextWriter.Null,
        collectible = true
      )
    return new InProcessFsiSession(session, { Projects = []; ResolveFrom = [] }) :> IFsiSession
  }

/// One host build shared by every remote test (keyed by SDK + sources).
let private hostDll : Lazy<string> =
  lazy
    (let cache = Path.Combine(Path.GetTempPath(), "sagefs-fsihost-test-cache")
     match resolveSdkVersion dotnet repoRoot |> Result.bind (fun sdk -> ensureBuilt dotnet sdk cache) with
     | Result.Ok(Built dll)
     | Result.Ok(Reused dll) -> dll
     | Result.Error reason -> failwith (describeBuildError reason))

/// A fresh session backed by an isolated FSI host process.
let private newRemote () : Async<IFsiSession> =
  async {
    let options =
      { HostDll = hostDll.Force()
        Dotnet = dotnet
        FsiArgs = fsiArgs
        WorkingDir = repoRoot
        Environment = []
        OnOutput = fun _ _ -> ()
        OnLog = ignore
        StartupTimeoutMs = 60_000 }
    match! start options with
    | Result.Ok host ->
      match! attach host { Projects = []; ResolveFrom = [] } with
      | Result.Ok session -> return session :> IFsiSession
      | Result.Error reason -> return failtest (describeAttachError reason)
    | Result.Error reason -> return failtest (describeStartError reason)
  }

/// The things an IFsiSession can do that the contract specifies. Each contract case is tagged with one, so an
/// implementation that cannot do something yet says so explicitly (a pending case) instead of silently skipping it.
type Capability =
  | Evaluating
  | Diagnostics
  | BoundValues
  | FeatureGates
  | LiveValues
  | Completions
  | TypeChecking
  | Disposal
  | HotReload
  | LiveTesting
  | LoadedAssemblies
  | Coverage

let private eval (session: IFsiSession) (code: string) = session.Eval(code, CancellationToken.None)

let private runTest (session: IFsiSession) (test: SageFs.Features.LiveTesting.TestCase) : Async<SageFs.Features.LiveTesting.TestResult> =
  async {
    match! session.RunTest test with
    | AgentAnswered result -> return result
    | AgentUnavailable reason -> return failtestf "the agent was unavailable: %s" reason
  }

let private mustSucceed (session: IFsiSession) (code: string) =
  match (eval session code).Outcome with
  | FsiSucceeded -> ()
  | other -> failtestf "expected %s to succeed, got %A" code other

let private withSession (create: unit -> Async<IFsiSession>) (body: IFsiSession -> unit) : Async<unit> =
  async {
    let! session = create ()
    try body session
    finally session.Dispose()
  }

let private withSessionAsync (create: unit -> Async<IFsiSession>) (body: IFsiSession -> Async<unit>) : Async<unit> =
  async {
    let! session = create ()
    try return! body session
    finally session.Dispose()
  }

let private expectoPath = typeof<Expecto.TestCode>.Assembly.Location

/// A top-level (so FSI compiles it public) [<Tests>] value with one passing and one failing case.
let private probeTests = "open Expecto\n[<Tests>]\nlet probeTests = testList \"probe\" [ testCase \"passes\" (fun () -> ()); testCase \"fails\" (fun () -> failwith \"boom\") ]"

let private afterEval (session: IFsiSession) (code: string) (detours: DetourPolicy) (discovery: DiscoveryPolicy) : AfterEvalReport =
  match session.AfterEval { EvaluatedCode = code; Detours = detours; Discovery = discovery; IsFileSave = false } with
  | AgentAnswered report -> report
  | AgentUnavailable reason -> failtestf "the agent was unavailable: %s" reason

/// The behaviour EVERY IFsiSession implementation must have. Instantiated for each implementation, so the
/// in-process and isolated-host sessions are held to one specification. `notYet` lists the capabilities an
/// implementation does not have yet: those cases are pending (ignored, and counted as such), never skipped silently.
let contract (label: string) (create: unit -> Async<IFsiSession>) (notYet: Capability list) : Test =
  let caseAsync (capability: Capability) (name: string) (body: IFsiSession -> Async<unit>) =
    match List.contains capability notYet with
    | true -> ptestCase (sprintf "%s [%A: not yet in this implementation]" name capability) ignore
    | false -> testCaseAsync name (withSessionAsync create body)
  let case (capability: Capability) (name: string) (body: IFsiSession -> unit) =
    caseAsync capability name (fun session -> async { body session })
  testList (sprintf "IFsiSession contract: %s" label) [
    case Evaluating "a valid submission succeeds with no error diagnostics" (fun session ->
      let result = eval session "let x = 21 * 2;;"
      Expect.equal "succeeded" FsiSucceeded result.Outcome
      Expect.isEmpty "no diagnostics" result.Diagnostics)

    case Evaluating "a type error fails and reports a diagnostic on line 1" (fun session ->
      let result = eval session "let y : int = \"not an int\";;"
      match result.Outcome with
      | FsiFailed _ -> ()
      | other -> failtestf "expected a failure, got %A" other
      Expect.isNonEmpty "at least one diagnostic" result.Diagnostics
      Expect.equal "first diagnostic starts on line 1" 1 result.Diagnostics.[0].Range.StartLine
      Expect.isGreaterThan "carries the FS error number" (result.Diagnostics.[0].ErrorNumber, 0))

    case Evaluating "a runtime exception fails carrying its message" (fun session ->
      match (eval session "(failwith \"boom\" : unit);;").Outcome with
      | FsiFailed ex -> Expect.stringContains "message" "boom" ex.Message
      | other -> failtestf "expected a failure, got %A" other)

    case BoundValues "state persists across submissions and BoundValue reads it as text" (fun session ->
      mustSucceed session "let x = 21 * 2;;"
      mustSucceed session "let y = x + 1;;"
      Expect.equal "x" "42" (string (session.BoundValue "x"))
      Expect.equal "y" "43" (string (session.BoundValue "y"))
      Expect.isNull "an unbound name is null" (session.BoundValue "nope"))

    case FeatureGates "ReadFlag distinguishes unbound, bool and not-a-bool" (fun session ->
      mustSucceed session "let flagOn = true;;"
      mustSucceed session "let flagOff = false;;"
      mustSucceed session "let notBool = 3;;"
      Expect.equal "unbound" FlagUnbound (session.ReadFlag "missing")
      Expect.equal "true" (FlagBound true) (session.ReadFlag "flagOn")
      Expect.equal "false" (FlagBound false) (session.ReadFlag "flagOff")
      match session.ReadFlag "notBool" with
      | FlagNotBool _ -> ()
      | other -> failtestf "expected FlagNotBool, got %A" other)

    case LiveValues "LiveValuesJson lists the bound names and bumps the generation" (fun session ->
      mustSucceed session "let watched = 7;;"
      let generation = ref 0L
      let json = session.LiveValuesJson generation
      Expect.stringContains "names the binding" "watched" json
      Expect.equal "generation incremented" 1L generation.Value
      session.LiveValuesJson generation |> ignore
      Expect.equal "and again" 2L generation.Value)

    case Completions "Completions offers List.map after 'List.ma'" (fun session ->
      let items = session.Completions("List.ma", 7, "ma")
      Expect.isTrue "map is offered" (items |> List.exists (fun item -> item.ReplacementText = "map")))

    case Completions "a candidate's description is available on demand" (fun session ->
      let items = session.Completions("List.ma", 7, "ma")
      match items |> List.tryFind (fun item -> item.ReplacementText = "map") with
      | None -> failtest "map was not offered"
      | Some item ->
        match item.GetDescription with
        | None -> failtest "expected a lazy description"
        | Some describe -> Expect.isNonEmpty "the description has text" (describe ()))

    case TypeChecking "TypeCheckWithSymbols returns definitions and uses for good code" (fun session ->
      let result = session.TypeCheckWithSymbols("Sample.fsx", "let addOne x = x + 1\nlet three = addOne 2")
      Expect.isEmpty "no error diagnostics" result.Diagnostics
      let named = result.SymbolRefs |> List.filter (fun symbol -> symbol.SymbolFullName.EndsWith "addOne")
      Expect.isTrue "addOne is defined" (named |> List.exists (fun symbol -> symbol.UseKind = SageFs.Features.LiveTesting.SymbolUseKind.Definition))
      Expect.isTrue "addOne is used" (named |> List.exists (fun symbol -> symbol.UseKind = SageFs.Features.LiveTesting.SymbolUseKind.Reference))
      Expect.isTrue "the file path is carried" (named |> List.forall (fun symbol -> symbol.FilePath = "Sample.fsx")))

    case TypeChecking "TypeCheckWithSymbols reports errors and no symbols for bad code" (fun session ->
      let result = session.TypeCheckWithSymbols("Bad.fsx", "let bad : int = \"s\"")
      Expect.isNonEmpty "an error diagnostic" result.Diagnostics
      Expect.isEmpty "no symbols from code that does not check" result.SymbolRefs)

    case Diagnostics "Diagnose reports the same error the eval would" (fun session ->
      Expect.isNonEmpty "diagnostic for a type error" (session.Diagnose "let z : int = \"s\""))

    caseAsync LiveTesting "AfterEval discovers a test defined in the session, and RunTest runs it" (fun session ->
      async {
        mustSucceed session (sprintf "#r @\"%s\"" expectoPath)
        mustSucceed session probeTests
        let report = afterEval session probeTests DetourPolicy.RegisterOnly DiscoveryPolicy.Forced
        let named (fragment: string) =
          match report.LiveTest.DiscoveredTests |> Array.tryFind (fun t -> t.FullName.EndsWith fragment) with
          | Some test -> test
          | None -> failtestf "%s was not discovered among %A" fragment (report.LiveTest.DiscoveredTests |> Array.map (fun t -> t.FullName))
        let passes = named "passes"
        let fails = named "fails"
        match! runTest session passes with
        | SageFs.Features.LiveTesting.TestResult.Passed _ -> ()
        | other -> failtestf "expected passes to pass, got %A" other
        match! runTest session fails with
        | SageFs.Features.LiveTesting.TestResult.Failed _ -> ()
        | other -> failtestf "expected fails to fail, got %A" other
      })

    case Coverage "a session with nothing instrumented has no coverage to take" (fun session ->
      match session.TakeCoverage() with
      | AgentAnswered reading -> Expect.equal "nothing instrumented" NoCoverage reading
      | AgentUnavailable reason -> failtestf "the agent was unavailable: %s" reason)

    case LoadedAssemblies "LoadedAssemblyNames sees an assembly the session referenced" (fun session ->
      mustSucceed session (sprintf "#r @\"%s\"" expectoPath)
      mustSucceed session "open Expecto\nlet touched = testList \"t\" []"
      match session.LoadedAssemblyNames() with
      | AgentAnswered names -> Expect.contains "Expecto was loaded into the session's process" "Expecto" names
      | AgentUnavailable reason -> failtestf "the agent was unavailable: %s" reason)

    caseAsync LiveTesting "an expression-only eval discovers nothing new unless asked to look" (fun session ->
      async {
        mustSucceed session (sprintf "#r @\"%s\"" expectoPath)
        mustSucceed session probeTests
        afterEval session probeTests DetourPolicy.RegisterOnly DiscoveryPolicy.Forced |> ignore
        mustSucceed session "let three = 1 + 2"
        let quiet = afterEval session "let three = 1 + 2" DetourPolicy.RegisterOnly DiscoveryPolicy.WhenChanged
        Expect.isEmpty "nothing redefined, so no tests are reported" quiet.LiveTest.DiscoveredTests
      })

    caseAsync HotReload "redefining a function reports it as an updated method" (fun session ->
      async {
        // Detours pair methods by their dotted path, so the function lives in a named module, as a reloaded file's does.
        let version (delta: int) = sprintf "module Reload =\n  let addOne (x: int) = x + %d" delta
        mustSucceed session (version 1)
        afterEval session (version 1) DetourPolicy.ApplyDetours DiscoveryPolicy.WhenChanged |> ignore
        mustSucceed session (version 100)
        let report = afterEval session (version 100) DetourPolicy.ApplyDetours DiscoveryPolicy.WhenChanged
        Expect.isTrue "addOne is among the updated methods" (report.UpdatedMethods |> List.exists (fun name -> name.EndsWith "addOne"))
      })

    case Disposal "a disposed session can be disposed again" (fun session ->
      session.Dispose()
      session.Dispose())
  ]

[<Tests>]
let tests =
  testList "FsiSession port" [
    Integration.hostList "in-process session" [
      contract "InProcessFsiSession" newInProcess []

    ]

    Integration.hostList "isolated host session: isolation from SageFs" [
      testAsync "the host process loads no SageFs assembly and no 0Harmony" {
        do!
          withSession newRemote (fun session ->
            mustSucceed session "let loaded = System.AppDomain.CurrentDomain.GetAssemblies() |> Array.map (fun a -> a.GetName().Name) |> Array.sort |> String.concat \",\""
            let names = (string (session.BoundValue "loaded")).Split ','
            Expect.isFalse "no 0Harmony" (names |> Array.contains "0Harmony")
            let sageFs = names |> Array.filter (fun n -> n.StartsWith "SageFs" && n <> SageFs.FsiHostBuild.HostHarmonyName)
            Expect.isEmpty "no SageFs assembly" sageFs)
      }

      testAsync "a project's own Lib.Harmony loads beside the agent's, and the agent still works" {
        do!
          withSessionAsync newRemote (fun session ->
            async {
              // The user's Harmony (identity 0Harmony) is the very thing that used to collide with SageFs's.
              mustSucceed session (sprintf "#r @\"%s\"" (typeof<HarmonyLib.Harmony>.Assembly.Location))
              mustSucceed session "let ownHarmonyId = HarmonyLib.Harmony(\"the-projects-own\").Id"
              Expect.equal "the user's Harmony works" "the-projects-own" (string (session.BoundValue "ownHarmonyId"))
              let version (delta: int) = sprintf "module Reload =\n  let addOne (x: int) = x + %d" delta
              mustSucceed session (version 1)
              afterEval session (version 1) DetourPolicy.ApplyDetours DiscoveryPolicy.WhenChanged |> ignore
              mustSucceed session (version 100)
              let report = afterEval session (version 100) DetourPolicy.ApplyDetours DiscoveryPolicy.WhenChanged
              Expect.isTrue "the agent detoured beside it" (report.UpdatedMethods |> List.exists (fun name -> name.EndsWith "addOne"))
            })
      }
    ]

    Integration.hostList "isolated host session" [
      // Capabilities the isolated host does not have yet would be listed here (each becomes a running case when
      // implemented). The list is empty: the whole contract runs, agent included.
      contract "RemoteFsiSession" newRemote []
    ]
  ]
