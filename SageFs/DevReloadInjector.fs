module SageFs.DevReloadInjector

open System
open System.Reflection
open System.Collections.Concurrent
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open System.Threading
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

/// Try to insert middleware at position 0 in the pipeline via reflection.
/// DevReload runs as outermost middleware to guarantee it executes before
/// the implicit endpoint routing. The middleware itself strips Accept-Encoding
/// only for explicit text/html requests so ResponseCompression doesn't
/// interfere with body-swap, while leaving API call compression intact.
let private tryInsertFirst (appBuilder: IApplicationBuilder) (mw: Func<RequestDelegate, RequestDelegate>) =
  try
    let appType = appBuilder.GetType()
    let abProp = appType.GetProperty("ApplicationBuilder", BindingFlags.NonPublic ||| BindingFlags.Instance)
    let target =
      match abProp with
      | null -> appBuilder :> obj
      | prop -> prop.GetValue(appBuilder)
    let compField = target.GetType().GetField("_components", BindingFlags.NonPublic ||| BindingFlags.Instance)
    match compField with
    | null -> false
    | f ->
      let components = f.GetValue(target) :?> System.Collections.IList
      components.Insert(0, mw)
      true
  with _ -> false

/// Inject the DevReload middleware into a WebApplication's pipeline.
/// Called by Harmony prefix or manually for non-WebApplication hosts.
let private injectInto (appBuilder: IApplicationBuilder) =
  let id = Runtime.CompilerServices.RuntimeHelpers.GetHashCode(appBuilder)
  match injectedInstances.TryAdd(id, true) with
  | false -> () // already injected
  | true ->
    let mw = Func<RequestDelegate, RequestDelegate>(DevReloadMiddleware.createMiddleware workerPort)
    match tryInsertFirst appBuilder mw with
    | true ->
      DevReload.DevReloadHealthTracker.transition DevReload.Injected
      Log.info "[DevReload] Auto-injected hot-reload middleware at pipeline head (worker port %d)" workerPort
    | false ->
      appBuilder.Use(mw) |> ignore
      DevReload.DevReloadHealthTracker.transition (DevReload.Degraded "middleware appended instead of inserted at position 0 — other middleware may short-circuit before DevReload")
      Log.warn "[DevReload] Auto-injected hot-reload middleware (worker port %d, fallback append — middleware ordering may cause missed injections)" workerPort

/// Harmony prefix: runs before WebApplication.Run()/RunAsync().
type private RunPrefix() =
  static member Prefix(__instance: obj) : bool =
    match isDisabled () || workerPort <= 0 with
    | true -> ()
    | false -> injectInto (__instance :?> IApplicationBuilder)
    true // always continue to original

/// Generic Harmony patch installer — patches a single method with the RunPrefix.
/// Returns true if the patch was applied, false if the method wasn't found.
let private patchMethod (harmony: HarmonyLib.Harmony) (targetType: Type) (methodName: string) (paramTypes: Type array) =
  match targetType.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Instance, paramTypes) with
  | null ->
    Log.warn "[DevReload] %s.%s not found — skipping patch" targetType.Name methodName
    false
  | m ->
    let prefix = typeof<RunPrefix>.GetMethod("Prefix", BindingFlags.NonPublic ||| BindingFlags.Static)
    harmony.Patch(m, prefix = HarmonyLib.HarmonyMethod(prefix)) |> ignore
    Log.info "[DevReload] Patched %s.%s" targetType.Name methodName
    true

/// Install Harmony patches on WebApplication.Run and RunAsync.
/// Uses lazy patching: if WebApplication type is already loaded, patches immediately.
/// Otherwise, hooks AppDomain.AssemblyLoad to apply patches when ASP.NET Core loads.
/// Chesterton's fence: In FSI-hosted server scenarios (e.g. running Harmony inside
/// SageFs REPL), WebApplication type loads AFTER worker startup. Eager-only patching
/// silently misses these late-loaded types, leaving DevReload injection broken.
let install () =
  match isDisabled () with
  | true ->
    DevReload.DevReloadHealthTracker.transition DevReload.Disabled
    Log.info "[DevReload] Disabled via SAGEFS_DEVRELOAD env var"
  | false ->
    DevReload.DevReloadHealthTracker.transition DevReload.PatchPending
    let harmony = HarmonyLib.Harmony("sagefs.devreload")
    let tryPatch () =
      try
        let waType = typeof<WebApplication>
        match waType with
        | null -> false
        | t ->
          let r1 = patchMethod harmony t "Run" [| typeof<string> |]
          let r2 = patchMethod harmony t "RunAsync" [| typeof<string> |]
          r1 || r2
      with ex ->
        Log.debug "[DevReload] tryPatch probe failed: %s" ex.Message
        false
    // Try immediate patching first
    match tryPatch () with
    | true ->
      Log.info "[DevReload] Patches applied immediately"
    | false ->
      // Lazy patching: wait for Microsoft.AspNetCore to load
      // Chesterton's fence: Interlocked.CompareExchange instead of mutable bool
      // documents concurrency intent — AssemblyLoad fires on the loading thread,
      // so concurrent loads could race. CAS makes it a provably one-shot guard.
      Log.info "[DevReload] WebApplication not yet loaded — deferring patches to AssemblyLoad event"
      let patched = ref 0
      AppDomain.CurrentDomain.add_AssemblyLoad(AssemblyLoadEventHandler(fun _ args ->
        match args.LoadedAssembly.GetName().Name = "Microsoft.AspNetCore" with
        | false -> ()
        | true ->
          match Interlocked.CompareExchange(patched, 1, 0) = 0 with
          | false -> () // already patched by another thread
          | true ->
            match tryPatch () with
            | true ->
              Log.info "[DevReload] Lazy patches applied after Microsoft.AspNetCore loaded"
            | false ->
              Interlocked.Exchange(patched, 0) |> ignore // reset so next load can retry
              DevReload.DevReloadHealthTracker.transition (DevReload.PatchFailed "WebApplication.Run/RunAsync methods not found after Microsoft.AspNetCore loaded")
              Log.warn "[DevReload] ⚠ Hot-reload middleware could NOT be installed. Browser auto-reload will not work. Ensure the project uses WebApplication.Run() or WebApplication.RunAsync()."))

/// Manual helper for non-WebApplication hosts.
/// Call from user code: SageFs.DevReloadInjector.injectMiddleware app workerPort
let injectMiddleware (appBuilder: IApplicationBuilder) (port: int) =
  let mw = DevReloadMiddleware.createMiddleware port
  appBuilder.Use(Func<RequestDelegate, RequestDelegate>(mw)) |> ignore
