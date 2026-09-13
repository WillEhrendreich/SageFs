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

  ]

// ---------------------------------------------------------------------------
// BUG 2: Expecto tag 3 (AsyncFsCheck) — reflected property field resolution
// ---------------------------------------------------------------------------

[<Tests>]
let asyncFsCheckTag3Tests =
  testList "Issue40(R15) — Expecto AsyncFsCheck tag-3 property reflection" [

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

