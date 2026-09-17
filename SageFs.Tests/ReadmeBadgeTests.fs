/// Fails when the README test-count badge or property count drifts from the
/// live source, so the numbers can never silently go stale. Fix by running:
///   dotnet run --project SageFs.Tests -- --update-badge
module SageFs.Tests.ReadmeBadgeTests

open Expecto
open Expecto.Flip

[<Tests>]
let readmeBadgeTests =
  testList "README test-count badge stays fresh" [
    testCase "tests badge equals the live total test count" <| fun _ ->
      let text = System.IO.File.ReadAllText TestCountBadge.readmePath
      let expected = TestCountBadge.totalTestCount ()
      TestCountBadge.parseBadge text
      |> Expect.equal
        (sprintf "README tests badge must equal the live count %d — run: dotnet run --project SageFs.Tests -- --update-badge" expected)
        (Some expected)

    testCase "property-based test count in prose matches the source" <| fun _ ->
      let text = System.IO.File.ReadAllText TestCountBadge.readmePath
      let expected = TestCountBadge.propertyTestCount ()
      TestCountBadge.parseProperty text
      |> Expect.equal
        (sprintf "README property-based test count must equal %d — run: dotnet run --project SageFs.Tests -- --update-badge" expected)
        (Some expected)
  ]
