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
open SageFs.RemoteFsiSession
open SageFs.Tests.TestInfrastructure

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private dotnet =
  match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
  | null
  | "" -> "dotnet"
  | path -> path

let private fsiArgs = [ "fsi"; "--noninteractive"; "--nologo"; "--readline-" ]

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
    return new InProcessFsiSession(session) :> IFsiSession
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
    | Result.Ok host -> return new RemoteFsiSession(host) :> IFsiSession
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

let private eval (session: IFsiSession) (code: string) = session.Eval(code, CancellationToken.None)

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

/// The behaviour EVERY IFsiSession implementation must have. Instantiated for each implementation, so the
/// in-process and isolated-host sessions are held to one specification. `notYet` lists the capabilities an
/// implementation does not have yet: those cases are pending (ignored, and counted as such), never skipped silently.
let contract (label: string) (create: unit -> Async<IFsiSession>) (notYet: Capability list) : Test =
  let case (capability: Capability) (name: string) (body: IFsiSession -> unit) =
    match List.contains capability notYet with
    | true -> ptestCase (sprintf "%s [%A: not yet in this implementation]" name capability) ignore
    | false -> testCaseAsync name (withSession create body)
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

    case Disposal "a disposed session can be disposed again" (fun session ->
      session.Dispose()
      session.Dispose())
  ]

[<Tests>]
let tests =
  testList "FsiSession port" [
    Integration.hostList "in-process session" [
      contract "InProcessFsiSession" newInProcess []

      testAsync "DynamicAssemblies exposes what FSI emitted (in-process only: hot reload reflects over these)" {
        do!
          withSession newInProcess (fun session ->
            mustSucceed session "type Marker = { Value: int };;"
            Expect.isNonEmpty "an assembly was emitted" session.DynamicAssemblies)
      }
    ]

    Integration.hostList "isolated host session" [
      // Capabilities the isolated host does not have yet would be listed here (each becomes a running case when
      // implemented). The list is empty: the whole contract runs. Only DynamicAssemblies is host-agent work.
      contract "RemoteFsiSession" newRemote []
    ]
  ]
