module SageFs.Tests.MethodPatcherTests

open Expecto
open Expecto.Flip
open System.Reflection
open System
open HarmonyLib

type TestMethods() =
  static let mutable callCount = 0
  static let mutable patched = false
  static member CallCount with get() = callCount and set(v) = callCount <- v
  static member IsPatched with get() = patched and set(v) = patched <- v
  static member MethodToPatch (m: String) =
    callCount <- callCount + 1
    "shiny " + m
  // The same shape, but the body calls sprintf. Harmony copies the original's IL, and this is the IL that
  // the runtime rejects on net10 and net11 alike; it is its own method so the patchable case never depends on it.
  static member SprintfMethodToPatch (m: String) =
    callCount <- callCount + 1
    sprintf "shiny %s" m
  // Prefix patch - runs before original and does not alter control flow.
  static member PrefixPatch (m: String) = 
    patched <- true
  // Replacement method to verify Harmony can replace
  static member ReplacementMethod (m: String) = 
    callCount <- callCount + 1
    sprintf "patched %s" m
  static member MethodToTest m = TestMethods.MethodToPatch m + TestMethods.MethodToPatch m

[<Tests>]
let tests =
  // This suite installs real (process-global) Harmony patches AND mutates shared
  // static TestMethods state, so its own cases must run in order and never overlap
  // another Harmony-patching suite or a parallel test. `testSequenced` runs it after
  // the parallel pool, alone; a `testSequencedGroup` would run it INSIDE the pool
  // (see HarmonyCanaryTests for the measurement).
  testSequenced <| testList "method patcher tests" [
    testCase "test method data"
    <| fun _ -> 
      let t = typeof<TestMethods>
      let replacement = t.GetMethod("ReplacementMethod")
      let toPatch = t.GetMethod("MethodToPatch")
      replacement.ReturnType |> Expect.equal "return type equal" toPatch.ReturnType
      
    testCase "before patch"
    <| fun _ ->
      TestMethods.CallCount <- 0
      let result = TestMethods.MethodToTest ""
      (result.Contains "shiny") |> Expect.isTrue "is old method"
      TestMethods.CallCount |> Expect.equal "should call method twice" 2
      
    testCase "after patch using Harmony"
    <| fun _ ->
      TestMethods.IsPatched <- false
      let harmony = new Harmony("test.patch.prefix")
      let original = typeof<TestMethods>.GetMethod("MethodToPatch")
      let prefix = typeof<TestMethods>.GetMethod("PrefixPatch")
      harmony.Patch(original, prefix = new HarmonyMethod(prefix)) |> ignore
      TestMethods.MethodToPatch "test" |> ignore
      TestMethods.IsPatched |> Expect.isTrue "prefix patch should have been called"

    // Harmony copies the original's IL and rewrites it, and what the F# compiler emits for `sprintf`
    // depends on optimization rather than on the framework. The optimized IL is refused, on net10 and
    // net11 alike (both Release tiers pass this case), and the unoptimized IL that a Debug build emits
    // patches and runs. Asserting only the refusal left the suite red on every Debug run, which is the
    // lane the inner loop actually uses, so both are asserted and each branch says what it proves.
    testCase "the optimized sprintf IL is refused with InvalidProgramException on net10 and net11 alike, and the unoptimized one patches"
    <| fun _ ->
      let harmony = new Harmony("test.patch.sprintf")
      let original = typeof<TestMethods>.GetMethod("SprintfMethodToPatch")
      let prefix = typeof<TestMethods>.GetMethod("PrefixPatch")
      let debuggable = typeof<TestMethods>.Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()
      let unoptimized = not (isNull debuggable) && debuggable.IsJITOptimizerDisabled
      match unoptimized with
      | true ->
        TestMethods.IsPatched <- false
        harmony.Patch(original, prefix = new HarmonyMethod(prefix)) |> ignore
        TestMethods.SprintfMethodToPatch "test" |> ignore
        TestMethods.IsPatched |> Expect.isTrue "the prefix ran, so the unoptimized IL patched and the method is callable"
      | false ->
        Expect.throwsT<InvalidProgramException>
          "Harmony rewrites the original's IL, and the optimized IL the F# compiler emits for sprintf is rejected"
          (fun () -> harmony.Patch(original, prefix = new HarmonyMethod(prefix)) |> ignore)
  ]
