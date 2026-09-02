module SageFs.Tests.FeatureParityTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.FeatureParity

[<Tests>]
let featureParityTests = testList "FeatureParity" [

  test "matrix covers all features for all editors" {
    let expectedCount = Feature.all.Length * Editor.all.Length
    FeatureParity.matrix.Length
    |> Expect.equal "should have entry for every feature×editor combination" expectedCount
  }

  test "no duplicate entries in matrix" {
    let keys =
      FeatureParity.matrix
      |> List.map (fun e -> e.feature.name, e.editor)
    let distinct = keys |> List.distinct
    distinct.Length
    |> Expect.equal "should have no duplicates" keys.Length
  }

  test "every feature name is unique" {
    let names = Feature.all |> List.map (fun f -> f.name)
    let distinct = names |> List.distinct
    distinct.Length
    |> Expect.equal "feature names should be unique" names.Length
  }

  test "feature names use kebab-case" {
    Feature.all
    |> List.iter (fun f ->
      let isKebab = System.Text.RegularExpressions.Regex.IsMatch(f.name, "^[a-z][a-z0-9-]*$")
      isKebab |> Expect.isTrue (sprintf "feature name '%s' should be kebab-case" f.name))
  }

  test "every feature has a non-empty category" {
    Feature.all
    |> List.iter (fun f ->
      f.category.Length > 0
      |> Expect.isTrue (sprintf "feature '%s' should have a category" f.name))
  }

  test "every feature has a non-empty description" {
    Feature.all
    |> List.iter (fun f ->
      f.description.Length > 0
      |> Expect.isTrue (sprintf "feature '%s' should have a description" f.name))
  }

  test "WHY — Editor.all contains exactly 4 maintained clients because deprecated renderers must not shape current parity work" {
    Editor.all.Length
    |> Expect.equal "should have 4 maintained clients" 4
  }

  test "Editor.label returns non-empty for all editors" {
    Editor.all
    |> List.iter (fun ed ->
      (Editor.label ed).Length > 0
      |> Expect.isTrue (sprintf "editor %A should have a label" ed))
  }

  test "forEditor returns correct count" {
    let vsCodeEntries = FeatureParity.forEditor VsCode
    vsCodeEntries.Length
    |> Expect.equal "VS Code should have one entry per feature" Feature.all.Length
  }

  test "forFeature returns correct count" {
    let evalEntries = FeatureParity.forFeature Feature.evalCode
    evalEntries.Length
    |> Expect.equal "eval-code should have one entry per editor" Editor.all.Length
  }

  test "gaps finds features supported in VS Code but not Visual Studio" {
    let vsGaps = FeatureParity.gaps VsCode VisualStudio
    vsGaps.Length > 0
    |> Expect.isTrue "should have at least one gap"
    vsGaps
    |> List.iter (fun e ->
      match e.status with
      | NotSupported | Partial _ -> ()
      | _ -> failwith (sprintf "gap entry '%s' should be NotSupported or Partial" e.feature.name))
  }

  test "partialFeatures returns entries with reasons" {
    let partials = FeatureParity.partialFeatures ()
    partials.Length > 0
    |> Expect.isTrue "should have at least one partial feature"
    partials
    |> List.iter (fun (_, _, reason) ->
      reason.Length > 0
      |> Expect.isTrue "partial reason should be non-empty")
  }

  test "summary covers all editors" {
    let sum = FeatureParity.summary ()
    sum.Length
    |> Expect.equal "should have one summary per editor" Editor.all.Length
  }

  test "summary counts add up to total features per editor" {
    let total = Feature.all.Length
    FeatureParity.summary ()
    |> List.iter (fun (label, s, p, n, na) ->
      s + p + n + na
      |> Expect.equal (sprintf "%s counts should sum to %d" label total) total)
  }

  testProperty "forEditor returns only entries for that editor" <| fun () ->
    Editor.all
    |> List.forall (fun ed ->
      FeatureParity.forEditor ed
      |> List.forall (fun e -> e.editor = ed))

  testProperty "forFeature returns only entries for that feature" <| fun () ->
    Feature.all
    |> List.forall (fun f ->
      FeatureParity.forFeature f
      |> List.forall (fun e -> e.feature = f))
]
