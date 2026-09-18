module SageFs.Tests.TestDiscoveryMergeTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting
open SageFs.Tests.SharedGenerators

let private pick gen = (Gen.sample 1 gen).[0]

// ── Generators ──
//
// TestId is drawn from a small fixed pool so that `compiled` and `dynamic`
// arrays generated independently actually collide on TestId often enough to
// exercise the override behavior (a full 16-hex-char TestId.create hash
// would essentially never collide between two independently-generated
// arrays).

let private sharedIdPool =
  [ "t1"; "t2"; "t3"; "t4"; "t5"; "t6"; "t7"; "t8" ]

let private genSharedTestId : Gen<TestId> =
  Gen.elements sharedIdPool |> Gen.map TestId.TestId

/// A TestCase tagged with `tag` in both FullName and Labels, so a merged
/// entry's origin (compiled vs dynamic) can be asserted on a distinguishing
/// field without relying on TestId (the merge key) itself.
let private genTaggedTestCase (tag: string) : Gen<TestCase> =
  gen {
    let! id = genSharedTestId
    let! suffix = Gen.choose (0, 9999)
    return {
      Id = id
      FullName = sprintf "%s.Test%d" tag suffix
      DisplayName = sprintf "%s-%d" tag suffix
      Origin = TestOrigin.ReflectionOnly
      Labels = [ tag ]
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit
    }
  }

let private genCompiledTestCase = genTaggedTestCase "compiled"
let private genDynamicTestCase = genTaggedTestCase "dynamic"

/// A discovered-test array never carries two entries under the same
/// TestId — dedupe the generated array the same way real discovery would.
let private genDistinctArray (gen: Gen<TestCase>) : Gen<TestCase[]> =
  Gen.arrayOf gen |> Gen.map (Array.distinctBy (fun t -> t.Id))

let private genCompiledArray = genDistinctArray genCompiledTestCase
let private genDynamicArray = genDistinctArray genDynamicTestCase

let private ids (tests: TestCase[]) = tests |> Array.map (fun t -> t.Id) |> Set.ofArray

[<Tests>]
let testDiscoveryMergeTests =
  testList "TestDiscoveryMerge" [

    testPropertyWithConfig propConfig "merge x [||] = x (identity on empty dynamic)" <|
      fun (_seed: int) ->
        let compiled = pick genCompiledArray
        TestDiscoveryMerge.merge compiled [||] = compiled

    testPropertyWithConfig propConfig "merge [||] y = y (identity on empty compiled)" <|
      fun (_seed: int) ->
        let dynamic = pick genDynamicArray
        TestDiscoveryMerge.merge [||] dynamic = dynamic

    testPropertyWithConfig propConfig "dynamic overrides compiled on a shared TestId" <|
      fun (_seed: int) ->
        let compiled = pick genCompiledArray
        let dynamic = pick genDynamicArray
        let dynamicById = dynamic |> Array.map (fun t -> t.Id, t) |> Map.ofArray
        let merged = TestDiscoveryMerge.merge compiled dynamic
        let mergedById = merged |> Array.map (fun t -> t.Id, t) |> Map.ofArray
        let sharedIds = Set.intersect (ids compiled) (ids dynamic)
        sharedIds
        |> Set.forall (fun id ->
          mergedById.[id] = dynamicById.[id]
          && mergedById.[id].Labels = [ "dynamic" ])

    testPropertyWithConfig propConfig "merged count equals the union of TestIds" <|
      fun (_seed: int) ->
        let compiled = pick genCompiledArray
        let dynamic = pick genDynamicArray
        let merged = TestDiscoveryMerge.merge compiled dynamic
        let expected = Set.union (ids compiled) (ids dynamic) |> Set.count
        merged.Length = expected

    testPropertyWithConfig propConfig "no duplicate TestId in the merged result" <|
      fun (_seed: int) ->
        let compiled = pick genCompiledArray
        let dynamic = pick genDynamicArray
        let merged = TestDiscoveryMerge.merge compiled dynamic
        let mergedIds = merged |> Array.map (fun t -> t.Id)
        mergedIds.Length = (mergedIds |> Array.distinct |> Array.length)

    testPropertyWithConfig propConfig "merge is idempotent w.r.t. re-applying the same dynamic set" <|
      fun (_seed: int) ->
        let compiled = pick genCompiledArray
        let dynamic = pick genDynamicArray
        let once = TestDiscoveryMerge.merge compiled dynamic
        let twice = TestDiscoveryMerge.merge once dynamic
        twice = once

    testCase "compiled-only entries keep compiled order; dynamic-only entries append in dynamic order" <| fun _ ->
      let mk tag id =
        { Id = TestId.TestId id
          FullName = sprintf "%s.%s" tag id
          DisplayName = sprintf "%s-%s" tag id
          Origin = TestOrigin.ReflectionOnly
          Labels = [ tag ]
          Framework = TestFramework.Expecto
          Category = TestCategory.Unit }
      // compiled: c1, c2 (overridden by dynamic), c3
      // dynamic:  c2' (overrides c2), d1, d2 (new)
      let c1 = mk "compiled" "c1"
      let c2 = mk "compiled" "c2"
      let c3 = mk "compiled" "c3"
      let compiled = [| c1; c2; c3 |]
      let c2' = mk "dynamic" "c2"
      let d1 = mk "dynamic" "d1"
      let d2 = mk "dynamic" "d2"
      let dynamic = [| c2'; d1; d2 |]
      let merged = TestDiscoveryMerge.merge compiled dynamic
      merged
      |> Array.map (fun t -> t.FullName)
      |> Expect.equal
        "compiled order preserved for c1/c2(overridden)/c3, dynamic-only appended in dynamic order"
        [| "compiled.c1"; "dynamic.c2"; "compiled.c3"; "dynamic.d1"; "dynamic.d2" |]

    testCase "no TestId collision between compiled and dynamic returns their concatenation" <| fun _ ->
      let mk tag id =
        { Id = TestId.TestId id
          FullName = sprintf "%s.%s" tag id
          DisplayName = sprintf "%s-%s" tag id
          Origin = TestOrigin.ReflectionOnly
          Labels = [ tag ]
          Framework = TestFramework.Expecto
          Category = TestCategory.Unit }
      let compiled = [| mk "compiled" "a"; mk "compiled" "b" |]
      let dynamic = [| mk "dynamic" "c" |]
      let merged = TestDiscoveryMerge.merge compiled dynamic
      merged
      |> Array.map (fun t -> t.FullName)
      |> Expect.equal "compiled entries first, then dynamic-only" [| "compiled.a"; "compiled.b"; "dynamic.c" |]
  ]
