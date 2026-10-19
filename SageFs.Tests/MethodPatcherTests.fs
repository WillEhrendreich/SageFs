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

    testCase "patching a method whose body calls sprintf is refused with InvalidProgramException, on net10 and net11 alike"
    <| fun _ ->
      let harmony = new Harmony("test.patch.sprintf")
      let original = typeof<TestMethods>.GetMethod("SprintfMethodToPatch")
      let prefix = typeof<TestMethods>.GetMethod("PrefixPatch")
      Expect.throwsT<InvalidProgramException>
        "Harmony rewrites the original's IL, and the IL the F# compiler emits for sprintf is rejected"
        (fun () -> harmony.Patch(original, prefix = new HarmonyMethod(prefix)) |> ignore)
  ]
