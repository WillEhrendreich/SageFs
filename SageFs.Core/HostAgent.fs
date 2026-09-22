/// The agent that lives where the user's code lives.
///
/// Hot reload (Harmony detours over redefined methods) and live testing (discovering and running the user's tests) both
/// act on assemblies, so they must run in the process that loaded those assemblies. For an in-process session that is
/// the worker; for an isolated session it is the FSI host, which shares no assembly with SageFs. This file is compiled
/// into BOTH (it is embedded in the host's sources), so it depends on nothing but the BCL, FSharp.Core and Harmony.
///
/// Everything here is deterministic given its inputs: the assemblies come in through `AssemblySources` and the test
/// frameworks through the executor list, so a fake host is a record literal. The `Agent` is the single owner of the
/// process's reload registry; the `afterEvalStep` it drives is a plain function over immutable state.
module SageFs.HostAgent

open System
open System.Reflection
open SageFs.Utils
open SageFs.Features.LiveTesting
open SageFs.Middleware.HotReloadCore

open SageFs.Middleware.ValueReadTracking

/// Whether the methods an eval redefined are re-pointed at their new bodies.
[<RequireQualifiedAccess>]
type DetourPolicy =
  /// Hot reload is on: detour every redefined method.
  | ApplyDetours
  /// Hot reload is off: still record the methods, so live testing can find and map tests, but change nothing.
  | RegisterOnly

/// When to look for tests after an eval.
[<RequireQualifiedAccess>]
type DiscoveryPolicy =
  /// Only when methods were redefined, or on the session's first scan.
  | WhenChanged
  /// Always: the caller expects a brand-new test value that redefined no existing method.
  | Forced

/// What the worker tells the agent about the eval that just finished.
type AfterEval =
  { EvaluatedCode: string
    Detours: DetourPolicy
    Discovery: DiscoveryPolicy
    /// Was this eval a file-watcher-triggered hot-reload save (WorkerMain's
    /// own re-eval of an edited source file), as opposed to the startup/init
    /// script or an interactive eval? A save is an ATTEMPT to reach whatever
    /// the app already holds and must never redefine what "held" means;
    /// everything else is the kind of eval that builds route tables and
    /// handler closures, so it updates State.AppHolds.
    IsFileSave: bool }

/// What the agent found. `UpdatedMethods` are the dotted names of the methods that were redefined.
/// `DetourReport` is the full picture behind that count: which mutable bindings
/// tore, were declined, or landed both legs — the detail `UpdatedMethods` alone
/// cannot express, and the reason a caller must not throw this field away.
type AfterEvalReport =
  { UpdatedMethods: string list
    DetourReport: DetourReport
    LiveTest: LiveTestHookResultDto
    AssemblyLoadErrors: AssemblyLoadError list }

/// The tests found among the assemblies a process already has loaded.
type Discovery =
  { Tests: TestCase array
    Providers: ProviderDescription list }

/// What the agent starts from: the project outputs to load, and further files whose directories resolve dependencies.
type AgentInit =
  { Projects: string list
    ResolveFrom: string list
    /// Hot reload watches where the app's module values go, so a redefined
    /// value can be patched only when nothing kept a copy (rule 2).
    ValueReads: ValueReadWatch }

/// The outcome of starting an agent: which projects could not be loaded, and why.
type AgentStarted =
  { LoadedProjects: string list
    AssemblyLoadErrors: AssemblyLoadError list }

/// An agent's answer, or the honest statement that there is no agent to ask (an isolated host that has gone away).
/// Never an empty report standing in for "I don't know".
type AgentReply<'a> =
  | AgentAnswered of 'a
  | AgentUnavailable of reason: string

/// The coverage the instrumented assemblies of a process recorded since it was last taken: the probe count and the packed
/// hit bitmap (base64 words, see CoverageBitmap.toBase64).
type CoverageReading =
  | NoCoverage
  | CoverageTaken of count: int * words: string

/// Where the agent looks for assemblies. Injected so the agent can be driven without a real process.
type AssemblySources =
  { /// The assemblies FSI has emitted so far (the last is the newest).
    Dynamic: unit -> Assembly[]
    /// Every assembly the process has loaded.
    Loaded: unit -> Assembly[] }

/// The sources of the process this code runs in.
let currentProcess (dynamic: unit -> Assembly[]) : AssemblySources =
  { Dynamic = dynamic
    Loaded = fun () -> AppDomain.CurrentDomain.GetAssemblies() }

/// The test frameworks whose presence makes an assembly worth scanning for tests.
let testFrameworkMarkers =
  [| "Expecto"
     "xunit.core"
     "xunit.v3.core"
     "nunit.framework"
     "Microsoft.VisualStudio.TestPlatform.TestFramework"
     "TUnit.Core" |]

type private Runner = TestCase -> Async<TestResult>

/// The first runner that recognises the test wins; a runner that does not know it answers NotRun.
let private firstAnswer (runners: Runner list) : Runner =
  fun test ->
    let rec loop remaining =
      async {
        match remaining with
        | [] -> return TestResult.NotRun
        | (run: Runner) :: rest ->
          match! run test with
          | TestResult.NotRun -> return! loop rest
          | answered -> return answered
      }
    loop runners

/// The reload registry for a process that has loaded `init`: resolves dependencies from the given directories and
/// loads every project output, keeping the ones that failed as typed errors rather than throwing.
let startState (init: AgentInit) : State =
  setupAssemblyResolver ()
  init.Projects |> List.iter registerSearchPath
  init.ResolveFrom |> List.iter registerSearchPath
  let results = init.Projects |> List.map AssemblyLoadError.loadAssembly
  let assemblies = results |> List.choose (function Ok a -> Some a | Error _ -> None)
  let errors = results |> List.choose (function Error e -> Some e | Ok _ -> None)
  errors |> List.iter (fun e -> Log.logWarn $"%s{AssemblyLoadError.describe e}")
  let methods =
    assemblies
    |> List.collect getAllMethods
    |> List.groupBy (fun m -> m.MethodInfo.Name)
    |> Map.ofList
  // A freshly-started session has run no #load and no interactive eval, so
  // nothing else has ever redefined a name the loaded projects expose — the
  // compiled copy is, by construction, the one and only copy anything calls.
  // Seeding AppHolds with it here means a save that redirects it is honestly
  // reported as reaching the running process from the very first save,
  // instead of reading as `NoEffect`/`PatchIneffective` until some later
  // non-file-save eval happens to touch the same name. Any subsequent
  // #load/interactive eval still overwrites this per name (see
  // `handleNewAsmFromRepl`'s AppHolds update), so a `#load`ed app's own copy
  // wins the moment it is defined, exactly as before.
  let appHolds =
    methods |> Map.map (fun _ ms -> (List.last ms).MethodInfo)
  { Methods = methods
    LastOpenModules = []
    LastAssembly = None
    ProjectAssemblies = assemblies
    AssemblyLoadErrors = errors
    LiveTestInit = LiveTestInit.Pending
    AppHolds = appHolds }

/// The outcome of one step: the new registry, the methods it redefined, the
/// full detour report behind that (bindings torn/declined included), and what
/// live testing found.
type StepResult =
  { State: State
    UpdatedMethods: string list
    DetourReport: DetourReport
    Hook: LiveTestHookResult }

/// Merge per-assembly hook results into one: providers deduplicated by name, tests concatenated, the runners chained.
let private mergeHooks (affected: TestId array) (results: LiveTestHookResult list) : LiveTestHookResult =
  { LiveTestHookResult.empty with
      DetectedProviders =
        results
        |> List.collect (fun r -> r.DetectedProviders)
        |> List.distinctBy (function
          | ProviderDescription.AttributeBased a -> a.Name
          | ProviderDescription.Custom c -> c.Name)
      DiscoveredTests = results |> List.map (fun r -> r.DiscoveredTests) |> Array.concat
      AffectedTestIds = affected
      RunTest = results |> List.map (fun r -> r.RunTest) |> firstAnswer }

/// One eval's worth of work over the newest emitted assembly: register/detour its methods, then look for tests.
/// Pure over its arguments (the detours themselves are the only effect, and RegisterOnly performs none).
let afterEvalStep (executors: TestExecutor list) (logger: ILogger) (state: State) (asm: Assembly) (request: AfterEval) : StepResult =
  let apply =
    match request.Detours with
    | DetourPolicy.ApplyDetours -> true
    | DetourPolicy.RegisterOnly -> false
  let state, detourReport =
    state
    |> getOpenModules request.EvaluatedCode
    |> handleNewAsmFromRepl logger apply request.IsFileSave asm
  let updated = detourReport.Redirected
  let firstScan = state.LiveTestInit = LiveTestInit.Pending && not (List.isEmpty state.ProjectAssemblies)
  let forced =
    match request.Discovery with
    | DiscoveryPolicy.Forced -> true
    | DiscoveryPolicy.WhenChanged -> false
  match not (List.isEmpty updated) || firstScan || forced with
  | false ->
    { State = state; UpdatedMethods = updated; DetourReport = detourReport; Hook = LiveTestHookResult.empty }
  | true ->
    let fromEval = LiveTestingHook.afterReload executors asm updated
    match firstScan with
    | false -> { State = state; UpdatedMethods = updated; DetourReport = detourReport; Hook = fromEval }
    | true ->
      // The first scan also covers the pre-built project assemblies, whose tests no eval ever redefined.
      let fromProjects =
        state.ProjectAssemblies
        |> List.map (fun projectAsm ->
          try LiveTestingHook.afterReload executors projectAsm []
          with _ -> LiveTestHookResult.empty)
      { State = { state with LiveTestInit = LiveTestInit.Done }
        UpdatedMethods = updated
        DetourReport = detourReport
        Hook = mergeHooks fromEval.AffectedTestIds (fromEval :: fromProjects) }

/// Whether this agent watches value reads, and the tracker when it does.
[<RequireQualifiedAccess>]
type private ValueReadTracker =
  | Tracking of Tracker
  | NotTracking

/// What a session that doesn't watch value reads says about reflection reads.
let private notTrackingReport : SageFs.Middleware.ValueReads.ReflectionReadsReport =
  { Mode = SageFs.Middleware.ValueReads.ReflectionReadMode.standard
    Watch = SageFs.Middleware.ValueReads.ReflectionWatchStatus.NotWatching "this session isn't watching value reads, because it isn't a hot reload session"
    Notices = []
    Walks = 0L
    SiteHits = 0L }

/// The agent of one process. It owns that process's reload registry (single owner: `AfterEval` is called from the one
/// eval thread, and the lock makes any other caller safe), the runner for tests defined interactively, and the runner
/// for tests found in the project assemblies.
[<Sealed>]
type Agent(init: AgentInit, sources: AssemblySources, executors: TestExecutor list) =
  let gate = obj ()
  let mutable state = startState init
  // Before anything else runs: the probes and the startup window's watch have
  // to be on before the user's first line of code, or a read could slip past.
  let valueReads =
    match init.ValueReads with
    | ValueReadWatch.IgnoreValueReads -> ValueReadTracker.NotTracking
    | ValueReadWatch.WatchValueReads reflection ->
      let tracker = Tracker(reflection, ReflectionClock.system)
      tracker.Track state.ProjectAssemblies
      ValueReadTracker.Tracking tracker
  let started : AgentStarted =
    { LoadedProjects = state.ProjectAssemblies |> List.map (fun a -> a.Location)
      AssemblyLoadErrors = state.AssemblyLoadErrors }
  let mutable dynamicRunner : Runner option = None
  let mutable projectRunner : Runner option = None
  let logger = Log.asILogger ()

  new(init, sources) = Agent(init, sources, BuiltInExecutors.builtIn)

  /// What starting found: the projects loaded, and the ones that could not be.
  member _.Started = started

  /// Process the eval that just finished. With nothing emitted yet there is nothing to report.
  member _.AfterEval(request: AfterEval) : AfterEvalReport =
    lock gate (fun () ->
      match sources.Dynamic() |> Array.tryLast with
      | None ->
        { UpdatedMethods = []
          DetourReport = DetourReport.empty
          LiveTest = LiveTestHookResultDto.fromResult LiveTestHookResult.empty
          AssemblyLoadErrors = state.AssemblyLoadErrors }
      | Some asm ->
        // Readers an eval just defined are found (and probed) before hot
        // reload detours anything onto them.
        match valueReads with
        | ValueReadTracker.Tracking tracker -> tracker.ScanEval asm
        | ValueReadTracker.NotTracking -> ()
        let step = afterEvalStep executors logger state asm request
        match valueReads with
        | ValueReadTracker.Tracking tracker -> tracker.EvalFinished request.IsFileSave
        | ValueReadTracker.NotTracking -> ()
        state <- step.State
        // Tests defined interactively stay runnable across evals that discover nothing: only a scan replaces the runner.
        match step.Hook.DiscoveredTests.Length > 0 || not (List.isEmpty step.Hook.DetectedProviders) with
        | true -> dynamicRunner <- Some step.Hook.RunTest
        | false -> ()
        { UpdatedMethods = step.UpdatedMethods
          DetourReport = step.DetourReport
          LiveTest = LiveTestHookResultDto.fromResult step.Hook
          AssemblyLoadErrors = step.State.AssemblyLoadErrors })

  /// Where each value's reads went, and which of the readers ran (rule 2).
  member _.ValueReads(values: string list) : SageFs.Middleware.ValueReads.ValueEvidence list =
    lock gate (fun () ->
      match valueReads with
      | ValueReadTracker.Tracking tracker -> tracker.Evidence values
      | ValueReadTracker.NotTracking ->
        values
        |> List.map (fun value -> SageFs.Middleware.ValueReads.ValueEvidence.Untracked(value, "this session isn't watching value reads, because it isn't a hot reload session")))

  /// Where the session's reflection reads stand: the mode, whether the watch
  /// is on, and every hot loop it has asked about. Runs beside evals: the
  /// tracker is thread-safe, and this never waits on the eval thread.
  member _.ReflectionReads() : SageFs.Middleware.ValueReads.ReflectionReadsReport =
    match valueReads with
    | ValueReadTracker.Tracking tracker -> tracker.ReflectionReads
    | ValueReadTracker.NotTracking -> notTrackingReport

  /// Switch the reflection read mode of the running app. No restart.
  member _.SetReflectionMode(mode: SageFs.Middleware.ValueReads.ReflectionReadMode) : SageFs.Middleware.ValueReads.ReflectionReadsReport =
    match valueReads with
    | ValueReadTracker.Tracking tracker -> tracker.SetMode mode
    | ValueReadTracker.NotTracking -> notTrackingReport

  /// Take the coverage the instrumented assemblies recorded, and reset it for the next run. Coverage lives in the process
  /// that ran the tests, so only its agent can read it.
  member _.TakeCoverage() : CoverageReading =
    let instrumentable =
      sources.Loaded()
      |> Array.filter (fun a -> try not a.IsDynamic && not (isNull a.Location) && a.Location <> "" with _ -> false)
    match CoverageProbes.discoverAndCollectHits instrumentable with
    | None -> NoCoverage
    | Some hits ->
      let bitmap = CoverageBitmap.ofBoolArray hits
      CoverageProbes.discoverAndResetHits instrumentable
      CoverageTaken(bitmap.Count, CoverageBitmap.toBase64 bitmap)

  /// The simple names of every assembly the process has loaded, sorted and distinct: what a warmup check asks, since only
  /// the process the user's code runs in knows.
  member _.LoadedAssemblyNames() : string list =
    sources.Loaded()
    |> Array.choose (fun a -> try Some(a.GetName().Name) with _ -> None)
    |> Array.toList
    |> List.distinct
    |> List.sort

  /// Scan the assemblies the process has loaded (the project's own, referencing a test framework) for tests, and keep
  /// the runner for them.
  member _.DiscoverLoaded() : Discovery =
    lock gate (fun () ->
      let referencesFramework (a: Assembly) =
        try a.GetReferencedAssemblies() |> Array.exists (fun r -> testFrameworkMarkers |> Array.contains r.Name)
        with ex ->
          Log.warn "[HostAgent] framework check failed for %s: %s" a.FullName ex.Message
          false
      let results =
        sources.Loaded()
        |> Array.filter referencesFramework
        |> Array.choose (fun asm ->
          try
            let hook = LiveTestingHook.afterReload executors asm []
            match hook.DiscoveredTests.Length > 0 with
            | true -> Some hook
            | false -> None
          with ex ->
            Log.error "[HostAgent] discovery failed for %s: %s" asm.FullName ex.Message
            None)
      projectRunner <-
        (match results with
         | [||] -> None
         | found -> Some(found |> Array.toList |> List.map (fun r -> r.RunTest) |> firstAnswer))
      { Tests = results |> Array.collect (fun r -> r.DiscoveredTests) |> Array.distinctBy (fun t -> t.Id)
        Providers =
          results
          |> Array.collect (fun r -> List.toArray r.DetectedProviders)
          |> Array.distinctBy (function
            | ProviderDescription.AttributeBased d -> d.Name
            | ProviderDescription.Custom d -> d.Name)
          |> Array.toList })

  /// Run one test: interactively defined tests first, then the project's.
  member _.RunTest(test: TestCase) : Async<TestResult> =
    let runners =
      lock gate (fun () -> [ yield! Option.toList dynamicRunner; yield! Option.toList projectRunner ])
    firstAnswer runners test
