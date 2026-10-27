/// Step 7 of docs/plans/markers-on-expecto-cases.md: what the edge does with the
/// two inputs TestLocationsDetected now carries — the tree-sitter locations it
/// always carried, and the cases from ExpectoCases.discover — folded into a
/// session's live-testing state.
module SageFs.Tests.TestLocationsAppliedTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let lit (name: string) : SourceName = SourceName.Literal name

let literalCase (file: string) (path: string list) (line: int) (endLine: int) (body: string) : SourceCase =
  { Kind = SourceKind.Case
    FilePath = file
    Path = path |> List.map lit
    Line = line
    Column = 1
    EndLine = endLine
    Body = body }

let reflectedTest (fullName: string) (path: string list) : TestCase =
  { Id = TestId.create fullName TestFramework.Expecto
    FullName = fullName
    DisplayName = List.last path
    NamePath = path
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

[<Tests>]
let tests =
  testList "TestLocationsApplied" [
    test "a literal source case lands the mark on its line and its body decides the category" {
      let file = "Suite/TestList.fs"
      let cases =
        [| literalCase file [ "a"; "x" ] 4 9
             "let connection = new SqliteConnection(\"Data Source=:memory:\")" |]
      let detected = [| reflectedTest "Mod.allTests/a/x" [ "a"; "x" ] |]
      let locations, placed = TestLocationsApplied.apply [||] cases detected
      locations |> Expect.equal "the locations pass through unchanged" [||]
      placed.[0].Origin
      |> Expect.equal "the mark sits on the case's own line"
        (TestOrigin.SourceMapped (file, 4))
      placed.[0].Category
      |> Expect.equal "the source body is what makes it an integration test"
        TestCategory.Integration
    }

    test "without cases the state is what the merge left it, category included" {
      let kept = { reflectedTest "Mod.allTests" [ "a" ] with Category = TestCategory.Browser }
      let _, placed = TestLocationsApplied.apply [||] [||] [| kept |]
      placed |> Expect.equal "an unplaced test keeps its origin and its category" [| kept |]
    }

    test "a reflected leaf no case matches keeps its reflection origin" {
      let file = "Suite/TestList.fs"
      let cases = [| literalCase file [ "a"; "other" ] 4 9 "()" |]
      let kept = { reflectedTest "Mod.allTests/a/ghost" [ "a"; "ghost" ] with Category = TestCategory.Property }
      let _, placed = TestLocationsApplied.apply [||] cases [| kept |]
      placed |> Expect.equal "no matching case means no source opinion" [| kept |]
    }

    test "a computed name lands the mark on its nearest named ancestor, and the body still decides" {
      let file = "Suite/TestList.fs"
      let cases =
        [| { Kind = SourceKind.List
             FilePath = file
             Path = [ lit "a" ]
             Line = 2
             Column = 1
             EndLine = 12
             Body = "" }
           { Kind = SourceKind.Case
             FilePath = file
             Path = [ lit "a"; SourceName.Computed ]
             Line = 7
             Column = 3
             EndLine = 11
             Body = "use System.Net.Http.HttpClient" } |]
      let detected =
        [| { reflectedTest "Mod.allTests/a/renamed" [ "a"; "renamed" ] with Category = TestCategory.Architecture } |]
      let _, placed = TestLocationsApplied.apply [||] cases detected
      placed.[0].Origin
      |> Expect.equal "the fallback mark sits on the enclosing list's line, the nearest named ancestor"
        (TestOrigin.SourceMapped (file, 2))
      placed.[0].Category
      |> Expect.equal "the source body is read even on the fallback path"
        TestCategory.Integration
    }
  ]
