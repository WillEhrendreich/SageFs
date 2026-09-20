module SageFs.Tests.SessionHealthTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.ProjectLoading
open SageFs.WarmUp

let private handle : WorkerHandle = { Pid = 4242; Port = Some 5000 }

let private project (role: ProjectRole) (name: string) : ClassifiedProject =
  { Path = sprintf "/repo/%s/%s.fsproj" name name
    Role = role
    PackageRefs = [] }

let private myApp = project ProjectRole.Executable "MyApp"

let private loadedAssembly (name: string) : LoadedAssembly =
  { Name = name; Path = sprintf "/bin/%s.dll" name; NamespaceCount = 1; ModuleCount = 1 }

let private openedNamespace (name: string) : OpenedBinding =
  { Name = name; Kind = OpenableKind.Namespace; Source = "reflection"; DurationMs = 0.0 }

let private failedOpen (name: string) (msg: string) : WarmupOpenFailure =
  { Name = name; Kind = OpenableKind.Namespace; ErrorMessage = msg; Diagnostics = []; RetryCount = 1; DurationMs = 0.0 }

/// A warmup context that genuinely loaded something — the "everything worked" baseline.
let private healthyWarmup : WarmupContext =
  { SourceFilesScanned = 3
    AssembliesLoaded = [ loadedAssembly "MyApp" ]
    NamespacesOpened = [ openedNamespace "MyApp" ]
    FailedOpens = []
    PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 42L }
    StartedAt = System.DateTimeOffset.UtcNow }

/// Case 1 (measured live, reproduced against a real un-built project): Ready,
/// a project resolved, but warmup loaded nothing at all and recorded no
/// failure either.
let private nothingLoadedWarmup : WarmupContext =
  { healthyWarmup with AssembliesLoaded = []; NamespacesOpened = [] }

/// Case 2 (measured live — the dashboard report: "Warmup: 0 assemblies, 0
/// opened, 1 failed"): Ready, NOTHING loaded, and warmup recorded the
/// synthetic "(auto-open discovery)" failure AppState.fs raises when
/// auto-open found no source files for the project.
let private autoOpenFailureWarmup : WarmupContext =
  { nothingLoadedWarmup with
      FailedOpens = [ failedOpen "(auto-open discovery)" "Auto-open was enabled but no source files were found for this project." ] }

/// Live-discovered third scenario (SessionHealthTests were corrected against
/// it): a normal, freshly `dotnet build`-ed single-entry-point project. Its
/// assembly DOES load (AssembliesLoaded non-empty) but there is nothing else
/// in it for auto-open to discover, so warmup records the SAME KIND of
/// "no namespaces/modules were found to open" failure as the broken case.
/// Reproduced live: evaluating fully-qualified code against this session
/// succeeds. This must NOT classify as Degraded — see the "common case"
/// test below.
let private assemblyLoadedButNothingToOpenWarmup : WarmupContext =
  { healthyWarmup with
      NamespacesOpened = []
      FailedOpens = [ failedOpen "(auto-open discovery)" "Auto-open was enabled and 1 source file(s) were scanned, but no namespaces/modules were found to open. If the project defines modules, ensure they are compiled into the project assembly (dotnet build) and are not hidden behind RequireQualifiedAccess." ] }

[<Tests>]
let sessionHealthClassifyTests = testList "SessionHealth.classify" [

  testCase "WHY — Starting/Building/Restarting can't be judged yet, so health must say Starting, not guess"
  <| fun _ ->
    SessionHealth.classify (SessionLifecycleStatus.Starting handle) [ myApp ] None
    |> Expect.equal "starting" SessionHealth.Starting
    SessionHealth.classify (SessionLifecycleStatus.Building("dotnet build", handle)) [ myApp ] None
    |> Expect.equal "building" SessionHealth.Starting
    SessionHealth.classify (SessionLifecycleStatus.Restarting (Some 1)) [ myApp ] None
    |> Expect.equal "restarting" SessionHealth.Starting

  testCase "WHY — Faulted must carry the real fault reason, not a generic label"
  <| fun _ ->
    SessionHealth.classify (SessionLifecycleStatus.Faulted (Some "boom")) [ myApp ] None
    |> Expect.equal "failed with reason" (SessionHealth.Failed "boom")

  testCase "WHY — a Faulted session with no recorded reason still reports Failed, not a blank verdict"
  <| fun _ ->
    match SessionHealth.classify (SessionLifecycleStatus.Faulted None) [ myApp ] None with
    | SessionHealth.Failed _ -> ()
    | other -> failwithf "expected Failed, got %A" other

  testCase "WHY — Stopped is Failed" <| fun _ ->
    SessionHealth.classify SessionLifecycleStatus.Stopped [ myApp ] None
    |> Expect.equal "stopped" (SessionHealth.Failed "Session is stopped.")

  testCase "WHY — Ready with no warmup data yet must stay quiet (Healthy), never invent a problem the daemon can't see"
  <| fun _ ->
    SessionHealth.classify (SessionLifecycleStatus.Ready handle) [ myApp ] None
    |> Expect.equal "healthy" SessionHealth.Healthy

  testCase "WHY — Ready, a project resolved, warmup genuinely loaded something: the common case must classify Healthy, not noisy"
  <| fun _ ->
    SessionHealth.classify (SessionLifecycleStatus.Ready handle) [ myApp ] (Some healthyWarmup)
    |> Expect.equal "healthy" SessionHealth.Healthy

  testCase "WHY — MEASURED CASE 1: Ready, a project resolved, but 0 assemblies/0 namespaces and no recorded failure — the un-built-project lie. Must be Degraded, not Healthy."
  <| fun _ ->
    match SessionHealth.classify (SessionLifecycleStatus.Ready handle) [ myApp ] (Some nothingLoadedWarmup) with
    | SessionHealth.Degraded reason ->
      reason |> Expect.stringContains "names the missing build" "build"
    | other -> failwithf "expected Degraded, got %A" other

  testCase "WHY — MEASURED CASE 2: Ready, warmup recorded the auto-open-discovery failure — dashboard showed this in red under two green indicators. Must be Degraded and must quote the real failure message."
  <| fun _ ->
    match SessionHealth.classify (SessionLifecycleStatus.Ready handle) [ myApp ] (Some autoOpenFailureWarmup) with
    | SessionHealth.Degraded reason ->
      reason |> Expect.stringContains "names the failing entry" "(auto-open discovery)"
      reason |> Expect.stringContains "reuses the real warmup error text, doesn't invent new prose" "no source files were found for this project"
    | other -> failwithf "expected Degraded, got %A" other

  testCase "WHY — a genuinely bare session (no projects requested/resolved) that loaded nothing is NOT degraded — nothing was expected, so nothing is wrong"
  <| fun _ ->
    SessionHealth.classify (SessionLifecycleStatus.Ready handle) [] (Some WarmupContext.empty)
    |> Expect.equal "bare session stays healthy" SessionHealth.Healthy

  testCase "WHY — LIVE-DISCOVERED REGRESSION GUARD: a normal, freshly-built single-entry-point project whose assembly DID load must stay Healthy even though warmup recorded the exact same kind of \"nothing to open\" failure as the broken case — assemblies-loaded is the tell, not the presence of a warmup failure. Verified live: eval against this shape of session actually succeeds."
  <| fun _ ->
    SessionHealth.classify (SessionLifecycleStatus.Ready handle) [ myApp ] (Some assemblyLoadedButNothingToOpenWarmup)
    |> Expect.equal "assembly loaded, nothing else to open — still healthy" SessionHealth.Healthy

  testCase "WHY — Evaluating is judged the same way Ready is; mid-eval isn't an excuse to hide a degraded verdict"
  <| fun _ ->
    match SessionHealth.classify (SessionLifecycleStatus.Evaluating handle) [ myApp ] (Some autoOpenFailureWarmup) with
    | SessionHealth.Degraded _ -> ()
    | other -> failwithf "expected Degraded, got %A" other
]

[<Tests>]
let sessionHealthProjectionTests = testList "SessionHealth projections" [

  testCase "WHY — label is a stable, one-word case name for every verdict" <| fun _ ->
    SessionHealth.label SessionHealth.Starting |> Expect.equal "starting" "Starting"
    SessionHealth.label SessionHealth.Healthy |> Expect.equal "healthy" "Healthy"
    SessionHealth.label (SessionHealth.Degraded "x") |> Expect.equal "degraded" "Degraded"
    SessionHealth.label (SessionHealth.Failed "x") |> Expect.equal "failed" "Failed"

  testCase "WHY — reason is None for the quiet verdicts and Some for the ones that must explain themselves" <| fun _ ->
    SessionHealth.reason SessionHealth.Starting |> Expect.isNone "starting has no reason"
    SessionHealth.reason SessionHealth.Healthy |> Expect.isNone "healthy has no reason"
    SessionHealth.reason (SessionHealth.Degraded "why") |> Expect.equal "degraded carries its reason" (Some "why")
    SessionHealth.reason (SessionHealth.Failed "why") |> Expect.equal "failed carries its reason" (Some "why")

  testCase "WHY — describeForAgent must be silent (None) on Healthy so get_fsi_status doesn't grow noise on the common case" <| fun _ ->
    SessionHealth.describeForAgent SessionHealth.Healthy |> Expect.isNone "no line for healthy"
    SessionHealth.describeForAgent SessionHealth.Starting |> Expect.isNone "no line while starting"

  testCase "WHY — describeForAgent surfaces the reason text for Degraded/Failed so an agent reading get_fsi_status sees it" <| fun _ ->
    match SessionHealth.describeForAgent (SessionHealth.Degraded "needs a build") with
    | Some line -> line |> Expect.stringContains "reason must appear in the agent-facing line" "needs a build"
    | None -> failwith "expected a line for Degraded"

  testCase "WHY — toJson's status field matches label, and carries reason only when there is one — this is the /api/sessions wire shape" <| fun _ ->
    let healthy = SessionHealth.toJson SessionHealth.Healthy
    healthy.status |> Expect.equal "healthy status" "Healthy"
    healthy.reason |> Expect.isNone "healthy has no reason"
    let degraded = SessionHealth.toJson (SessionHealth.Degraded "needs a build")
    degraded.status |> Expect.equal "degraded status" "Degraded"
    degraded.reason |> Expect.equal "degraded reason" (Some "needs a build")
]
