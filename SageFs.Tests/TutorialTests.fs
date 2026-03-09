module SageFs.Tests.TutorialTests

open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let tutorialTests =
  let tutorialPath =
    System.IO.Path.Combine(
      __SOURCE_DIRECTORY__, "..", "samples", "getting-started.fsx")

  testList "Tutorial" [
    testList "getting-started.fsx structure" [
      test "file exists at samples/getting-started.fsx" {
        System.IO.File.Exists tutorialPath
        |> Expect.isTrue "tutorial file should exist"
      }

      test "has 10 numbered sections" {
        let content = System.IO.File.ReadAllText tutorialPath
        let sectionCount =
          System.Text.RegularExpressions.Regex.Matches(
            content, @"// ── \d+\.")
          |> Seq.length
        sectionCount
        |> Expect.equal "should have 10 sections" 10
      }

      test "sections are numbered sequentially 1 through 10" {
        let content = System.IO.File.ReadAllText tutorialPath
        for i in 1..10 do
          content
          |> Expect.stringContains $"should contain section {i}" $"// ── {i}."
      }

      test "first section is instant feedback" {
        let content = System.IO.File.ReadAllText tutorialPath
        content
        |> Expect.stringContains "first section" "1. Instant feedback"
      }

      test "mentions Alt+Enter in header" {
        let content = System.IO.File.ReadAllText tutorialPath
        content
        |> Expect.stringContains "should mention Alt+Enter" "Alt+Enter"
      }

      test "includes pipeline operator section" {
        let content = System.IO.File.ReadAllText tutorialPath
        content
        |> Expect.stringContains "should cover pipelines" "|>"
      }

      test "includes discriminated union section" {
        let content = System.IO.File.ReadAllText tutorialPath
        content
        |> Expect.stringContains "should cover DUs" "Discriminated union"
      }

      test "includes testing section" {
        let content = System.IO.File.ReadAllText tutorialPath
        content
        |> Expect.stringContains "should cover Expecto" "Expecto"
      }

      test "includes hot reload section" {
        let content = System.IO.File.ReadAllText tutorialPath
        content
        |> Expect.stringContains "should cover hot reload" "Hot reload"
      }

      test "mentions koans as next step" {
        let content = System.IO.File.ReadAllText tutorialPath
        content
        |> Expect.stringContains "should point to koans" "from-koans"
      }

      test "mentions all language bridges" {
        let content = System.IO.File.ReadAllText tutorialPath
        let bridges = ["from-csharp"; "from-python"; "from-javascript"; "from-java"; "from-rust"; "from-jupyter"]
        for bridge in bridges do
          content
          |> Expect.stringContains $"should mention {bridge}" bridge
      }

      test "starts with 1 + 1 as immediate success" {
        let lines = System.IO.File.ReadAllLines tutorialPath
        let firstCodeLine =
          lines
          |> Array.tryFind (fun l ->
            let trimmed = l.Trim()
            trimmed.Length > 0
            && not (trimmed.StartsWith "//")
            && not (trimmed.StartsWith "#"))
        firstCodeLine
        |> Expect.isSome "should have a code line"
        firstCodeLine.Value.Trim()
        |> Expect.stringContains "first code should be 1+1" "1 + 1"
      }
    ]

    testList "Tutorial module" [
      test "fileName is getting-started.fsx" {
        Tutorial.fileName
        |> Expect.equal "should be getting-started.fsx" "getting-started.fsx"
      }

      test "sections has 10 entries" {
        Tutorial.sections
        |> List.length
        |> Expect.equal "should define 10 sections" 10
      }

      test "sections are numbered 1 through 10" {
        Tutorial.sections
        |> List.mapi (fun i (num, _) -> (i + 1, num))
        |> List.iter (fun (expected, actual) ->
          actual |> Expect.equal $"section {expected}" expected)
      }

      test "resolvePath finds file relative to samples dir" {
        let samplesDir =
          System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "samples")
          |> System.IO.Path.GetFullPath
        match Tutorial.resolvePath samplesDir with
        | Some path ->
          System.IO.File.Exists path
          |> Expect.isTrue "resolved path should exist"
        | None ->
          failtest "should resolve tutorial path"
      }

      test "resolvePath returns None for nonexistent directory" {
        Tutorial.resolvePath "/nonexistent/dir/that/does/not/exist"
        |> Expect.isNone "should return None for missing dir"
      }
    ]
  ]
