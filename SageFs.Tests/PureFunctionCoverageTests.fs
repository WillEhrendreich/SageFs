module SageFs.Tests.PureFunctionCoverageTests

open Expecto
open Expecto.Flip
open FSharp.Compiler.EditorServices
open SageFs.Middleware.HotReloading
open SageFs.Features.AutoCompletion
open SageFs.AppState

// ═══════════════════════════════════════════════════════════
// HotReloading — isTopLevelFunctionBinding
// ═══════════════════════════════════════════════════════════

let isTopLevelFunctionBindingTests = testList "isTopLevelFunctionBinding" [
  test "simple function with parameter" {
    isTopLevelFunctionBinding "let f x = x + 1"
    |> Expect.isTrue "let with param is function"
  }
  test "function with multiple params" {
    isTopLevelFunctionBinding "let add x y = x + y"
    |> Expect.isTrue "multiple params is function"
  }
  test "function with unit param" {
    isTopLevelFunctionBinding "let f () = 42"
    |> Expect.isTrue "unit param is function"
  }
  test "function with typed param" {
    isTopLevelFunctionBinding "let f (x: int) = x"
    |> Expect.isTrue "typed param is function"
  }
  test "value binding is not function" {
    isTopLevelFunctionBinding "let x = 42"
    |> Expect.isFalse "simple value is not function"
  }
  test "typed value binding is not function" {
    isTopLevelFunctionBinding "let x : int = 42"
    |> Expect.isFalse "typed value is not function"
  }
  test "indented binding is not top-level" {
    isTopLevelFunctionBinding "  let f x = x"
    |> Expect.isFalse "indented is not top-level"
  }
  test "let! is not a binding" {
    isTopLevelFunctionBinding "let! x = async { return 1 }"
    |> Expect.isFalse "let! is computation expression"
  }
  test "private function" {
    isTopLevelFunctionBinding "let private f x = x"
    |> Expect.isTrue "private function is still function"
  }
  test "inline function" {
    isTopLevelFunctionBinding "let inline f x = x"
    |> Expect.isTrue "inline function is still function"
  }
  test "rec function" {
    isTopLevelFunctionBinding "let rec f x = f x"
    |> Expect.isTrue "recursive function is still function"
  }
  test "no equals sign" {
    isTopLevelFunctionBinding "let f x"
    |> Expect.isFalse "no equals means not a complete binding"
  }
]

// ═══════════════════════════════════════════════════════════
// HotReloading — isStaticMemberFunction
// ═══════════════════════════════════════════════════════════

let isStaticMemberFunctionTests = testList "isStaticMemberFunction" [
  test "static member with parens" {
    isStaticMemberFunction "  static member Create(x) = x"
    |> Expect.isTrue "static member with parens is function"
  }
  test "static member with named param" {
    isStaticMemberFunction "  static member Add x y = x + y"
    |> Expect.isTrue "static member with params is function"
  }
  test "static member property" {
    isStaticMemberFunction "  static member Value = 42"
    |> Expect.isFalse "static member without params is property"
  }
  test "static member with type annotation" {
    isStaticMemberFunction "  static member Default: int = 0"
    |> Expect.isFalse "type-annotated value is not function"
  }
  test "non-static member is not matched" {
    isStaticMemberFunction "  member this.Foo(x) = x"
    |> Expect.isFalse "instance member is not static"
  }
  test "regular let binding" {
    isStaticMemberFunction "let f x = x"
    |> Expect.isFalse "let binding is not static member"
  }
  test "no equals sign" {
    isStaticMemberFunction "  static member Foo"
    |> Expect.isFalse "no equals, not a complete definition"
  }
]

// ═══════════════════════════════════════════════════════════
// AutoCompletion — CompletionKind.ofGlyph
// ═══════════════════════════════════════════════════════════

let completionKindOfGlyphTests = testList "CompletionKind.ofGlyph" [
  test "Class maps to Class" {
    CompletionKind.ofGlyph FSharpGlyph.Class
    |> Expect.equal "Class" CompletionKind.Class
  }
  test "Method maps to Method" {
    CompletionKind.ofGlyph FSharpGlyph.Method
    |> Expect.equal "Method" CompletionKind.Method
  }
  test "OverridenMethod maps to OverriddenMethod" {
    CompletionKind.ofGlyph FSharpGlyph.OverridenMethod
    |> Expect.equal "OverridenMethod" CompletionKind.OverriddenMethod
  }
  test "NameSpace maps to Namespace" {
    CompletionKind.ofGlyph FSharpGlyph.NameSpace
    |> Expect.equal "Namespace" CompletionKind.Namespace
  }
  test "Error maps to Type" {
    CompletionKind.ofGlyph FSharpGlyph.Error
    |> Expect.equal "Error fallback → Type" CompletionKind.Type
  }
  test "all glyphs handled without exception" {
    let glyphs = [
      FSharpGlyph.Class; FSharpGlyph.Constant; FSharpGlyph.Delegate
      FSharpGlyph.Enum; FSharpGlyph.EnumMember; FSharpGlyph.Event
      FSharpGlyph.Exception; FSharpGlyph.Field; FSharpGlyph.Interface
      FSharpGlyph.Method; FSharpGlyph.OverridenMethod; FSharpGlyph.Module
      FSharpGlyph.NameSpace; FSharpGlyph.Property; FSharpGlyph.Struct
      FSharpGlyph.Typedef; FSharpGlyph.Type; FSharpGlyph.Union
      FSharpGlyph.Variable; FSharpGlyph.ExtensionMethod; FSharpGlyph.Error
      FSharpGlyph.TypeParameter
    ]
    for g in glyphs do
      CompletionKind.ofGlyph g |> ignore
  }
]

// ═══════════════════════════════════════════════════════════
// AppState — stripAnsi
// ═══════════════════════════════════════════════════════════

let stripAnsiTests = testList "stripAnsi" [
  test "removes color escape sequences" {
    stripAnsi "\u001b[31mred text\u001b[0m"
    |> Expect.equal "color stripped" "red text"
  }
  test "removes bold/underline" {
    stripAnsi "\u001b[1mbold\u001b[22m"
    |> Expect.equal "bold stripped" "bold"
  }
  test "cursor reset becomes newline" {
    stripAnsi "line1\u001b[0Gline2"
    |> Expect.stringContains "cursor reset becomes newline" "line1"
  }
  test "plain text unchanged" {
    stripAnsi "hello world"
    |> Expect.equal "no change" "hello world"
  }
  test "empty string unchanged" {
    stripAnsi "" |> Expect.equal "empty" ""
  }
  test "multiple sequences stripped" {
    stripAnsi "\u001b[32mgreen\u001b[0m and \u001b[34mblue\u001b[0m"
    |> Expect.equal "both stripped" "green and blue"
  }
]

// ═══════════════════════════════════════════════════════════
// AppState — reformatExpectoSummary
// ═══════════════════════════════════════════════════════════

let reformatExpectoSummaryTests = testList "reformatExpectoSummary" [
  test "reformats standard expecto summary" {
    let input = "EXPECTO! 10 tests run in 00:00:01.234 for MyTests \u2013 8 passed, 1 ignored, 1 failed, 0 errored. OK!"
    let result = reformatExpectoSummary input
    result |> Expect.stringContains "has test suite name" "MyTests"
    result |> Expect.stringContains "has count" "10"
    result |> Expect.stringContains "has passed" "8"
    result |> Expect.stringContains "has failed" "1"
  }
  test "non-expecto line passes through" {
    let input = "just a regular line"
    reformatExpectoSummary input
    |> Expect.equal "unchanged" "just a regular line"
  }
]

// ═══════════════════════════════════════════════════════════
// Combined
// ═══════════════════════════════════════════════════════════

[<Tests>]
let allPureFunctionCoverageTests = testList "Pure function coverage" [
  testList "HotReloading" [
    isTopLevelFunctionBindingTests
    isStaticMemberFunctionTests
  ]
  testList "AutoCompletion" [
    completionKindOfGlyphTests
  ]
  testList "AppState" [
    stripAnsiTests
    reformatExpectoSummaryTests
  ]
]
