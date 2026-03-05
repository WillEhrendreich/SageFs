module SageFs.DevReloadInjector

open System
open System.Reflection
open System.Collections.Concurrent
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open SageFs.Utils

/// Worker HTTP port — set after the worker server starts.
let mutable private workerPort = 0

/// Called by WorkerMain after the HTTP server starts.
let setWorkerPort port = workerPort <- port

/// Track which WebApplication instances already have the middleware injected.
/// Prevents duplicate injection when Run() is called multiple times in the REPL.
let private injectedInstances = ConcurrentDictionary<int, bool>()

let private isDisabled () =
  match Environment.GetEnvironmentVariable("SAGEFS_DEVRELOAD") with
  | "0" | "false" -> true
  | _ -> false

/// Inject the DevReload middleware into a WebApplication's pipeline.
/// Called by Harmony prefix or manually for non-WebApplication hosts.
let private injectInto (appBuilder: IApplicationBuilder) =
  let id = Runtime.CompilerServices.RuntimeHelpers.GetHashCode(appBuilder)
  match injectedInstances.TryAdd(id, true) with
  | false -> () // already injected
  | true ->
    let mw = DevReloadMiddleware.createMiddleware workerPort
    appBuilder.Use(Func<RequestDelegate, RequestDelegate>(mw)) |> ignore
    Log.info "[DevReload] Auto-injected hot-reload middleware (worker port %d)" workerPort

/// Harmony prefix: runs before WebApplication.Run()/RunAsync().
type private RunPrefix() =
  static member Prefix(__instance: obj) : bool =
    match isDisabled () || workerPort <= 0 with
    | true -> ()
    | false -> injectInto (__instance :?> IApplicationBuilder)
    true // always continue to original

/// Generic Harmony patch installer — patches a single method with the RunPrefix.
let private patchMethod (harmony: HarmonyLib.Harmony) (targetType: Type) (methodName: string) (paramTypes: Type array) =
  match targetType.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Instance, paramTypes) with
  | null -> Log.warn "[DevReload] %s.%s not found — skipping patch" targetType.Name methodName
  | m ->
    let prefix = typeof<RunPrefix>.GetMethod("Prefix", BindingFlags.Public ||| BindingFlags.Static)
    harmony.Patch(m, prefix = HarmonyLib.HarmonyMethod(prefix)) |> ignore
    Log.info "[DevReload] Patched %s.%s" targetType.Name methodName

/// Install Harmony patches on WebApplication.Run and RunAsync.
/// Safe to call early — the prefix is a no-op until setWorkerPort is called.
let install () =
  match isDisabled () with
  | true ->
    Log.info "[DevReload] Disabled via SAGEFS_DEVRELOAD env var"
  | false ->
    try
      let harmony = HarmonyLib.Harmony("sagefs.devreload")
      patchMethod harmony typeof<WebApplication> "Run" [| typeof<string> |]
      patchMethod harmony typeof<WebApplication> "RunAsync" [| typeof<string> |]
    with ex ->
      Log.warn "[DevReload] Harmony patch installation failed: %s" (ex.Message)

/// Manual helper for non-WebApplication hosts.
/// Call from user code: SageFs.DevReloadInjector.injectMiddleware app workerPort
let injectMiddleware (appBuilder: IApplicationBuilder) (port: int) =
  let mw = DevReloadMiddleware.createMiddleware port
  appBuilder.Use(Func<RequestDelegate, RequestDelegate>(mw)) |> ignore
