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
/// Uses lazy patching: if WebApplication type is already loaded, patches immediately.
/// Otherwise, hooks AppDomain.AssemblyLoad to apply patches when ASP.NET Core loads.
/// Chesterton's fence: In FSI-hosted server scenarios (e.g. running Harmony inside
/// SageFs REPL), WebApplication type loads AFTER worker startup. Eager-only patching
/// silently misses these late-loaded types, leaving DevReload injection broken.
let install () =
  match isDisabled () with
  | true ->
    Log.info "[DevReload] Disabled via SAGEFS_DEVRELOAD env var"
  | false ->
    let harmony = HarmonyLib.Harmony("sagefs.devreload")
    let tryPatch () =
      try
        let waType = typeof<WebApplication>
        match waType with
        | null -> false
        | t ->
          patchMethod harmony t "Run" [| typeof<string> |]
          patchMethod harmony t "RunAsync" [| typeof<string> |]
          true
      with _ -> false
    // Try immediate patching first
    match tryPatch () with
    | true ->
      Log.info "[DevReload] Patches applied immediately"
    | false ->
      // Lazy patching: wait for Microsoft.AspNetCore to load
      Log.info "[DevReload] WebApplication not yet loaded — deferring patches to AssemblyLoad event"
      let mutable patched = false
      AppDomain.CurrentDomain.add_AssemblyLoad(AssemblyLoadEventHandler(fun _ args ->
        match patched || not (args.LoadedAssembly.GetName().Name = "Microsoft.AspNetCore") with
        | true -> ()
        | false ->
          match tryPatch () with
          | true ->
            patched <- true
            Log.info "[DevReload] Lazy patches applied after Microsoft.AspNetCore loaded"
          | false -> ()))

/// Manual helper for non-WebApplication hosts.
/// Call from user code: SageFs.DevReloadInjector.injectMiddleware app workerPort
let injectMiddleware (appBuilder: IApplicationBuilder) (port: int) =
  let mw = DevReloadMiddleware.createMiddleware port
  appBuilder.Use(Func<RequestDelegate, RequestDelegate>(mw)) |> ignore
