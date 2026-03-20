module SageFs.Tests.ExactTestSelectionScenarioTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features.Verification

let private mkTestCase fullName displayName category =
  { Id = TestId.TestId fullName
    FullName = fullName
    DisplayName = displayName
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = category }

let private exact text =
  match ExactTestRef.create text with
  | Ok value -> value
  | Error err -> failtestf "expected exact test ref, got error: %s" err

let private sampleTests = [|
  mkTestCase "Tests.UserPreferences.loadFromFile returns error for missing directory" "loadFromFile missing dir" TestCategory.Unit
  mkTestCase "Tests.UserPreferences.loadFromFile returns success for existing directory" "loadFromFile existing dir" TestCategory.Unit
  mkTestCase "Tests.UserPreferences.loadFromFile integration preserves existing file" "integration existing file" TestCategory.Integration
|]

[<Tests>]
let tests =
  testList "Exact test selection scenarios" [
    testCase "when the user names one regression guard, SageFs runs exactly that guard and nothing adjacent" <| fun _ ->
      let selection =
        TestSelection.parse (Some "exact:Tests.UserPreferences.loadFromFile returns error for missing directory")
        |> Result.defaultWith failtest
      let resolution = TestSelection.resolve sampleTests selection None
      match resolution with
      | TestSelectionResolution.ExactMatch test ->
        test.FullName
        |> Expect.equal
          "the exact guard should be selected"
          "Tests.UserPreferences.loadFromFile returns error for missing directory"
      | other ->
        failtestf "expected exact match, got %A" other

    testCase "when the exact test name does not exist, SageFs fails clearly instead of silently broadening scope" <| fun _ ->
      let selection =
        TestSelection.parse (Some "exact:Tests.UserPreferences.unknown guard")
        |> Result.defaultWith failtest
      let resolution = TestSelection.resolve sampleTests selection None
      match resolution with
      | TestSelectionResolution.NoExactMatch missing ->
        ExactTestRef.value missing
        |> Expect.equal "the missing exact test should be preserved" "Tests.UserPreferences.unknown guard"
      | other ->
        failtestf "expected exact miss, got %A" other

    testCase "when only fuzzy selection is requested, SageFs admits the confidence downgrade explicitly" <| fun _ ->
      let selection =
        TestSelection.parse (Some "loadFromFile")
        |> Result.defaultWith failtest
      let resolution = TestSelection.resolve sampleTests selection None
      match resolution with
      | TestSelectionResolution.FuzzyMatches tests ->
        tests.Length |> Expect.equal "fuzzy selection should keep both neighboring tests" 3
      | other ->
        failtestf "expected fuzzy matches, got %A" other

    testCase "exact test selection still respects category boundaries" <| fun _ ->
      let selection =
        TestSelection.parse (Some "exact:Tests.UserPreferences.loadFromFile integration preserves existing file")
        |> Result.defaultWith failtest
      let unitResolution = TestSelection.resolve sampleTests selection (Some TestCategory.Unit)
      unitResolution
      |> Expect.equal
        "exact selection should not leak across categories"
        (TestSelectionResolution.NoExactMatch (exact "Tests.UserPreferences.loadFromFile integration preserves existing file"))
  ]
