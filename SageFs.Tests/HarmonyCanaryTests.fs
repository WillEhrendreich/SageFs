module SageFs.Tests.HarmonyCanaryTests

open System
open System.Reflection
open System.Reflection.Emit
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Expecto
open Expecto.Flip
open HarmonyLib
open SageFs.Middleware.HotReloading
open SageFs.Middleware.HotReloadCore
open SageFs.DevReload

// ============================================================================
// Test helper types — each test gets unique methods to avoid cross-contamination
// from permanent Harmony patches within the same process.
// ============================================================================

type CanarySuccessMethods() =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Original (x: int) = x + 1

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Replacement (x: int) = x + 2

type CanaryNoPatchMethods() =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Untouched (x: int) = x * 3

type CanaryDegradedMethods() =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Target (x: int) = x + 10

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Alt (x: int) = x + 20

// ============================================================================
// Unit tests for validateDetourCanary and snapshotMethodState
// ============================================================================

let validateCanaryUnitTests = testList "validateDetourCanary" [

  testCase "returns DetourConfirmed when bytes differ from pre-detour" <| fun () ->
    let m = typeof<CanaryNoPatchMethods>.GetMethod("Untouched")
    let jitAddr, _currentBytes = DevReloadCanaryTests.snapshotOrFail m
    // Use bogus pre-detour bytes that won't match the real JIT code
    let bogusBytes = Array.create 16 0xCCuy
    validateDetourCanary jitAddr bogusBytes
    |> Expect.equal "should confirm detour when bytes differ" CanaryResult.DetourConfirmed

  testCase "returns BytesUnchanged when bytes match pre-detour" <| fun () ->
    let m = typeof<CanaryNoPatchMethods>.GetMethod("Untouched")
    let jitAddr, currentBytes = DevReloadCanaryTests.snapshotOrFail m
    validateDetourCanary jitAddr currentBytes
    |> Expect.equal "should report unchanged when same bytes" CanaryResult.BytesUnchanged

  testCase "snapshotMethodState returns Some for a valid method" <| fun () ->
    let m = typeof<CanaryNoPatchMethods>.GetMethod("Untouched")
    let _jitAddr, bytes = DevReloadCanaryTests.snapshotOrFail m
    bytes.Length
    |> Expect.equal "should be 16 bytes" 16
]

// ============================================================================
// Integration tests — real Harmony detour + canary
// ============================================================================

let integrationTests = testList "HarmonyCanary integration" [

  testCase "detourMethod with canary confirms successful Harmony detour" <| fun () ->
    let original = typeof<CanarySuccessMethods>.GetMethod("Original")
    let replacement = typeof<CanarySuccessMethods>.GetMethod("Replacement")
    let logger = SageFs.Utils.Log.asILogger()
    // Reset health tracker so we can observe transitions
    DevReloadHealthTracker.reset()
    // detourMethod does not throw: a Harmony that rejects the body comes back as Failed carrying the exception text, and a
    // detour whose native code did not change comes back as Ineffective. Both are this canary's red, on any runtime,
    // because a canary that excuses them is blind exactly when a runtime change has broken method patching.
    let applied = detourMethod logger original replacement
    applied
    |> Expect.equal
         (sprintf "the detour must land and the canary must see the native code change on %s" RuntimeInformation.FrameworkDescription)
         DetourApplied.Redirected
    CanarySuccessMethods.Original 5
    |> Expect.equal "calling the original now runs the replacement body" (CanarySuccessMethods.Replacement 5)
    match DevReloadHealthTracker.current() with
    | Degraded reason -> failtestf "a confirmed detour must not degrade the dev reload health, got: %s" reason
    | _ -> ()

  testCase "canary transitions to Degraded when unchanged snapshot" <| fun () ->
    let m = typeof<CanaryDegradedMethods>.GetMethod("Target")
    DevReloadHealthTracker.reset()
    let jitAddr, currentBytes = DevReloadCanaryTests.snapshotOrFail m
    // Validate canary with the current snapshot (no detour applied) → unchanged
    validateDetourCanary jitAddr currentBytes
    |> Expect.equal "should detect unchanged state" CanaryResult.BytesUnchanged
    // Verify the transition that detourMethod would perform
    DevReloadHealthTracker.transition
      (DevReloadHealth.Degraded (sprintf "Canary: bytes unchanged for %s" m.Name))
    match DevReloadHealthTracker.current() with
    | Degraded reason ->
      reason
      |> Expect.stringContains "should mention canary" "Canary"
    | other ->
      failwithf "expected Degraded but got %A" other

  testCase "canary gracefully handles CanaryError without crashing" <| fun () ->
    // CanaryError path: validateDetourCanary catches exceptions from
    // Marshal.ReadIntPtr/Copy. Verify the DU case round-trips.
    let err = CanaryResult.CanaryError (InvalidOperationException("test error"))
    match err with
    | CanaryResult.CanaryError ex ->
      ex.Message
      |> Expect.equal "should preserve error message" "test error"
    | other ->
      failwithf "expected CanaryError but got %A" other
]

// ============================================================================
// User code that ends in a call to a function value (`sprintf`, `failwithf`, any
// `f x` where f is a closure). F# marks that call `tail.`, and Harmony cannot put a
// PREFIX in front of a body that has one: the patch throws InvalidProgramException at
// Patch time and leaves the method unpatched. A TRANSPILER-only patch is fine.
//
// Measured in a SageFs session on .NET 11.0.0-rc.1 / Harmony 2.4.2: `tail. callvirt
// FSharpFunc.Invoke` + prefix fails (with or without `__originalMethod`), the same body
// with a transpiler (pass-through, or one that adds a call at the entry, GuardPatcher's
// shape) patches and runs, and the same prefix with a transpiler that drops `tail.`
// beside it patches and runs.
//
// Two product paths put a patch on user code. ValueReadTracking puts PREFIXES on readers
// and getters (the exposed one). GuardPatcher puts a TRANSPILER on the code a click
// reaches (not exposed, and pinned here so it stays that way).
// ============================================================================

/// How the fixture reader ends.
type ReaderEnding =
  /// `f "x"` as the last act, marked `tail.` the way F# marks a call to a function value.
  | EndsInTailCall
  /// The same call without the prefix.
  | EndsInPlainCall

/// A debug-built (optimizations off) app with the shape the tracker watches: a module value `greeting`
/// and a reader that stores it in a static field (so it escapes) and then calls the function it is handed.
type EmittedReader =
  { Assembly: Assembly
    /// `static string Use(FSharpFunc<string, string> f)`
    Use: MethodInfo }

let emitReader (ending: ReaderEnding) : EmittedReader =
  let name = sprintf "TailReader%s" (Guid.NewGuid().ToString "N")
  let asm = AssemblyBuilder.DefineDynamicAssembly(AssemblyName name, AssemblyBuilderAccess.Run)
  asm.SetCustomAttribute(
    CustomAttributeBuilder(
      typeof<System.Diagnostics.DebuggableAttribute>.GetConstructor [| typeof<System.Diagnostics.DebuggableAttribute.DebuggingModes> |],
      [| box (System.Diagnostics.DebuggableAttribute.DebuggingModes.Default ||| System.Diagnostics.DebuggableAttribute.DebuggingModes.DisableOptimizations) |]))
  let modb = asm.DefineDynamicModule name
  // module App = let greeting = "hello"
  let app = modb.DefineType("App", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  app.SetCustomAttribute(
    CustomAttributeBuilder(
      typeof<Microsoft.FSharp.Core.CompilationMappingAttribute>.GetConstructor [| typeof<Microsoft.FSharp.Core.SourceConstructFlags> |],
      [| box Microsoft.FSharp.Core.SourceConstructFlags.Module |]))
  let field = app.DefineField("greeting@1", typeof<string>, FieldAttributes.Assembly ||| FieldAttributes.Static)
  let cctor = app.DefineTypeInitializer().GetILGenerator()
  cctor.Emit(OpCodes.Ldstr, "hello")
  cctor.Emit(OpCodes.Stsfld, field)
  cctor.Emit(OpCodes.Ret)
  let getter =
    app.DefineMethod("get_greeting", MethodAttributes.Public ||| MethodAttributes.Static ||| MethodAttributes.SpecialName, typeof<string>, [||])
  let gil = getter.GetILGenerator()
  gil.Emit(OpCodes.Ldsfld, field)
  gil.Emit(OpCodes.Ret)
  let prop = app.DefineProperty("greeting", PropertyAttributes.None, typeof<string>, [||])
  prop.SetGetMethod getter
  // type Reader: static Sink; static Use f = Sink <- App.greeting; f "x"
  let reader = modb.DefineType("Reader", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  let sink = reader.DefineField("Sink", typeof<string>, FieldAttributes.Public ||| FieldAttributes.Static)
  let use' =
    reader.DefineMethod("Use", MethodAttributes.Public ||| MethodAttributes.Static, typeof<string>, [| typeof<string -> string> |])
  let il = use'.GetILGenerator()
  il.Emit(OpCodes.Call, getter)
  il.Emit(OpCodes.Stsfld, sink)
  il.Emit(OpCodes.Ldarg_0)
  il.Emit(OpCodes.Ldstr, "x")
  match ending with
  | EndsInTailCall -> il.Emit(OpCodes.Tailcall)
  | EndsInPlainCall -> ()
  il.Emit(OpCodes.Callvirt, typeof<string -> string>.GetMethod("Invoke"))
  il.Emit(OpCodes.Ret)
  app.CreateType() |> ignore
  let readerType = reader.CreateType()
  { Assembly = readerType.Assembly
    Use = readerType.GetMethod "Use" }

let private addQuestionMark : string -> string = fun s -> s + "?"

/// The fixture's reader, watched by a real Tracker started on its assembly. Returns what the ledger says
/// about the reader straight after `Track`, and again after the reader has run once.
let probeStatuses (ending: ReaderEnding) : SageFs.Middleware.ValueReads.ReaderStatus * SageFs.Middleware.ValueReads.ReaderStatus * string =
  let fixture = emitReader ending
  let tracker =
    SageFs.Middleware.ValueReadTracking.Tracker(
      { Mode = SageFs.Middleware.ValueReads.ReflectionReadMode.ProbeCallers
        HotLoop = SageFs.Middleware.ValueReads.HotLoopThreshold.standard
        Tiering = SageFs.Middleware.ValueReads.TieringChoice.TieringOffWhileWatching },
      SageFs.Middleware.ValueReadTracking.ReflectionClock.system)
  tracker.Track [ fixture.Assembly ]
  let status () =
    match tracker.Ledger.Status |> Map.toList with
    | [ _, status ] -> status
    | other -> failtestf "expected the one reader in the fixture to be in the ledger, got %A" other
  let before = status ()
  let returned = fixture.Use.Invoke(null, [| box addQuestionMark |]) |> string
  before, status (), returned

let tailCallTests = testList "user code that ends in a call to a function value" [

  testCase "WHY — a reader that ends in a plain call carries the value-read probe, and the probe fires when it runs" <| fun () ->
    let before, after, returned = probeStatuses EndsInPlainCall
    before |> Expect.equal "armed once the tracker has started" SageFs.Middleware.ValueReads.ReaderStatus.Armed
    returned |> Expect.equal "and the reader still returns what its function returns" "x?"
    match after with
    | SageFs.Middleware.ValueReads.ReaderStatus.Ran _ -> ()
    | other -> failtestf "the probe should have fired when the reader ran, got %A" other

  testCase "WHY — a reader that ends in `tail. callvirt Invoke` (what sprintf and failwithf compile to) carries the probe too, not 'Harmony couldn't patch it'" <| fun () ->
    let before, after, returned = probeStatuses EndsInTailCall
    match before with
    | SageFs.Middleware.ValueReads.ReaderStatus.Unwatchable why ->
      failtestf "the reader was refused its probe: %s" why
    | SageFs.Middleware.ValueReads.ReaderStatus.Armed -> ()
    | other -> failtestf "expected Armed, got %A" other
    returned |> Expect.equal "and the reader still returns what its function returns" "x?"
    match after with
    | SageFs.Middleware.ValueReads.ReaderStatus.Ran _ -> ()
    | other -> failtestf "the probe should have fired when the reader ran, got %A" other

  testCase "WHY — GuardPatcher guards the same tail-call body: its patch is a transpiler, which Harmony accepts there" <| fun () ->
    let fixture = emitReader EndsInTailCall
    let registry = SageFs.Features.GuardPatcher.shared
    let lease = registry.NewLease()
    match registry.Acquire(lease, fixture.Use) with
    | SageFs.Features.Acquired.Held ->
      let returned = fixture.Use.Invoke(null, [| box addQuestionMark |]) |> string
      registry.Release(lease, fixture.Use)
      returned |> Expect.equal "the guarded body still returns what its function returns" "x?"
    | SageFs.Features.Acquired.Refused reason -> failtestf "the guard patch was refused: %A" reason
]

// integrationTests calls DevReloadHealthTracker.reset()/.transition/.current()
// directly (process-global static state) AND applies real Harmony detours via
// detourMethod, which itself transitions the same tracker (DevReload.fs).
// Left in the parallel pool, its DevReloadHealthTracker-touching cases race each
// other and every other test whose detourMethod call (GuardCoexistence, the hot
// reload suites) transitions the same static — an order-dependent flake (a run
// can exit 2 on a spurious Degraded/BytesUnchanged mismatch).
//
// It is `testSequenced`, NOT `testSequencedGroup`. Measured on Expecto 11.0.0
// (a spans experiment, not read from the docs): a plain `testSequenced` test
// runs after the whole parallel pool has finished, alone, and so overlaps
// neither a parallel test nor another sequenced list in any file; a
// `testSequencedGroup` test runs INSIDE the parallel pool and is serialized only
// against the same group's other tests. This file used a named group and so still
// raced the parallel hot reload tests.
[<Tests>]
let allTests =
  testSequenced (testList "HarmonyCanary" [
    validateCanaryUnitTests
    integrationTests
    tailCallTests
  ])
