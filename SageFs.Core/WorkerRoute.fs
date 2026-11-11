namespace SageFs

open System

/// How the worker's origin gate treats a request.
[<RequireQualifiedAccess>]
type RouteAccess =
  /// Executes F# or mutates worker state: any cross-site signal or foreign
  /// origin is rejected (HttpOriginGuard.decide).
  | Mutating
  /// Reads state only: loopback origins pass, foreign origins are rejected.
  | ReadOnly
  /// The DevReload SSE stream, the one surface a browser page (the user's
  /// dev app on a loopback origin) legitimately reads cross-origin.
  | CrossOriginStream

/// The two things that name a request: its HTTP method and its path. One record, so a call site says which is which.
type RequestRoute = { HttpMethod: string; Path: string }

/// Every HTTP route a worker serves. This is the one home of the daemon/worker wire paths: the client
/// (`HttpWorkerClient`) and the server (`WorkerHttpTransport`) both name a route by its case, and the path,
/// method and access each case carries are in the exhaustive functions of `WorkerRoute` below. A new case does
/// not compile until it has a path, a method and an access.
[<RequireQualifiedAccess>]
type WorkerRoute =
  | DiagThreadpool
  | Status
  | LiveValues
  /// Runs one getter of the user's code on a click, so it is a POST like every other route that executes code.
  | LiveValuesEvaluate
  /// Changes how much of a class every later walk runs, so it is a POST like every other mutating route.
  | LiveValuesMode
  | Eval
  | Check
  | TypecheckSymbols
  | Completions
  /// Runs the compiler over the session's projects, so it is a POST like the other routes that ask the compiler.
  | References
  /// Asks which process the session's FSI runs in. A POST with a body, like the other routes the daemon asks one question of.
  | HostProcess
  | Cancel
  | CancelRequest
  | LoadScript
  | Reset
  | HardReset
  /// Replaces the FSI host when nothing in it would be lost, so it changes what the worker runs: a POST like the resets.
  | LevelRepl
  | RunTests
  | RunTestsStream
  | TestDiscovery
  /// Identity-preserving buffer eval for as-you-type live testing. Executes F# from the request body,
  /// the same mutating class as `Eval`.
  | EvalLiveTestFile
  | InstrumentationMaps
  | Shutdown
  | SessionWarmupContext
  | HotReload
  | HotReloadToggle
  /// Run the new initializer of a `let mutable` a save kept (rule 3 of the state spec). Writes the app's live
  /// state, so it is a POST like every other mutating route.
  | HotReloadResetState
  /// Switch the running app's reflection read mode (rule 2). Changes what the app's process watches, so it is
  /// a POST like every other mutating route.
  | HotReloadReflectionMode
  | HotReloadWatchAll
  | HotReloadUnwatchAll
  | HotReloadWatchProject
  | HotReloadUnwatchProject
  | HotReloadWatchDirectory
  | HotReloadUnwatchDirectory
  | RunApp
  | StopApp
  | AwaitAppChange
  /// Hold one test for a debugger, and release it. Both change what the host is doing, so both are POSTs.
  | DebugTest
  | DebugTestContinue
  /// Where the save pipeline takes its saves from, and the saves a landing hands it. Both change what the worker does.
  | SaveSource
  | ApplySaves
  | DevReloadStream
  /// The last terminal `ReloadOutcome`, past the server boundary, for a client that cannot hold the `DevReloadStream`
  /// SSE stream open: a dashboard poll, a health check, an editor extension with no standing connection to this
  /// port. Same JSON shape a live SSE subscriber gets. Loopback-only like every other read.
  | HotReloadLastOutcome

[<RequireQualifiedAccess>]
module WorkerRoute =

  /// The route's path, without a query string.
  let path (route: WorkerRoute) : string =
    match route with
    | WorkerRoute.DiagThreadpool -> "/diag/threadpool"
    | WorkerRoute.Status -> "/status"
    | WorkerRoute.LiveValues -> "/live-values"
    | WorkerRoute.LiveValuesEvaluate -> "/live-values/evaluate"
    | WorkerRoute.LiveValuesMode -> "/live-values/mode"
    | WorkerRoute.Eval -> "/eval"
    | WorkerRoute.Check -> "/check"
    | WorkerRoute.TypecheckSymbols -> "/typecheck-symbols"
    | WorkerRoute.Completions -> "/completions"
    | WorkerRoute.References -> "/references"
    | WorkerRoute.HostProcess -> "/host-process"
    | WorkerRoute.Cancel -> "/cancel"
    | WorkerRoute.CancelRequest -> "/cancel-request"
    | WorkerRoute.LoadScript -> "/load-script"
    | WorkerRoute.Reset -> "/reset"
    | WorkerRoute.HardReset -> "/hard-reset"
    | WorkerRoute.LevelRepl -> "/level-repl"
    | WorkerRoute.RunTests -> "/run-tests"
    | WorkerRoute.RunTestsStream -> "/run-tests-stream"
    | WorkerRoute.TestDiscovery -> "/test-discovery"
    | WorkerRoute.EvalLiveTestFile -> "/eval-live-test-file"
    | WorkerRoute.InstrumentationMaps -> "/instrumentation-maps"
    | WorkerRoute.Shutdown -> "/shutdown"
    | WorkerRoute.SessionWarmupContext -> "/warmup-context"
    | WorkerRoute.HotReload -> "/hotreload"
    | WorkerRoute.HotReloadToggle -> "/hotreload/toggle"
    | WorkerRoute.HotReloadResetState -> "/hotreload/reset-state"
    | WorkerRoute.HotReloadReflectionMode -> "/hotreload/reflection-mode"
    | WorkerRoute.HotReloadWatchAll -> "/hotreload/watch-all"
    | WorkerRoute.HotReloadUnwatchAll -> "/hotreload/unwatch-all"
    | WorkerRoute.HotReloadWatchProject -> "/hotreload/watch-project"
    | WorkerRoute.HotReloadUnwatchProject -> "/hotreload/unwatch-project"
    | WorkerRoute.HotReloadWatchDirectory -> "/hotreload/watch-directory"
    | WorkerRoute.HotReloadUnwatchDirectory -> "/hotreload/unwatch-directory"
    | WorkerRoute.RunApp -> "/run-app"
    | WorkerRoute.StopApp -> "/stop-app"
    | WorkerRoute.AwaitAppChange -> "/await-app-change"
    | WorkerRoute.DebugTest -> "/debug-test"
    | WorkerRoute.DebugTestContinue -> "/debug-test-continue"
    | WorkerRoute.SaveSource -> "/save-source"
    | WorkerRoute.ApplySaves -> "/apply-saves"
    | WorkerRoute.DevReloadStream -> "/__sagefs__/reload"
    | WorkerRoute.HotReloadLastOutcome -> "/hotreload/last-outcome"

  /// Whether the route is a read (GET) and with which access. Every route that is not listed here is a POST
  /// and is mutating by construction: there is no way to declare a POST read-only.
  let getAccess (route: WorkerRoute) : RouteAccess option =
    match route with
    | WorkerRoute.DiagThreadpool
    | WorkerRoute.Status
    | WorkerRoute.LiveValues
    | WorkerRoute.TestDiscovery
    | WorkerRoute.InstrumentationMaps
    | WorkerRoute.SessionWarmupContext
    | WorkerRoute.HotReload
    | WorkerRoute.HotReloadLastOutcome -> Some RouteAccess.ReadOnly
    | WorkerRoute.DevReloadStream -> Some RouteAccess.CrossOriginStream
    | WorkerRoute.LiveValuesEvaluate
    | WorkerRoute.LiveValuesMode
    | WorkerRoute.Eval
    | WorkerRoute.Check
    | WorkerRoute.TypecheckSymbols
    | WorkerRoute.Completions
    | WorkerRoute.References
    | WorkerRoute.HostProcess
    | WorkerRoute.Cancel
    | WorkerRoute.CancelRequest
    | WorkerRoute.LoadScript
    | WorkerRoute.Reset
    | WorkerRoute.HardReset
    | WorkerRoute.LevelRepl
    | WorkerRoute.RunTests
    | WorkerRoute.RunTestsStream
    | WorkerRoute.EvalLiveTestFile
    | WorkerRoute.Shutdown
    | WorkerRoute.HotReloadToggle
    | WorkerRoute.HotReloadResetState
    | WorkerRoute.HotReloadReflectionMode
    | WorkerRoute.HotReloadWatchAll
    | WorkerRoute.HotReloadUnwatchAll
    | WorkerRoute.HotReloadWatchProject
    | WorkerRoute.HotReloadUnwatchProject
    | WorkerRoute.HotReloadWatchDirectory
    | WorkerRoute.HotReloadUnwatchDirectory
    | WorkerRoute.RunApp
    | WorkerRoute.StopApp
    | WorkerRoute.AwaitAppChange
    | WorkerRoute.DebugTest
    | WorkerRoute.DebugTestContinue
    | WorkerRoute.SaveSource
    | WorkerRoute.ApplySaves -> None

  let httpMethod (route: WorkerRoute) : string =
    match getAccess route with
    | Some _ -> "GET"
    | None -> "POST"

  let access (route: WorkerRoute) : RouteAccess =
    match getAccess route with
    | Some declared -> declared
    | None -> RouteAccess.Mutating

  /// Every route, read from the type itself so a new case is in the table the moment it exists.
  let all : WorkerRoute list =
    Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<WorkerRoute>)
    |> Array.map (fun case -> Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, [||]) :?> WorkerRoute)
    |> Array.toList

  /// The route a request names, matched the way ASP.NET routing matches: method and path case-insensitively.
  let tryOfRequest (request: RequestRoute) : WorkerRoute option =
    all
    |> List.tryFind (fun route ->
      String.Equals(httpMethod route, request.HttpMethod, StringComparison.OrdinalIgnoreCase)
      && String.Equals(path route, request.Path, StringComparison.OrdinalIgnoreCase))

  /// The path with a query string cut off, for a caller that holds a request target.
  let withoutQuery (target: string) : string =
    match target.IndexOf '?' with
    | -1 -> target
    | at -> target.Substring(0, at)
