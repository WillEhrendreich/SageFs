module SageFs.Tests.DevReloadCanaryTests

open System
open System.Reflection
open System.Runtime.CompilerServices
open Expecto
open Expecto.Flip
open HarmonyLib
open SageFs.Middleware.HotReloading
open SageFs.Middleware.HotReloadCore

// ============================================================================
// Unique test helper types for DevReload prefix-patch canary tests.
// Each test uses unique types to avoid cross-contamination from permanent
// Harmony patches within the same process.
// ============================================================================

type PrefixRunTarget() =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.Run (url: string) = ()

type PrefixRunAsyncTarget() =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.RunAsync (url: string) = Threading.Tasks.Task.CompletedTask

type PrefixHook() =
  [<DefaultValue>]
  [<ThreadStatic>]
  static val mutable private ran: bool
  static member Ran
    with get () = PrefixHook.ran
    and set v = PrefixHook.ran <- v
  static member Prefix (__instance: obj) : bool =
    PrefixHook.ran <- true
    true

type SnapshotOnlyTarget() =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.Execute (x: int) = x + 1

// ============================================================================
// Unit tests: canary building blocks with instance methods (DevReload-style)
// ============================================================================

let canaryUnitTests = testList "canary unit" [

  testCase "snapshotMethodState captures bytes for prefix-patchable instance method" <| fun () ->
    let m = typeof<SnapshotOnlyTarget>.GetMethod("Execute")
    let result = snapshotMethodState m
    result
    |> Expect.isSome "should snapshot state for an instance method"
    match result with
    | Some (_, bytes) ->
      bytes.Length
      |> Expect.equal "should capture 16 bytes" 16
    | None -> ()

  testCase "canary reports BytesUnchanged when no patch applied" <| fun () ->
    let m = typeof<SnapshotOnlyTarget>.GetMethod("Execute")
    match snapshotMethodState m with
    | Some (jitAddr, currentBytes) ->
      let result = validateDetourCanary jitAddr currentBytes
      result
      |> Expect.equal "should detect unchanged bytes" CanaryResult.BytesUnchanged
    | None ->
      skiptest "could not snapshot method state on this platform"

  testCase "canary error path does not crash" <| fun () ->
    let err = CanaryResult.CanaryError (InvalidOperationException("test error"))
    match err with
    | CanaryResult.CanaryError ex ->
      ex.Message
      |> Expect.equal "should preserve error message" "test error"
    | other ->
      failwithf "expected CanaryError but got %A" other
]

// ============================================================================
// Integration tests: real Harmony prefix patch + canary (DevReload-style)
// ============================================================================

let prefixPatchIntegrationTests = testList "Harmony prefix patch" [

  testCase "canary validates prefix patch on Run-style instance method" <| fun () ->
    let target = typeof<PrefixRunTarget>.GetMethod("Run")
    let prefix = typeof<PrefixHook>.GetMethod("Prefix", BindingFlags.Public ||| BindingFlags.Static)
    match snapshotMethodState target with
    | Some (jitAddr, preBytes) ->
      let harmony = Harmony("sagefs.test.devreload.canary.run")
      harmony.Patch(target, prefix = HarmonyMethod(prefix)) |> ignore
      let result = validateDetourCanary jitAddr preBytes
      // The patch must actually fire: invoking the target after patching
      // must run the prefix hook (BytesUnchanged alone is NOT proof — a
      // silently-dead patch also reports unchanged bytes).
      PrefixHook.Ran <- false
      PrefixRunTarget().Run("http://localhost:0")
      PrefixHook.Ran
      |> Expect.isTrue "invoking the patched target must run the prefix hook"
      match result with
      | DetourConfirmed | BytesUnchanged -> ()
      | CanaryError ex ->
        failwithf "canary should not error on prefix patch, got: %s" ex.Message
    | None ->
      skiptest "could not snapshot method state on this platform"

  testCase "canary validates prefix patch on RunAsync-style instance method" <| fun () ->
    let target = typeof<PrefixRunAsyncTarget>.GetMethod("RunAsync")
    let prefix = typeof<PrefixHook>.GetMethod("Prefix", BindingFlags.Public ||| BindingFlags.Static)
    match snapshotMethodState target with
    | Some (jitAddr, preBytes) ->
      let harmony = Harmony("sagefs.test.devreload.canary.runasync")
      harmony.Patch(target, prefix = HarmonyMethod(prefix)) |> ignore
      let result = validateDetourCanary jitAddr preBytes
      PrefixHook.Ran <- false
      PrefixRunAsyncTarget().RunAsync("http://localhost:0").Wait()
      PrefixHook.Ran
      |> Expect.isTrue "invoking the patched target must run the prefix hook"
      match result with
      | DetourConfirmed | BytesUnchanged -> ()
      | CanaryError ex ->
        failwithf "canary should not error on prefix patch, got: %s" ex.Message
    | None ->
      skiptest "could not snapshot method state on this platform"
]

// ============================================================================
// Resilience tests: detourMethod handles stale FSI types gracefully
// ============================================================================

let nullLogger : SageFs.Utils.ILogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let resilienceTests = testList "detour resilience" [

  testCase "detourMethod handles loadable method without throwing" <| fun () ->
    // Exercises the full detourMethod path with a real loadable type.
    // The TypeLoadException catch clause is only triggered by real stale
    // FSI assemblies, but this validates the method signature and that
    // the canary + error handling don't crash on normal inputs.
    let m = typeof<SnapshotOnlyTarget>.GetMethod("Execute")
    try
      detourMethod nullLogger m m
    with ex ->
      failwithf "detourMethod should not propagate: %s" ex.Message
]

// prefixPatchIntegrationTests applies real Harmony patches (process-global
// IL rewrites via Harmony.Patch) — sequenced so this file's tests never
// overlap with each other, or with any other test running in the default
// parallel pool, the way InstrumentationTests is sequenced for its own
// process-global ActivitySource state.
[<Tests>]
let allTests =
  // Shared sequenced group with MethodPatcherTests: both install real Harmony
  // patches, which are process-global, so no Harmony-patching suite may run
  // concurrently with another. A shared group name serializes them across files
  // (a per-file `testSequenced` only orders within one file).
  testSequencedGroup "sagefs-harmony" (testList "DevReloadCanary" [
    canaryUnitTests
    prefixPatchIntegrationTests
    resilienceTests
  ])
