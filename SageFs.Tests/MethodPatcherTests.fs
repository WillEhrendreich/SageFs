module SageFs.Tests.MethodPatcherTests

open Expecto
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
    sprintf "shiny %s" m
  // Prefix patch - runs before original, returns bool to indicate whether to skip original
  static member PrefixPatch (m: String) = 
    patched <- true
    false // Don't skip original method
  // Replacement method to verify Harmony can replace
  static member ReplacementMethod (m: String) = 
    callCount <- callCount + 1
    sprintf "patched %s" m
  static member MethodToTest m = TestMethods.MethodToPatch m + TestMethods.MethodToPatch m

[<Tests>]
let tests =

  testList "method patcher tests" [
    testCase "test method data"
    <| fun _ -> 
      let t = typeof<TestMethods>
      let replacement = t.GetMethod("ReplacementMethod")
      let toPatch = t.GetMethod("MethodToPatch")
      Expect.equal replacement.ReturnType toPatch.ReturnType "return type equal"
      
    testCase "before patch"
    <| fun _ ->
      TestMethods.CallCount <- 0
      let result = TestMethods.MethodToTest ""
      Expect.isTrue (result.Contains "shiny") "is old method"
      Expect.equal TestMethods.CallCount 2 "should call method twice"
      
    testCase "after patch using Harmony"
    <| fun _ ->
      TestMethods.IsPatched <- false
      let harmony = new Harmony("test.patch.prefix")
      let original = typeof<TestMethods>.GetMethod("MethodToPatch")
      let prefix = typeof<TestMethods>.GetMethod("PrefixPatch")
      
      // Add a prefix patch that sets a flag when called
      harmony.Patch(original, prefix = new HarmonyMethod(prefix)) |> ignore
      
      TestMethods.MethodToPatch "test" |> ignore
      Expect.isTrue TestMethods.IsPatched "prefix patch should have been called"
  ]
