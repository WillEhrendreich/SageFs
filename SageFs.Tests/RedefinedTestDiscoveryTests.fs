/// A test that is evaluated again in an FSI session is still one test. Each evaluation lands in its own `FSI_NNNN`
/// container and the session's assembly keeps every one of them, so a scan of it finds the same test once per evaluation.
/// Nothing downstream needs the copies (the runner resolves a test by id and the later copy wins, the daemon merges by
/// id), but each one travels across the process boundary on every evaluation: 385 entries for 11 tests after 35 saves,
/// 89% of the reply, and the cost of reading it grows with the session.
module SageFs.Tests.RedefinedTestDiscoveryTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let private redefinedExecutor : TestExecutor =
  TestExecutor.AttributeBased
    { Description =
        { Name = TestFramework.Unknown "RedefinedFact"
          TestAttributes = [ "RedefinedFactAttribute" ]
          AssemblyMarker = "SageFs.Tests" }
      Execute = ReflectionExecutor.executeMethod
      TheoryAttributes = [] }

let private testsAssembly = typeof<FSI_0001.RedefinedAcrossEvals>.Assembly

/// The discovered tests that are the redefined one, whichever container they came from.
let private redefined (tests: TestCase list) : TestCase list =
  tests |> List.filter (fun test -> test.FullName.Contains "RedefinedAcrossEvals.SameTest")

[<Tests>]
let tests =
  testList "A test redefined in an FSI session is discovered once" [
    test "two evaluations of the same test are one test" {
      let result = TestOrchestrator.discoverAll [ redefinedExecutor ] testsAssembly
      redefined result.Tests
      |> List.length
      |> Expect.equal "the same name and id found in FSI_0001 and FSI_0002 is listed once" 1
    }

    testAsync "the one that is left runs the later definition, as the runner already does" {
      let result = TestOrchestrator.discoverAll [ redefinedExecutor ] testsAssembly
      match redefined result.Tests with
      | [ only ] ->
        let! outcome = result.RunTest only
        match outcome with
        | TestResult.Failed _ -> ()
        | other -> failtestf "the later evaluation fails, so a run of the test must fail, got %A" other
      | other -> failtestf "expected the redefined test once, found %d" (List.length other)
    }
  ]
