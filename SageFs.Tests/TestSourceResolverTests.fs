module SageFs.Tests.TestSourceResolverTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features.CellDependencyGraph
open SageFs.Features.TestSourceResolver

let private emptyGraph : CellGraph = { Cells = Map.empty; Edges = [] }

let private makeTest (fullName: string) (origin: TestOrigin) : TestCase = {
  Id          = TestId.create fullName TestFramework.Expecto
  FullName    = fullName
  DisplayName = fullName
  Origin      = origin
  Labels      = []
  Framework   = TestFramework.Expecto
  Category    = TestCategory.Unit
}

[<Tests>]
let tests =
  testList "TestSourceResolver" [

    test "resolveTestLocations with empty graph and empty tests returns empty list" {
      let result = resolveTestLocations emptyGraph []
      result |> Expect.isEmpty "should return empty list for no tests"
    }

    test "resolveTestLocations with empty graph and ReflectionOnly tests returns empty list" {
      let tests = [
        makeTest "MyModule.test1" TestOrigin.ReflectionOnly
        makeTest "MyModule.test2" TestOrigin.ReflectionOnly
      ]
      let result = resolveTestLocations emptyGraph tests
      result |> Expect.isEmpty "ReflectionOnly tests have no location data"
    }

    // WHY — `resolveTestLocations` dropping ReflectionOnly tests is correct for a
    // caller that needs a file and line, and catastrophic for a caller that needs
    // the TEST LIST. `list_tests` is the latter, and every test in a
    // compiled-project session is ReflectionOnly, so it reported TotalCount 0
    // while /api/live-testing/status reported three passing tests for the same
    // session — confirmed live. `resolveAllTests` must never lose a test.
    test "resolveAllTests keeps ReflectionOnly tests that resolveTestLocations discards" {
      let tests = [
        makeTest "MyModule.test1" TestOrigin.ReflectionOnly
        makeTest "MyModule.test2" TestOrigin.ReflectionOnly
      ]
      resolveTestLocations emptyGraph tests
      |> Expect.isEmpty "the location-only view still has nothing to offer"

      let all = resolveAllTests emptyGraph tests
      all |> List.length |> Expect.equal "every test survives, located or not" 2
      all
      |> List.forall (function
        | ResolvedTest.Unlocated _ -> true
        | ResolvedTest.Located _ -> false)
      |> Expect.isTrue "both are carried as Unlocated, not as a fabricated path"
    }

    test "resolveAllTests keeps a SourceMapped test Located and a ReflectionOnly one Unlocated" {
      let tests = [
        makeTest "MyModule.mapped" (TestOrigin.SourceMapped("/src/MyModule.fs", 42))
        makeTest "MyModule.reflected" TestOrigin.ReflectionOnly
      ]
      let all = resolveAllTests emptyGraph tests
      all |> List.length |> Expect.equal "neither kind is dropped" 2
      all
      |> List.choose (function
        | ResolvedTest.Located l -> Some l.TestName
        | ResolvedTest.Unlocated _ -> None)
      |> Expect.equal "the mapped one keeps its location" [ "MyModule.mapped" ]
      all
      |> List.choose (function
        | ResolvedTest.Unlocated(name, _) -> Some name
        | ResolvedTest.Located _ -> None)
      |> Expect.equal "the reflected one is reported without inventing one" [ "MyModule.reflected" ]
    }

    test "resolveAllTests never returns fewer tests than it was given" {
      for origins in [ []; [ TestOrigin.ReflectionOnly ]
                       [ TestOrigin.SourceMapped("/a.fs", 1); TestOrigin.ReflectionOnly ]
                       [ TestOrigin.ReflectionOnly; TestOrigin.ReflectionOnly; TestOrigin.SourceMapped("/b.fs", 9) ] ] do
        let tests = origins |> List.mapi (fun i o -> makeTest (sprintf "M.t%d" i) o)
        resolveAllTests emptyGraph tests
        |> List.length
        |> Expect.equal (sprintf "no test may be lost for %d input(s)" tests.Length) tests.Length
    }

    test "resolveTestLocations with SourceMapped test returns correct file path and line" {
      let tests = [ makeTest "MyModule.test1" (TestOrigin.SourceMapped ("/src/MyModule.fs", 42)) ]
      let result = resolveTestLocations emptyGraph tests
      result |> Expect.hasLength "one SourceMapped test should yield one location" 1
      let loc = List.head result
      loc.FilePath  |> Expect.equal "file path should match" "/src/MyModule.fs"
      loc.StartLine |> Expect.equal "start line should match" 42
      loc.EndLine   |> Expect.equal "end line equals start for single-line" 42
      loc.TestName  |> Expect.equal "test name should be FullName" "MyModule.test1"
    }

    test "resolveTestLocations skips ReflectionOnly and keeps SourceMapped in mixed list" {
      let tests = [
        makeTest "MyModule.test1" (TestOrigin.SourceMapped ("/src/MyModule.fs", 10))
        makeTest "MyModule.test2" TestOrigin.ReflectionOnly
        makeTest "MyModule.test3" (TestOrigin.SourceMapped ("/src/MyModule.fs", 20))
      ]
      let result = resolveTestLocations emptyGraph tests
      result |> Expect.hasLength "should return only SourceMapped tests" 2
      result |> List.map (fun l -> l.TestName)
             |> Expect.containsAll "should contain the two SourceMapped tests"
                  [ "MyModule.test1"; "MyModule.test3" ]
    }

    test "resolveTestLocations TestSourceLocation record fields are correct" {
      let tests = [ makeTest "Suite.myTest" (TestOrigin.SourceMapped ("/path/to/File.fs", 7)) ]
      let result = resolveTestLocations emptyGraph tests
      let loc = List.head result
      loc.TestName  |> Expect.equal "TestName should be FullName" "Suite.myTest"
      loc.FilePath  |> Expect.equal "FilePath should match" "/path/to/File.fs"
      loc.StartLine |> Expect.equal "StartLine should be 7" 7
      loc.EndLine   |> Expect.equal "EndLine should equal StartLine" 7
    }

    test "resolveTestLocations CellId is -1 when graph has no matching cell" {
      let graph = {
        Cells = Map.ofList [ 1, { Id = 1; Source = "let unrelated = 42"; Produces = ["unrelated"]; Consumes = [] } ]
        Edges = []
      }
      let tests = [ makeTest "MyModule.test1" (TestOrigin.SourceMapped ("/src/MyModule.fs", 5)) ]
      let result = resolveTestLocations graph tests
      (List.head result).CellId |> Expect.equal "CellId should be -1 when no cell matches" -1
    }

    test "resolveTestLocations CellId matches when cell Produces contains test name substring" {
      let graph = {
        Cells = Map.ofList [ 42, { Id = 42; Source = "let myTests = testList ..."; Produces = ["myTests"]; Consumes = [] } ]
        Edges = []
      }
      // FullName contains "myTests" as a substring
      let tests = [ makeTest "MyModule.myTests/should pass" (TestOrigin.SourceMapped ("/src/MyModule.fs", 15)) ]
      let result = resolveTestLocations graph tests
      (List.head result).CellId |> Expect.equal "CellId should match cell that produces 'myTests'" 42
    }

    test "resolveTestLocations does not infer CellId from DisplayName text alone" {
      let graph = {
        Cells = Map.ofList [ 17, { Id = 17; Source = "let validationTests = testList ..."; Produces = ["validation"]; Consumes = [] } ]
        Edges = []
      }
      let testCase =
        { makeTest "MyModule.tests/should reject invalid input" (TestOrigin.SourceMapped ("/src/MyModule.fs", 27)) with
            DisplayName = "validation should reject invalid input" }
      let result = resolveTestLocations graph [ testCase ]
      (List.head result).CellId |> Expect.equal "DisplayName text should not drive CellId resolution" -1
    }

  ]

