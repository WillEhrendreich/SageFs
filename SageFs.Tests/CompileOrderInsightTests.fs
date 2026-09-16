module SageFs.Tests.CompileOrderInsightTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs

// Build a diagnostic the way MSBuild's parser would (SageFs.BuildDiagnostic).
let private diag file code message : BuildDiagnostic =
  { File = file; Line = Some 1; Column = Some 1
    Severity = BuildDiagnosticSeverity.Error
    Code = code; Message = message }

// A defines→file lookup for the canonical shop scenario.
let private shopDefines =
  function
  | "Product" | "Sku" -> Some "Types.fs"
  | "helper" -> Some "Utils.fs"
  | _ -> None

[<Tests>]
let compileOrderInsightTests =
  testList "CompileOrderInsight" [

    testList "undefinedName" [
      test "extracts value-or-constructor name" {
        CompileOrderInsight.undefinedName "The value or constructor 'computeTotal' is not defined."
        |> Expect.equal "should pull the quoted identifier" (Some "computeTotal")
      }
      test "extracts namespace-or-module name" {
        CompileOrderInsight.undefinedName "The namespace or module 'Shop' is not defined."
        |> Expect.equal "should pull the quoted identifier" (Some "Shop")
      }
      test "returns None for an unrelated message" {
        CompileOrderInsight.undefinedName "This expression was expected to have type 'int'."
        |> Expect.isNone "'is not defined' is absent, so no name"
      }
    ]

    testList "namesDefinedIn" [
      test "finds types, union cases, and module-level lets" {
        let src =
          "namespace Shop\n\n" +
          "type Sku = Sku of string\n\n" +
          "type Product =\n  { Sku: Sku\n    Name: string }\n\n" +
          "module Product =\n  let create s n = { Sku = s; Name = n }"
        let names = CompileOrderInsight.namesDefinedIn src
        names |> Expect.contains "defines the Product type" "Product"
        names |> Expect.contains "defines the Sku type" "Sku"
        names |> Expect.contains "defines the create binding" "create"
      }
    ]

    testList "analyze" [
      test "wrong compile order → precise Reorder suggestion" {
        // Types.fs compiles AFTER Inventory.fs, which uses its types.
        let compileOrder = [ "Inventory.fs"; "Types.fs" ]
        let diags =
          [ diag (Some "Inventory.fs") (Some "FS0039") "The type 'Product' is not defined."
            diag (Some "Inventory.fs") (Some "FS0039") "The value or constructor 'Sku' is not defined." ]
        match CompileOrderInsight.analyze compileOrder shopDefines diags with
        | CompileOrderInsight.Insight.Reorder [ s ] ->
          s.DefiningFile |> Expect.equal "must move Types.fs earlier" "Types.fs"
          s.UsedInFile |> Expect.equal "referenced from Inventory.fs" "Inventory.fs"
          s.Names |> List.sort |> Expect.equal "aggregates both missing names" [ "Product"; "Sku" ]
        | other -> failtestf "expected one Reorder suggestion, got %A" other
      }

      test "correct compile order with a real typo → NoIssue" {
        // Types.fs first; 'Produkt' is a genuine typo, defined nowhere.
        let compileOrder = [ "Types.fs"; "Inventory.fs" ]
        let diags = [ diag (Some "Inventory.fs") (Some "FS0039") "The value or constructor 'Produkt' is not defined." ]
        CompileOrderInsight.analyze compileOrder shopDefines diags
        |> Expect.equal "a typo is not a compile-order problem" CompileOrderInsight.Insight.NoIssue
      }

      test "name defined in an EARLIER file → NoIssue (order is already right)" {
        let compileOrder = [ "Types.fs"; "Inventory.fs" ]
        let diags = [ diag (Some "Inventory.fs") (Some "FS0039") "The type 'Product' is not defined." ]
        CompileOrderInsight.analyze compileOrder shopDefines diags
        |> Expect.equal "Types.fs already compiles first" CompileOrderInsight.Insight.NoIssue
      }

      test "non-FS0039 diagnostics never yield a compile-order insight" {
        let compileOrder = [ "Inventory.fs"; "Types.fs" ]
        let diags = [ diag (Some "Inventory.fs") (Some "FS0001") "This expression was expected to have type 'int'." ]
        CompileOrderInsight.analyze compileOrder shopDefines diags
        |> Expect.equal "type mismatch is not a compile-order problem" CompileOrderInsight.Insight.NoIssue
      }

      test "matches on file name even when the diagnostic path is absolute" {
        let compileOrder = [ "Inventory.fs"; "Types.fs" ]
        let diags = [ diag (Some "/home/dev/shop/Inventory.fs") (Some "FS0039") "The type 'Product' is not defined." ]
        match CompileOrderInsight.analyze compileOrder shopDefines diags with
        | CompileOrderInsight.Insight.Reorder [ s ] ->
          s.UsedInFile |> Expect.equal "absolute path normalized to file name" "Inventory.fs"
        | other -> failtestf "expected a Reorder suggestion, got %A" other
      }
    ]

    testList "describe" [
      test "renders an actionable move-file hint" {
        let insight =
          CompileOrderInsight.Insight.Reorder
            [ { DefiningFile = "Types.fs"; UsedInFile = "Inventory.fs"; Names = [ "Product"; "Sku" ] } ]
        match CompileOrderInsight.describe insight with
        | Some text ->
          text |> Expect.stringContains "names the file to move" "move Types.fs above Inventory.fs"
          text |> Expect.stringContains "explains F#'s ordering rule" "top-to-bottom"
        | None -> failtest "a Reorder insight must describe itself"
      }
      test "NoIssue describes to None" {
        CompileOrderInsight.describe CompileOrderInsight.Insight.NoIssue
        |> Expect.isNone "nothing to say when the order is fine"
      }
    ]

    testList "forProject (IO edge)" [
      test "reads real sources + .fsproj order and yields a Warning advisory diagnostic" {
        let dir = Path.Combine(Path.GetTempPath(), "sagefs-uxfour-" + System.Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
          File.WriteAllText(Path.Combine(dir, "Inventory.fs"), "module Shop.Inventory\nlet stock (p: Product) = p")
          File.WriteAllText(Path.Combine(dir, "Types.fs"), "namespace Shop\ntype Product = { Name: string }")
          let proj = Path.Combine(dir, "Shop.fsproj")
          // WRONG order on purpose: Inventory before Types.
          File.WriteAllText(proj,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" +
            "    <Compile Include=\"Inventory.fs\" />\n    <Compile Include=\"Types.fs\" />\n" +
            "  </ItemGroup>\n</Project>")
          let diags =
            [ diag (Some (Path.Combine(dir, "Inventory.fs"))) (Some "FS0039") "The type 'Product' is not defined." ]
          match CompileOrderInsight.forProject proj diags with
          | Some d ->
            d.Severity |> Expect.equal "hint is an advisory, not an error" BuildDiagnosticSeverity.Warning
            d.File |> Expect.isNone "the advisory has no source location"
            d.Code |> Expect.equal "carries the distinctive SageFs code" (Some "SAGEFS-COMPILE-ORDER")
            d.Message |> Expect.stringContains "advises moving Types.fs" "move Types.fs above Inventory.fs"
          | None -> failtest "a real compile-order failure should produce an advisory"
        finally
          try Directory.Delete(dir, true) with _ -> ()
      }

      test "returns None when the .fsproj has no <Compile> items" {
        let dir = Path.Combine(Path.GetTempPath(), "sagefs-uxfour-" + System.Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
          let proj = Path.Combine(dir, "Empty.fsproj")
          File.WriteAllText(proj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
          CompileOrderInsight.forProject proj []
          |> Expect.isNone "no compile order, no insight"
        finally
          try Directory.Delete(dir, true) with _ -> ()
      }
    ]
  ]
