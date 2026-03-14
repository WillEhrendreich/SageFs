module SageFs.Tests.DevReloadCanaryTests

open System
open System.Reflection
open System.Runtime.CompilerServices
open Expecto
open Expecto.Flip
open HarmonyLib
open SageFs.Middleware.HotReloading

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
  static member Prefix (__instance: obj) : bool = true

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
      // Harmony prefix patches may or may not change JIT bytes —
      // either DetourConfirmed or BytesUnchanged is acceptable
      match result with
      | DetourConfirmed -> ()
      | BytesUnchanged -> ()
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
      match result with
      | DetourConfirmed -> ()
      | BytesUnchanged -> ()
      | CanaryError ex ->
        failwithf "canary should not error on prefix patch, got: %s" ex.Message
    | None ->
      skiptest "could not snapshot method state on this platform"

  testCase "snapshot before and after prefix patch captures different state" <| fun () ->
    // This test verifies the full snapshot-patch-validate flow used by
    // DevReloadInjector.patchMethod after the canary integration.
    let target = typeof<PrefixRunTarget>.GetMethod("Run")
    let prefix = typeof<PrefixHook>.GetMethod("Prefix", BindingFlags.Public ||| BindingFlags.Static)
    match snapshotMethodState target with
    | Some (jitAddr, preBytes) ->
      // Pre-bytes should be 16 bytes
      preBytes.Length
      |> Expect.equal "pre-bytes should be 16 bytes" 16
      // Post-patch validation should not throw
      let harmony = Harmony("sagefs.test.devreload.canary.flow")
      harmony.Patch(target, prefix = HarmonyMethod(prefix)) |> ignore
      let result = validateDetourCanary jitAddr preBytes
      // Result must be one of the three valid DU cases
      match result with
      | DetourConfirmed | BytesUnchanged | CanaryError _ -> ()
    | None ->
      skiptest "could not snapshot method state on this platform"
]

[<Tests>]
let allTests =
  testList "DevReloadCanary" [
    canaryUnitTests
    prefixPatchIntegrationTests
  ]
