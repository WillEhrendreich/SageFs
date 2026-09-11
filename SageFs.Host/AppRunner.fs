/// Runs a session project's compiled entry point inside the worker and owns
/// the resulting app: one app per worker, started, observed and stopped here.
module SageFs.AppRunner

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open SageFs.AppRun

type EntryPoint = {
  Name: string
  Invoke: string array -> int
}

/// Applies environment variables for the app. Production writes the worker
/// process environment (one app per worker, like `dotnet run`'s child).
type SetEnv = (string * string) list -> unit

type StartTimeouts = {
  /// How long an entry point may run before building a host, after which it
  /// is treated as an app without a server.
  HostAppearGrace: TimeSpan
  /// How long a built host may take to start.
  HostStartTimeout: TimeSpan
}

let defaultTimeouts = {
  HostAppearGrace = TimeSpan.FromSeconds 10.0
  HostStartTimeout = TimeSpan.FromSeconds 90.0
}

let processEnv : SetEnv =
  fun vars -> for (key, value) in vars do Environment.SetEnvironmentVariable(key, value)

let rec private rootCause (ex: exn) =
  match ex with
  | :? TargetInvocationException as tie ->
    match tie.InnerException with
    | null -> ex
    | inner -> rootCause inner
  | :? AggregateException as agg when agg.InnerExceptions.Count = 1 -> rootCause agg.InnerExceptions.[0]
  | _ -> ex

let private describeCrash (ex: exn) =
  let root = rootCause ex
  sprintf "%s: %s" (root.GetType().Name) root.Message

/// The compiler's own answer: Assembly.EntryPoint.
let entryPointOf (asm: Assembly) : Result<EntryPoint, string> =
  match asm.EntryPoint with
  | null ->
    Error (sprintf "%s has no entry point, so it is not an executable. → Only projects with <OutputType>Exe</OutputType> can run." (string (asm.GetName().Name)))
  | method ->
    let name =
      match method.DeclaringType with
      | null -> method.Name
      | t -> sprintf "%s.%s" (string t.FullName) method.Name
    let takesArgs = method.GetParameters().Length > 0
    let invoke (args: string array) =
      let callArgs : obj array =
        match takesArgs with
        | true -> [| box args |]
        | false -> [||]
      match method.Invoke(null, callArgs) with
      | :? int as code -> code
      | _ -> 0
    Ok { Name = name; Invoke = invoke }

let private pathComparison =
  match OperatingSystem.IsWindows() with
  | true -> StringComparison.OrdinalIgnoreCase
  | false -> StringComparison.Ordinal

/// The assembly the session actually loaded for a project (its shadow copy),
/// so the app runs the same code the REPL and hot reload see.
let resolveProjectAssembly (projectTargets: (string * string) list) (projectPath: string) : Result<Assembly, string> =
  let samePath (a: string) (b: string) = String.Equals(Path.GetFullPath a, Path.GetFullPath b, pathComparison)
  match projectTargets |> List.tryFind (fun (project, _) -> samePath project projectPath) with
  | None -> Error (sprintf "%s is not loaded in this session. → Create a session that includes it." projectPath)
  | Some (_, target) ->
    let alreadyLoaded =
      AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a -> not a.IsDynamic && String.Equals(a.Location, target, pathComparison))
    match alreadyLoaded, File.Exists target with
    | Some asm, _ -> Ok asm
    | None, false -> Error (sprintf "The built assembly %s is missing. → Build the project (dotnet build), then hard-reset the session." target)
    | None, true ->
      try Ok (Assembly.LoadFrom target)
      with ex -> Error (sprintf "Could not load %s: %s" target ex.Message)

/// The project's Properties/launchSettings.json, like `dotnet run` reads it.
let readLaunchConfig (projectPath: string) : Result<LaunchConfig, string> =
  let projectDir =
    match Path.GetDirectoryName(Path.GetFullPath projectPath) with
    | null -> Path.GetFullPath "."
    | dir -> dir
  let file = Path.Combine(projectDir, "Properties", "launchSettings.json")
  match File.Exists file with
  | false -> Ok LaunchConfig.NoProfile
  | true ->
    parseLaunchSettings (File.ReadAllText file)
    |> Result.mapError (fun reason -> sprintf "%s → Fix %s or remove it." reason file)

/// Scopes hosting events to the run that raised them: the listener is
/// process-global, the AsyncLocal flows only through the run's own thread.
let private runScope = AsyncLocal<string>()

let private subscribeHosting (onEvent: string -> objnull -> unit) : IDisposable =
  let subscriptions = List<IDisposable>()
  let events =
    { new IObserver<KeyValuePair<string, objnull>> with
        member _.OnNext kv = onEvent kv.Key kv.Value
        member _.OnError _ = ()
        member _.OnCompleted() = () }
  let listeners =
    { new IObserver<DiagnosticListener> with
        member _.OnNext listener =
          match listener.Name with
          | "Microsoft.Extensions.Hosting" -> lock subscriptions (fun () -> subscriptions.Add(listener.Subscribe events))
          | _ -> ()
        member _.OnError _ = ()
        member _.OnCompleted() = () }
  let all = DiagnosticListener.AllListeners.Subscribe listeners
  { new IDisposable with
      member _.Dispose() =
        all.Dispose()
        lock subscriptions (fun () -> for s in subscriptions do s.Dispose()) }

let private defaultUrlIfUnset (config: IConfigurationBuilder) =
  match config with
  | :? IConfiguration as current when String.IsNullOrEmpty current["urls"] ->
    config.AddInMemoryCollection([ KeyValuePair<string, string | null>("urls", freeLoopbackUrl) ]) |> ignore
  | _ -> ()

let private addressesOf (host: IHost) : string list =
  match host.Services.GetService(typeof<IServer>) with
  | :? IServer as server ->
    match server.Features.Get<IServerAddressesFeature>() with
    | null -> []
    | feature -> feature.Addresses |> Seq.toList
  | _ -> []

let private stopHost (host: IHost) = async {
  try
    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
    do! host.StopAsync(cts.Token) |> Async.AwaitTask
  with ex ->
    SageFs.Utils.Log.warn "[AppRunner] Stopping the app host failed: %s" ex.Message
}

/// How the runner can end a live app.
type private AppHandle =
  | HostHandle of IHost
  | ThreadOnly

type private Owned =
  | Idle of last: AppRunState
  | Live of app: RunningApp * handle: AppHandle * restoreEnv: (string * string) list

let private stateOf = function
  | Idle last -> last
  | Live (app, _, _) -> AppRunState.Running app

type internal Msg =
  | Start of project: string * EntryPoint * LaunchPlan * AsyncReplyChannel<AppRunState>
  | Stop of AsyncReplyChannel<Result<AppRunState, string>>
  | Finished of runId: string * AppRunState
  | Await of runId: string * TaskCompletionSource<AppRunState>
  | Shutdown of AsyncReplyChannel<unit>

let private finishedState (project: string) (outcome: Result<int, exn>) =
  match outcome with
  | Ok code -> AppRunState.Exited (project, code, DateTime.UtcNow)
  | Error ex -> AppRunState.Crashed (project, describeCrash ex, DateTime.UtcNow)

let private launch
  (timeouts: StartTimeouts)
  (setEnv: SetEnv)
  (inbox: MailboxProcessor<Msg>)
  (project: string)
  (entry: EntryPoint)
  (plan: LaunchPlan)
  : Async<Owned> = async {
  let runId = Guid.NewGuid().ToString("N").Substring(0, 8)
  let startedAt = DateTime.UtcNow
  let restoreEnv =
    plan.EnvironmentVariables
    |> List.map (fun (key, _) ->
      key,
      match Environment.GetEnvironmentVariable key with
      | null -> ""
      | value -> value)
  setEnv plan.EnvironmentVariables
  let hostBuilding = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
  let hostBuilt = TaskCompletionSource<IHost>(TaskCreationOptions.RunContinuationsAsynchronously)
  let hostStarted = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
  let hostStopped = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
  let entryDone = TaskCompletionSource<Result<int, exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
  let onEvent (name: string) (payload: objnull) =
    match String.Equals(runScope.Value, runId, StringComparison.Ordinal) with
    | false -> ()
    | true ->
      match name, payload with
      | "HostBuilding", (:? IHostBuilder as builder) ->
        match plan.UrlPolicy with
        | UrlPolicy.FreeLoopbackPortIfUnset ->
          builder.ConfigureAppConfiguration(fun (_: HostBuilderContext) (config: IConfigurationBuilder) -> defaultUrlIfUnset config) |> ignore
        | UrlPolicy.ProjectConfigured -> ()
        hostBuilding.TrySetResult() |> ignore
      | "HostBuilt", (:? IHost as host) ->
        match host.Services.GetService(typeof<IHostApplicationLifetime>) with
        | :? IHostApplicationLifetime as lifetime ->
          lifetime.ApplicationStarted.Register(fun () -> hostStarted.TrySetResult() |> ignore) |> ignore
          lifetime.ApplicationStopped.Register(fun () -> hostStopped.TrySetResult() |> ignore) |> ignore
        | _ -> ()
        hostBuilt.TrySetResult host |> ignore
      | _ -> ()
  use _subscription = subscribeHosting onEvent
  let thread =
    Thread(
      (fun () ->
        runScope.Value <- runId
        try
          entryDone.TrySetResult(Ok (entry.Invoke [||])) |> ignore
        with ex ->
          entryDone.TrySetResult(Error ex) |> ignore),
      IsBackground = true,
      Name = sprintf "sagefs-app-%s" runId)
  thread.Start()

  let running endpoint = {
    RunId = runId
    Project = project
    EntryPoint = entry.Name
    Endpoint = endpoint
    StartedAt = startedAt }

  // A host's own lifetime is authoritative once it exists: `main` may return
  // early after a fire-and-forget RunAsync while the server keeps serving.
  let watchHost () =
    async {
      do! hostStopped.Task |> Async.AwaitTask
      let! _ = Task.WhenAny(entryDone.Task :> Task, Task.Delay(TimeSpan.FromSeconds 5.0)) |> Async.AwaitTask
      let outcome =
        match entryDone.Task.IsCompleted with
        | true -> entryDone.Task.Result
        | false -> Ok 0
      inbox.Post(Finished(runId, finishedState project outcome))
    } |> Async.Start

  let watchThread () =
    async {
      let! outcome = entryDone.Task |> Async.AwaitTask
      inbox.Post(Finished(runId, finishedState project outcome))
    } |> Async.Start

  let idle state =
    setEnv restoreEnv
    Idle state

  let! _ =
    Task.WhenAny(hostBuilding.Task, entryDone.Task :> Task, Task.Delay(timeouts.HostAppearGrace))
    |> Async.AwaitTask
  match hostBuilding.Task.IsCompleted, entryDone.Task.IsCompleted with
  | false, true -> return idle (finishedState project entryDone.Task.Result)
  | false, false ->
    watchThread ()
    return Live (running AppEndpoint.NoServer, ThreadOnly, restoreEnv)
  | true, _ ->
    let! _ =
      Task.WhenAny(hostStarted.Task, Task.Delay(timeouts.HostStartTimeout), (task {
        let! outcome = entryDone.Task
        match outcome, hostBuilt.Task.IsCompleted with
        | Error _, _ | Ok _, false -> return ()
        | Ok _, true -> do! Task.Delay(Timeout.Infinite)
      } :> Task))
      |> Async.AwaitTask
    match hostStarted.Task.IsCompleted, entryDone.Task.IsCompleted with
    | true, _ ->
      let host = hostBuilt.Task.Result
      watchHost ()
      return Live (running (endpointFromAddresses (addressesOf host)), HostHandle host, restoreEnv)
    | false, true when not hostBuilt.Task.IsCompleted || Result.isError entryDone.Task.Result ->
      return idle (finishedState project entryDone.Task.Result)
    | false, _ ->
      match hostBuilt.Task.IsCompleted with
      | true -> do! stopHost hostBuilt.Task.Result
      | false -> ()
      return idle (AppRunState.Crashed (
        project,
        sprintf "The app built its host but did not start within %.0fs. → Check its startup code and logs." timeouts.HostStartTimeout.TotalSeconds,
        DateTime.UtcNow))
}

type Runner(timeouts: StartTimeouts, setEnv: SetEnv) =
  let snapshot = ref AppRunState.NotRunning
  let agent =
    MailboxProcessor<Msg>.Start(fun inbox ->
      let publish (owned: Owned) = snapshot.Value <- stateOf owned
      let settle (waiters: TaskCompletionSource<AppRunState> list) (state: AppRunState) =
        for w in waiters do w.TrySetResult state |> ignore
      let rec loop (owned: Owned) (waiters: TaskCompletionSource<AppRunState> list) = async {
        let! msg = inbox.Receive()
        match msg with
        | Start (project, entry, plan, reply) ->
          match owned with
          | Live (app, _, _) ->
            reply.Reply(AppRunState.Running app)
            return! loop owned waiters
          | Idle _ ->
            snapshot.Value <- AppRunState.Starting (project, StartPhase.LaunchingEntryPoint, DateTime.UtcNow)
            let! next = launch timeouts setEnv inbox project entry plan
            publish next
            reply.Reply(stateOf next)
            settle waiters (stateOf next)
            return! loop next []
        | Stop reply ->
          match owned with
          | Idle _ ->
            let next = Idle AppRunState.NotRunning
            publish next
            reply.Reply(Ok AppRunState.NotRunning)
            settle waiters AppRunState.NotRunning
            return! loop next []
          | Live (app, ThreadOnly, _) ->
            reply.Reply(Error (sprintf "%s has no host to stop, so it cannot be stopped in place. → Hard-reset the session to stop it." app.EntryPoint))
            return! loop owned waiters
          | Live (_, HostHandle host, restoreEnv) ->
            let next = Idle AppRunState.NotRunning
            publish next
            do! stopHost host
            setEnv restoreEnv
            reply.Reply(Ok AppRunState.NotRunning)
            settle waiters AppRunState.NotRunning
            return! loop next []
        | Finished (runId, final) ->
          match owned with
          | Live (app, _, restoreEnv) when app.RunId = runId ->
            setEnv restoreEnv
            let next = Idle final
            publish next
            settle waiters final
            return! loop next []
          | _ -> return! loop owned waiters
        | Await (runId, waiter) ->
          match owned with
          | Live (app, _, _) when app.RunId = runId -> return! loop owned (waiter :: waiters)
          | _ ->
            waiter.TrySetResult(stateOf owned) |> ignore
            return! loop owned waiters
        | Shutdown reply ->
          match owned with
          | Live (_, HostHandle host, restoreEnv) ->
            do! stopHost host
            setEnv restoreEnv
          | Live (_, ThreadOnly, _) | Idle _ -> ()
          settle waiters AppRunState.NotRunning
          reply.Reply()
      }
      loop (Idle AppRunState.NotRunning) [])

  member internal _.Agent = agent
  member _.State = snapshot.Value

  interface IDisposable with
    member _.Dispose() =
      try agent.PostAndReply((fun reply -> Shutdown reply), 15_000)
      with :? TimeoutException -> SageFs.Utils.Log.warn "[AppRunner] Shutdown timed out"
      (agent :> IDisposable).Dispose()

  interface IAsyncDisposable with
    member _.DisposeAsync() =
      ValueTask(task {
        try
          do! agent.PostAndAsyncReply((fun reply -> Shutdown reply), 15_000) |> Async.StartAsTask
        with :? TimeoutException -> SageFs.Utils.Log.warn "[AppRunner] Shutdown timed out"
        (agent :> IDisposable).Dispose() })

let create (timeouts: StartTimeouts) (setEnv: SetEnv) = new Runner(timeouts, setEnv)

/// Returns the running app unchanged when one is already running.
let start (runner: Runner) (project: string) (entry: EntryPoint) (plan: LaunchPlan) : Task<AppRunState> =
  runner.Agent.PostAndAsyncReply(fun reply -> Start(project, entry, plan, reply)) |> Async.StartAsTask

let stop (runner: Runner) : Task<Result<AppRunState, string>> =
  runner.Agent.PostAndAsyncReply(fun reply -> Stop reply) |> Async.StartAsTask

let state (runner: Runner) = runner.State

/// Completes when the app is no longer Running with this run id (or when
/// the token cancels, with whatever the state is then).
let awaitChange (runner: Runner) (runId: string) (ct: CancellationToken) : Task<AppRunState> =
  let waiter = TaskCompletionSource<AppRunState>(TaskCreationOptions.RunContinuationsAsynchronously)
  ct.Register(fun () -> waiter.TrySetResult(runner.State) |> ignore) |> ignore
  runner.Agent.Post(Await(runId, waiter))
  waiter.Task
