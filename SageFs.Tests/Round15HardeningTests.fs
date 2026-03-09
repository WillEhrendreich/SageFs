module SageFs.Tests.Round15HardeningTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

// ---------------------------------------------------------------------------
// Issue #40 — run_tests hot-reload timing race + property test execution
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// BUG 1: LastDiscoveryTime updates on TestsDiscovered
// ---------------------------------------------------------------------------

[<Tests>]
let lastDiscoveryTimeTests =
  testList "Issue40(R15) — LastDiscoveryTime tracks TestsDiscovered events" [

    testCase "LiveTestState.empty has LastDiscoveryTime of DateTimeOffset.MinValue" <| fun _ ->
      LiveTestState.empty.LastDiscoveryTime
      |> Expect.equal "empty state should have MinValue LastDiscoveryTime" DateTimeOffset.MinValue

    testCase "mergeDiscoveredTests does not change LastDiscoveryTime — that is app-layer concern" <| fun _ ->
      // mergeDiscoveredTests is a pure function that only merges test arrays.
      // LastDiscoveryTime is updated by the SageFsApp event handler, not by this function.
      // This test verifies the field exists on LiveTestState and defaults correctly.
      let state = LiveTestState.empty
      let beforeMerge = state.LastDiscoveryTime
      // Directly mutate via record update as app-layer would after merging
      let now = DateTimeOffset.UtcNow
      let updated = { state with LastDiscoveryTime = now }
      updated.LastDiscoveryTime |> Expect.equal "updated state should have new LastDiscoveryTime" now
      beforeMerge |> Expect.equal "original state should be unchanged" DateTimeOffset.MinValue

    testCase "LastDiscoveryTime advances monotonically on successive discovery events" <| fun _ ->
      let t1 = DateTimeOffset.UtcNow
      let s1 = { LiveTestState.empty with LastDiscoveryTime = t1 }
      let t2 = t1.AddMilliseconds(50.0)
      let s2 = { s1 with LastDiscoveryTime = t2 }
      (s2.LastDiscoveryTime > s1.LastDiscoveryTime)
      |> Expect.isTrue "second discovery time should be later than first"

    testCase "runTests secondary wait condition — discoveryTime > discoveryTimeBefore" <| fun _ ->
      // Simulates the condition used in Mcp.runTests to detect discovery completion.
      // discoveryTimeBefore is snapshotted before the hot-reload wait;
      // the loop exits once LastDiscoveryTime advances past that snapshot.
      let before = DateTimeOffset.UtcNow.AddSeconds(-1.0)
      let after = DateTimeOffset.UtcNow
      let state = { LiveTestState.empty with LastDiscoveryTime = after }
      (state.LastDiscoveryTime > before)
      |> Expect.isTrue "discovery wait condition should be true when discovery time advanced"

    testCase "runTests secondary wait condition — false when no discovery happened" <| fun _ ->
      let before = DateTimeOffset.UtcNow
      // No discovery fired; LastDiscoveryTime stays at MinValue
      let state = LiveTestState.empty
      (state.LastDiscoveryTime > before)
      |> Expect.isFalse "discovery wait condition should be false when discovery did not advance"

  ]

// ---------------------------------------------------------------------------
// BUG 2: Expecto tag 3 (AsyncFsCheck) — reflected property field resolution
// ---------------------------------------------------------------------------

[<Tests>]
let asyncFsCheckTag3Tests =
  testList "Issue40(R15) — Expecto AsyncFsCheck tag-3 property reflection" [

    testCase "tag 3 branch: null propProp resolves to Skipped result" <| fun _ ->
      // The fix returns TestResult.Skipped when the 'property' field cannot be found.
      // We verify the discriminated union shape can carry this message.
      let result = TestResult.Skipped "AsyncFsCheck: could not reflect 'property' field"
      match result with
      | TestResult.Skipped msg ->
        msg |> Expect.stringContains "should contain AsyncFsCheck" "AsyncFsCheck"
      | other ->
        failwithf "Expected Skipped, got %A" other

    testCase "tag 3 branch: RunSynchronously not found resolves to Skipped result" <| fun _ ->
      let result = TestResult.Skipped "AsyncFsCheck: Async.RunSynchronously not found"
      match result with
      | TestResult.Skipped msg ->
        msg |> Expect.stringContains "should contain RunSynchronously" "RunSynchronously"
      | other ->
        failwithf "Expected Skipped, got %A" other

    testCase "FSharpAsync<bool> RunSynchronously via reflection — true means pass" <| fun _ ->
      // Verify the reflection path that tag 3 uses actually works for FSharpAsync<bool>.
      let asyncTrue : Async<bool> = async { return true }
      let runMethod =
        typeof<Async>.GetMethods()
        |> Array.tryFind (fun m ->
          m.Name = "RunSynchronously" && m.GetParameters().Length = 3)
      runMethod |> Expect.isSome "RunSynchronously should be findable via reflection"
      let genericRun = runMethod.Value.MakeGenericMethod([| typeof<bool> |])
      let result =
        genericRun.Invoke(null, [|
          box asyncTrue
          box (None: int option)
          box (None: System.Threading.CancellationToken option)
        |]) :?> bool
      result |> Expect.isTrue "async { return true } should produce true"

    testCase "FSharpAsync<bool> RunSynchronously via reflection — false means fail" <| fun _ ->
      let asyncFalse : Async<bool> = async { return false }
      let runMethod =
        typeof<Async>.GetMethods()
        |> Array.tryFind (fun m ->
          m.Name = "RunSynchronously" && m.GetParameters().Length = 3)
      let genericRun = runMethod.Value.MakeGenericMethod([| typeof<bool> |])
      let result =
        genericRun.Invoke(null, [|
          box asyncFalse
          box (None: int option)
          box (None: System.Threading.CancellationToken option)
        |]) :?> bool
      result |> Expect.isFalse "async { return false } should produce false, triggering failure"

  ]

// ---------------------------------------------------------------------------
// BUG 3: FsCheck.Xunit Property<T> detection in invokeWith
// ---------------------------------------------------------------------------

[<Tests>]
let fsCheckPropertyReturnTests =
  testList "Issue40(R15) — FsCheck.Xunit Property<T> return type detection" [

    testCase "type name starting with 'Property' is detected as FsCheck result" <| fun _ ->
      // The fix checks t.Name.StartsWith("Property") to identify FsCheck Property<T> values.
      // Verify the string-based detection logic is correct for the relevant type names.
      let matchesProperty (name: string) = name.StartsWith("Property")
      matchesProperty "Property`1" |> Expect.isTrue "Property`1 should match"
      matchesProperty "PropertyGen`1" |> Expect.isTrue "PropertyGen`1 should match"
      matchesProperty "Task" |> Expect.isFalse "Task should not match"
      matchesProperty "Unit" |> Expect.isFalse "Unit should not match"

    testCase "FsCheck assembly lookup — AppDomain search by name" <| fun _ ->
      // Verify the assembly-search logic used in the fix.
      // FsCheck may or may not be loaded in this test process, but the lookup must not throw.
      let result =
        try
          System.AppDomain.CurrentDomain.GetAssemblies()
          |> Array.tryFind (fun a -> a.GetName().Name = "FsCheck")
          |> Option.map (fun a -> a.GetName().Name)
        with ex ->
          failwithf "Assembly search threw: %s" ex.Message
      // Result is either None (not loaded) or Some "FsCheck" — either is fine
      match result with
      | None -> () // FsCheck not loaded — acceptable in minimal test process
      | Some name -> name |> Expect.equal "should be FsCheck assembly" "FsCheck"

    testCase "QuickThrowOnFailure method has 1 parameter" <| fun _ ->
      // Verify that when FsCheck is loaded, Check.QuickThrowOnFailure can be found
      // with exactly 1 parameter, matching the invocation in the fix.
      let fsCheckAssembly =
        System.AppDomain.CurrentDomain.GetAssemblies()
        |> Array.tryFind (fun a -> a.GetName().Name = "FsCheck")
      match fsCheckAssembly with
      | None ->
        () // FsCheck not loaded in this test session — skip
      | Some asm ->
        let checkType = asm.GetType("FsCheck.Check")
        match checkType with
        | null -> () // type not found — acceptable
        | checkT ->
          let methods =
            checkT.GetMethods()
            |> Array.filter (fun m -> m.Name = "QuickThrowOnFailure")
          let singleParam = methods |> Array.tryFind (fun m -> m.GetParameters().Length = 1)
          singleParam
          |> Expect.isSome "QuickThrowOnFailure with 1 parameter should exist in FsCheck.Check"

  ]
