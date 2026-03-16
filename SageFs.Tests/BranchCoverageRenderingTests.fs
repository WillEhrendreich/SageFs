module SageFs.Tests.BranchCoverageRenderingTests

/// RED tests for Stream 5: Branch Coverage Gutters for All Editors.
/// These tests define the expected API surface for branch-level coverage
/// rendering across TUI, VS Code, Visual Studio, and Raylib GUI.
/// All tests should FAIL until the implementation is complete.

open Expecto
open Expecto.Flip
open System.Text.Json
open Microsoft.FSharp.Reflection
open SageFs.Features.LiveTesting
open SageFs.McpTools

// ── Helpers ─────────────────────────────────────────────────────

/// Try to find a type by simple name in the SageFs.Core assembly.
let private tryFindType (name: string) =
  typeof<CoverageAnnotation>.Assembly.GetTypes()
  |> Array.tryFind (fun t -> t.Name = name)

let private mkCovAnn line endLine endCol detail testIds branchCov : CoverageLineAnnotation =
  { Line = line; EndLine = endLine; EndColumn = endCol
    Detail = detail; CoveringTestIds = testIds; BranchCoverage = branchCov }

let private mkFileAnns path covAnns : FileAnnotations =
  { FilePath = path; TestAnnotations = [||]; CoverageAnnotations = covAnns
    InlineFailures = [||]; CodeLenses = [||]; PerformanceAnnotations = [||] }

// ── Tests ───────────────────────────────────────────────────────

[<Tests>]
let tests =
  testList "Stream 5 — Branch Coverage Gutters" [

    test "CoverageAnnotation has BranchCoverage field" {
      // CoverageAnnotation is the symbol-level type pushed via SSE to all editors.
      // It currently has Symbol, FilePath, DefinitionLine, Status — but no branch data.
      // Adding BranchCoverage enables all editors to render branch gutters from the
      // same SSE payload without needing a separate MCP call.
      typeof<CoverageAnnotation>.GetProperty("BranchCoverage")
      |> box
      |> Expect.isNotNull "CoverageAnnotation should have a BranchCoverage property"
    }

    test "BranchCoverage DU exists with FullyCovered, PartiallyCovered, NotCovered, Unknown cases" {
      // A dedicated BranchCoverage DU (distinct from LineCoverage) is needed because:
      // - LineCoverage lacks an Unknown case for when branch data is unavailable
      // - The visual language requires four states: green/yellow/red/grey
      // - Neovim already uses this four-state model (annotations.lua:179-236)
      let branchType = tryFindType "BranchCoverage"
      branchType
      |> Expect.isSome "BranchCoverage type should exist in the LiveTesting assembly"
      let cases =
        FSharpType.GetUnionCases branchType.Value
        |> Array.map (fun c -> c.Name)
        |> Set.ofArray
      let expected = Set.ofList [ "FullyCovered"; "PartiallyCovered"; "NotCovered"; "Unknown" ]
      cases
      |> Expect.equal "should have exactly four DU cases" expected
    }

    test "PartiallyCovered carries covered and total branch counts" {
      // "2/3 branches covered" tells users exactly what to test next.
      // The Neovim plugin already formats this as EOL virtual text (annotations.lua:228).
      // All editors need the same data shape.
      let branchType = tryFindType "BranchCoverage"
      branchType
      |> Expect.isSome "BranchCoverage type must exist for field inspection"
      let partial =
        FSharpType.GetUnionCases branchType.Value
        |> Array.find (fun c -> c.Name = "PartiallyCovered")
      let fields = partial.GetFields()
      fields.Length
      |> Expect.equal "PartiallyCovered should have two fields (covered, total)" 2
      fields.[0].PropertyType
      |> Expect.equal "first field should be int (covered count)" typeof<int>
      fields.[1].PropertyType
      |> Expect.equal "second field should be int (total count)" typeof<int>
    }

    test "formatFileCoverage includes structured branchCoverage in JSON output" {
      // All editors consume the MCP JSON response from get_file_coverage.
      // Currently BranchCoverage is serialized as a flat string ("Partial(3/5)").
      // Editors need a structured object { Case, Covered, Total } so they can
      // pattern-match without parsing strings.
      let ann =
        mkCovAnn 10 10 30
          (CoverageStatus.Covered (1, CoverageHealth.AllPassing))
          [||]
          (Some (LineCoverage.PartiallyCovered (2, 3)))
      let json =
        formatFileCoverageResponse (mkFileAnns "Branch.fs" [| ann |]) LiveTestState.empty
      let doc = JsonDocument.Parse(json)
      let line0 = doc.RootElement.GetProperty("Lines").EnumerateArray() |> Seq.head
      let bc = line0.GetProperty("BranchCoverage")
      // Should be a structured JSON object, not a flat string
      bc.ValueKind
      |> Expect.equal "branchCoverage should be a JSON object, not a string" JsonValueKind.Object
    }

    test "branch coverage overrides line coverage when present" {
      // Branch coverage is strictly more informative than line coverage.
      // When both are present, the gutter must show branch-specific icons.
      // This matches Neovim behavior where parse_branch_coverage is checked
      // first, falling back to Detail only when BranchCoverage is nil.
      let gutterCases =
        FSharpType.GetUnionCases typeof<GutterIcon>
        |> Array.map (fun c -> c.Name)
        |> Set.ofArray
      // GutterIcon must have branch-specific cases so the override is expressible
      [ "BranchFullyCovered"; "BranchPartiallyCovered"; "BranchNotCovered" ]
      |> List.iter (fun name ->
        gutterCases |> Set.contains name
        |> Expect.isTrue (sprintf "GutterIcon should have %s case for branch override" name))
    }

    test "TUI gutter renders distinct symbols for branch coverage states" {
      // Consistent visual language matching the Neovim implementation:
      //   ▐ (U+2590) = fully covered branches (right half block)
      //   ◐ (U+25D0) = partially covered branches (circle left half black)
      //   ▌ (U+258C) = no branch coverage (left half block)
      // The TUI already renders line coverage via GutterIcon.toChar.
      // Branch-specific cases extend the same pattern.
      let cases = FSharpType.GetUnionCases typeof<GutterIcon>
      let fullCase = cases |> Array.tryFind (fun c -> c.Name = "BranchFullyCovered")
      fullCase
      |> Expect.isSome "GutterIcon must have BranchFullyCovered case"
      let fullIcon = FSharpValue.MakeUnion(fullCase.Value, [||]) :?> GutterIcon
      GutterIcon.toChar fullIcon
      |> Expect.equal "fully covered branch symbol should be ▐" '\u2590'
      let noneCase = cases |> Array.tryFind (fun c -> c.Name = "BranchNotCovered")
      noneCase
      |> Expect.isSome "GutterIcon must have BranchNotCovered case"
      let noneIcon = FSharpValue.MakeUnion(noneCase.Value, [||]) :?> GutterIcon
      GutterIcon.toChar noneIcon
      |> Expect.equal "uncovered branch symbol should be ▌" '\u258C'
    }

    test "VS Code decoration contract includes branch coverage states in JSON" {
      // VS Code reads the MCP JSON and maps BranchCoverage to decoration types.
      // The JSON must carry a structured Case field so the extension can dispatch
      // to BranchFull/BranchPartial/BranchNone decoration types without string parsing.
      let ann =
        mkCovAnn 5 5 20
          (CoverageStatus.Covered (1, CoverageHealth.AllPassing))
          [||]
          (Some LineCoverage.FullyCovered)
      let json =
        formatFileCoverageResponse (mkFileAnns "VscBranch.fs" [| ann |]) LiveTestState.empty
      let doc = JsonDocument.Parse(json)
      let line0 = doc.RootElement.GetProperty("Lines").EnumerateArray() |> Seq.head
      let bc = line0.GetProperty("BranchCoverage")
      // Must be a structured object for VS Code decoration dispatch
      bc.ValueKind
      |> Expect.equal "branchCoverage must be a JSON object for VS Code dispatch" JsonValueKind.Object
      bc.GetProperty("Case").GetString()
      |> Expect.equal "Case field should identify the branch state" "FullyCovered"
    }
  ]
