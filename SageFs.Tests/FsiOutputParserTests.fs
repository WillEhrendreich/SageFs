module SageFs.Tests.FsiOutputParserTests

open Expecto
open Expecto.Flip
open System.Text.Json
open SageFs.Features.FsiOutputParser

// ── parseFsiVal ───────────────────────────────────────────────────────────────

let parseFsiValTests =
  testList "parseFsiVal" [

    test "parses simple integer binding" {
      let result = parseFsiVal "val x : int = 43"
      result |> Expect.isSome "should parse"
      result.Value.Name         |> Expect.equal "name"  "x"
      result.Value.TypeSig      |> Expect.equal "type"  "int"
      result.Value.DisplayValue |> Expect.equal "value" "43"
      result.Value.IsTruncated     |> Expect.isFalse "not truncated"
      result.Value.IsFunctionValue |> Expect.isFalse "not a function"
    }

    test "parses mutable binding" {
      let result = parseFsiVal "val mutable counter : int = 0"
      result |> Expect.isSome "should parse"
      result.Value.Name |> Expect.equal "name" "counter"
      result.Value.TypeSig |> Expect.equal "type" "int"
    }

    test "parses string binding with spaces in value" {
      let result = parseFsiVal """val greeting : string = "hello world" """
      result |> Expect.isSome "should parse"
      result.Value.DisplayValue |> Expect.equal "string value" "\"hello world\""
    }

    test "parses truncated list — IsTruncated is true" {
      let result = parseFsiVal "val xs : int list = [1; 2; 3; 4; 5; ...]"
      result |> Expect.isSome "should parse"
      result.Value.IsTruncated     |> Expect.isTrue  "truncated"
      result.Value.DisplayValue    |> Expect.equal "display" "[1; 2; 3; 4; 5; ...]"
      result.Value.IsFunctionValue |> Expect.isFalse "not a function"
    }

    test "parses function value — IsFunctionValue is true" {
      let result = parseFsiVal "val f : int -> string = <fun>"
      result |> Expect.isSome "should parse"
      result.Value.IsFunctionValue |> Expect.isTrue "is a function"
      result.Value.TypeSig         |> Expect.equal "type sig" "int -> string"
      result.Value.DisplayValue    |> Expect.equal "display"  "<fun>"
    }

    test "parses map with = sign inside value" {
      let result = parseFsiVal """val m : Map<string,int> = map [("a", 1); ("b=2", 3)]"""
      result |> Expect.isSome "map should parse"
      result.Value.Name    |> Expect.equal "name"  "m"
      result.Value.TypeSig |> Expect.equal "type"  "Map<string,int>"
      result.Value.DisplayValue |> Expect.stringContains "value starts with map" "map ["
    }

    test "parses nested generic type with multiple = in value" {
      let result = parseFsiVal "val m2 : Map<string, Map<int,int>> = map [(\"k\", map [])]"
      result |> Expect.isSome "nested generic should parse"
      result.Value.TypeSig |> Expect.equal "type" "Map<string, Map<int,int>>"
    }

    test "parses unit return — value is ()" {
      let result = parseFsiVal "val it : unit = ()"
      result |> Expect.isSome "unit should parse"
      result.Value.DisplayValue |> Expect.equal "value" "()"
    }

    test "returns None for bare expression result '- : T = V'" {
      // FSI uses '- : ...' for do-expressions, not 'val'
      parseFsiVal "- : unit = ()"  |> Expect.isNone "bare expression"
    }

    test "returns None for type definition line" {
      parseFsiVal "type Color = Red | Green | Blue" |> Expect.isNone "type def"
    }

    test "returns None for empty line" {
      parseFsiVal "" |> Expect.isNone "empty"
    }

    test "returns None for warning line" {
      parseFsiVal "warning FS0044: deprecated" |> Expect.isNone "warning"
    }

    test "returns None for error line" {
      parseFsiVal "/path/file.fs(1,1): error FS0001" |> Expect.isNone "error"
    }

    test "parses binding with arrow function type containing ->" {
      let result = parseFsiVal "val g : (int -> bool) -> int list = <fun>"
      result |> Expect.isSome "higher order should parse"
      result.Value.IsFunctionValue |> Expect.isTrue "is a function"
    }
  ]

// ── parseFsiBatch ─────────────────────────────────────────────────────────────

let parseFsiBatchTests =
  testList "parseFsiBatch" [

    test "extracts multiple bindings from multiline FSI output" {
      let output = "val x : int = 42\nval y : string = \"hello\"\nsome other output line\nval f : unit -> int = <fun>\n"
      let results = parseFsiBatch output
      results |> List.length |> Expect.equal "three bindings" 3
      results.[0].Name             |> Expect.equal "first is x" "x"
      results.[2].IsFunctionValue  |> Expect.isTrue "third is fun"
    }

    test "is safe on empty input" {
      parseFsiBatch ""           |> Expect.isEmpty "empty"
      parseFsiBatch "   \n\n   " |> Expect.isEmpty "whitespace"
    }

    test "ignores non-val lines" {
      let output = "Build succeeded.\nval answer : int = 42\nwarning FS0001: foo"
      let results = parseFsiBatch output
      results |> List.length |> Expect.equal "one binding" 1
      results.[0].Name |> Expect.equal "name" "answer"
    }

    test "handles FSI output with duplicate val names (shadowing)" {
      let output = "val x : int = 1\nval x : string = \"overridden\""
      let results = parseFsiBatch output
      results |> List.length |> Expect.equal "both parsed" 2
    }

    test "parseFsiBatch assigns 1-based SourceLine to each binding" {
      let output = "val x : int = 42\nval y : string = \"hello\""
      let results = parseFsiBatch output
      results |> List.length |> Expect.equal "two bindings" 2
      results.[0].SourceLine |> Expect.equal "first binding is line 1" 1
      results.[1].SourceLine |> Expect.equal "second binding is line 2" 2
    }

    test "parseFsiBatch SourceLine skips non-val lines correctly" {
      let output = "Build succeeded.\nval answer : int = 42\nwarning FS0001: foo"
      let results = parseFsiBatch output
      results |> List.length |> Expect.equal "one binding" 1
      // "val answer" is on line index 1 (0-based) → SourceLine = 2
      results.[0].SourceLine |> Expect.equal "second line of output" 2
    }

    test "parseFsiBatch produces SourceLine=0 via parseFsiVal for single-line calls" {
      let bv = parseFsiVal "val x : int = 42"
      bv |> Expect.isSome "should parse"
      bv.Value.SourceLine |> Expect.equal "SourceLine is 0 for line-agnostic parseFsiVal" 0
    }
  ]

// ── BindingValue formatting ───────────────────────────────────────────────────

let ghostTextTests =
  testList "BindingValue.toGhostText" [

    test "int value shows arrow and type" {
      let bv : BindingValue = {
        Name = "x"; TypeSig = "int"; DisplayValue = "42"
        IsTruncated = false; IsFunctionValue = false
        CellIndex = 0; EvalDurationMs = 1.0; SourceLine = 0
      }
      let text = BindingValue.toGhostText bv
      text |> Expect.stringContains "has arrow and value" "→ 42"
      text |> Expect.stringContains "has type" "int"
    }

    test "function value shows user-friendly <fn> not raw <fun>" {
      let bv : BindingValue = {
        Name = "f"; TypeSig = "int -> string"; DisplayValue = "<fun>"
        IsTruncated = false; IsFunctionValue = true
        CellIndex = 0; EvalDurationMs = 0.5; SourceLine = 0
      }
      let text = BindingValue.toGhostText bv
      text |> Expect.stringContains "user-friendly fn" "→ <fn>"
      text.Contains("<fun>") |> Expect.isFalse "no raw FSI fun text"
    }

    test "truncated value shows unicode ellipsis indicator" {
      let bv : BindingValue = {
        Name = "xs"; TypeSig = "int list"; DisplayValue = "[1; 2; ...]"
        IsTruncated = true; IsFunctionValue = false
        CellIndex = 0; EvalDurationMs = 2.0; SourceLine = 0
      }
      let text = BindingValue.toGhostText bv
      text |> Expect.stringContains "unicode ellipsis" "…"
    }

    test "truncated value ghost text includes ⟨truncated⟩ suffix" {
      let bv : BindingValue = {
        Name = "xs"; TypeSig = "int list"; DisplayValue = "[1; 2; ...]"
        IsTruncated = true; IsFunctionValue = false
        CellIndex = 0; EvalDurationMs = 2.0; SourceLine = 0
      }
      let text = BindingValue.toGhostText bv
      text |> Expect.stringContains "truncated suffix" "⟨truncated⟩"
    }

    test "null interop value renders null not <null>" {
      let bv : BindingValue = {
        Name = "obj"; TypeSig = "SomeType"; DisplayValue = "<null>"
        IsTruncated = false; IsFunctionValue = false
        CellIndex = 0; EvalDurationMs = 0.0; SourceLine = 0
      }
      let text = BindingValue.toGhostText bv
      text |> Expect.stringContains "null shown" "null"
      text.Contains("<null>") |> Expect.isFalse "no angle-bracket null in ghost text"
    }

    test "unit value shows neutral indicator" {
      let bv : BindingValue = {
        Name = "it"; TypeSig = "unit"; DisplayValue = "()"
        IsTruncated = false; IsFunctionValue = false
        CellIndex = 0; EvalDurationMs = 0.1; SourceLine = 0
      }
      let text = BindingValue.toGhostText bv
      text |> Expect.stringContains "unit shown" "()"
    }
  ]

// ── BindingValue JSON contract (Seemann's parity requirement) ─────────────────

let bindingValueContractTests =
  testList "BindingValue.jsonContract" [

    test "BindingValue round-trips through JSON serialization without field loss" {
      let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
      let bv : BindingValue = {
        Name = "answer"; TypeSig = "int"; DisplayValue = "42"
        IsTruncated = false; IsFunctionValue = false
        CellIndex = 3; EvalDurationMs = 12.5; SourceLine = 0
      }
      let json = JsonSerializer.Serialize(bv, opts)
      let rt = JsonSerializer.Deserialize<BindingValue>(json, opts)
      rt.Name          |> Expect.equal "Name"          bv.Name
      rt.TypeSig       |> Expect.equal "TypeSig"       bv.TypeSig
      rt.DisplayValue  |> Expect.equal "DisplayValue"  bv.DisplayValue
      rt.IsTruncated   |> Expect.equal "IsTruncated"   bv.IsTruncated
      rt.IsFunctionValue |> Expect.equal "IsFunctionValue" bv.IsFunctionValue
      rt.CellIndex     |> Expect.equal "CellIndex"     bv.CellIndex
      rt.EvalDurationMs |> Expect.equal "EvalDurationMs" bv.EvalDurationMs
      rt.SourceLine    |> Expect.equal "SourceLine"    bv.SourceLine
    }

    test "bindings_snapshot payload JSON contains BindingValues array key" {
      let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
      let bv : BindingValue = {
        Name = "x"; TypeSig = "int"; DisplayValue = "1"
        IsTruncated = false; IsFunctionValue = false
        CellIndex = 0; EvalDurationMs = 0.0; SourceLine = 0
      }
      let payload = {| BindingValues = [ bv ] |}
      let json = JsonSerializer.Serialize(payload, opts)
      json |> Expect.stringContains "has BindingValues key" "bindingValues"
      json |> Expect.stringContains "value in payload" "\"x\""
    }
  ]

// ── EvalBoundaryKind ──────────────────────────────────────────────────────────

let evalBoundaryKindTests =
  testList "EvalBoundaryKind" [

    testList "detectBoundaryKind from submitted code" [

      test "let binding detected" {
        let kind = detectBoundaryKind "let answer = 42"
        kind |> Expect.equal "let binding" (EvalBoundaryKind.LetBinding "answer")
      }

      test "let rec binding detected" {
        match detectBoundaryKind "let rec fib n = if n < 2 then n else fib (n-1) + fib (n-2)" with
        | EvalBoundaryKind.LetBinding name -> name |> Expect.equal "name is fib" "fib"
        | other -> failtest (sprintf "Expected LetBinding, got %A" other)
      }

      test "let with type annotation detected" {
        match detectBoundaryKind "let (x: int) = 5" with
        | EvalBoundaryKind.LetBinding _ -> ()
        | other -> failtest (sprintf "Expected LetBinding, got %A" other)
      }

      test "mutable binding detected as LetBinding" {
        match detectBoundaryKind "let mutable count = 0" with
        | EvalBoundaryKind.LetBinding name -> name |> Expect.equal "name" "count"
        | other -> failtest (sprintf "Expected LetBinding, got %A" other)
      }

      test "type definition detected" {
        match detectBoundaryKind "type Color = Red | Green | Blue" with
        | EvalBoundaryKind.TypeOrModuleDefinition name ->
          name |> Expect.equal "type name" "Color"
        | other -> failtest (sprintf "Expected TypeOrModuleDefinition, got %A" other)
      }

      test "module definition detected" {
        match detectBoundaryKind "module MyHelpers =\n  let x = 1" with
        | EvalBoundaryKind.TypeOrModuleDefinition name ->
          name |> Expect.equal "module name" "MyHelpers"
        | other -> failtest (sprintf "Expected TypeOrModuleDefinition, got %A" other)
      }

      test "bare expression is DoExpression" {
        detectBoundaryKind "printfn \"hello\""
        |> Expect.equal "bare expression" EvalBoundaryKind.DoExpression
      }

      test "open statement detected" {
        match detectBoundaryKind "open System.Collections.Generic" with
        | EvalBoundaryKind.OpenStatement ns ->
          ns |> Expect.equal "namespace" "System.Collections.Generic"
        | other -> failtest (sprintf "Expected OpenStatement, got %A" other)
      }

      test "hash directive detected" {
        match detectBoundaryKind "#r \"nuget: Newtonsoft.Json\"" with
        | EvalBoundaryKind.HashDirective d ->
          d |> Expect.stringContains "directive content" "nuget"
        | other -> failtest (sprintf "Expected HashDirective, got %A" other)
      }

      test "do expression with do keyword is DoExpression" {
        detectBoundaryKind "do printfn \"hello\""
        |> Expect.equal "do keyword" EvalBoundaryKind.DoExpression
      }

      test "empty code is Unknown" {
        detectBoundaryKind "" |> Expect.equal "empty" EvalBoundaryKind.Unknown
        detectBoundaryKind "   " |> Expect.equal "whitespace" EvalBoundaryKind.Unknown
      }
    ]

    testList "EvalBoundaryKind.toLabel" [

      test "LetBinding gives let name as label" {
        EvalBoundaryKind.toLabel (EvalBoundaryKind.LetBinding "counter")
        |> Expect.equal "let label" "let counter"
      }

      test "TypeOrModuleDefinition gives type name" {
        EvalBoundaryKind.toLabel (EvalBoundaryKind.TypeOrModuleDefinition "Color")
        |> Expect.equal "type label" "type Color"
      }

      test "DoExpression gives generic label" {
        EvalBoundaryKind.toLabel EvalBoundaryKind.DoExpression
        |> Expect.stringContains "do expression label" "expr"
      }

      test "WholeFile gives filename not full path" {
        let label = EvalBoundaryKind.toLabel (EvalBoundaryKind.WholeFile "/very/long/path/to/script.fsx")
        label |> Expect.equal "just filename" "script.fsx"
        label.Contains("/") |> Expect.isFalse "no path separators in label"
      }

      test "OpenStatement gives namespace label" {
        EvalBoundaryKind.toLabel (EvalBoundaryKind.OpenStatement "System.IO")
        |> Expect.stringContains "open label" "System.IO"
      }
    ]
  ]

// ── Filmstrip label integration ───────────────────────────────────────────────

let filmstripLabelTests =
  testList "filmstrip label (detectBoundaryKind → toLabel)" [

    test "let binding code produces let-name label" {
      let code = "let answer = 42"
      detectBoundaryKind code
      |> EvalBoundaryKind.toLabel
      |> Expect.equal "filmstrip label for let binding" "let answer"
    }

    test "type definition code produces type-name label" {
      let code = "type Color = Red | Green | Blue"
      detectBoundaryKind code
      |> EvalBoundaryKind.toLabel
      |> Expect.equal "filmstrip label for type def" "type Color"
    }

    test "module definition code produces module-name label" {
      let code = "module Utils ="
      detectBoundaryKind code
      |> EvalBoundaryKind.toLabel
      |> Expect.stringContains "filmstrip label for module def" "Utils"
    }

    test "open statement code produces namespace label" {
      let code = "open System.IO"
      detectBoundaryKind code
      |> EvalBoundaryKind.toLabel
      |> Expect.stringContains "filmstrip label for open" "System.IO"
    }

    test "do expression code produces do-expr label" {
      let code = "printfn \"hello\""
      detectBoundaryKind code
      |> EvalBoundaryKind.toLabel
      |> Expect.stringContains "filmstrip label for do expr" "expr"
    }

    test "hash directive code produces directive label without leading #" {
      let code = "#r nuget:FSharp.Data"
      let label = detectBoundaryKind code |> EvalBoundaryKind.toLabel
      label |> Expect.stringContains "filmstrip label for hash directive" "nuget"
    }

    test "multiline let binding uses first name in label" {
      let code = "let greet name =\n  sprintf \"Hello %s\" name"
      detectBoundaryKind code
      |> EvalBoundaryKind.toLabel
      |> Expect.equal "filmstrip label for multiline let" "let greet"
    }

    test "unknown code falls back to question mark" {
      detectBoundaryKind ""
      |> EvalBoundaryKind.toLabel
      |> Expect.equal "filmstrip label for unknown" "?"
    }

  ]

[<Tests>]
let allFsiParserTests =
  testList "FsiOutputParser" [
    parseFsiValTests
    parseFsiBatchTests
    ghostTextTests
    bindingValueContractTests
    evalBoundaryKindTests
    filmstripLabelTests
  ]
