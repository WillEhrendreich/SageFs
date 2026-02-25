module SageFs.Tests.FsiRewriteTests

open System
open Expecto
open Expecto.Flip
open SageFs.FsiRewrite

let fsiRewriteTests =
  testList "FsiRewrite" [
    testList "rewriteInlineUseStatements" [
      test "rewrites indented use to let" {
        let input = "let foo () =\n  use x = new System.IO.MemoryStream()\n  x"
        let result = rewriteInlineUseStatements input
        result |> Expect.stringContains "should contain let instead of use" "let x = new"
      }
      test "preserves non-indented use" {
        let input = "use x = new System.IO.MemoryStream()"
        let result = rewriteInlineUseStatements input
        result |> Expect.equal "should not change top-level use" input
      }
      test "returns original string when no changes" {
        let input = "let x = 42\nlet y = x + 1"
        let result = rewriteInlineUseStatements input
        Object.ReferenceEquals(result, input) |> Expect.isTrue "should return same string instance"
      }
      test "handles multiple use statements" {
        let input = "let foo () =\n  use a = something()\n  use b = other()\n  (a, b)"
        let result = rewriteInlineUseStatements input
        result |> Expect.stringContains "should rewrite first use" "let a ="
        result |> Expect.stringContains "should rewrite second use" "let b ="
      }
      test "preserves indentation" {
        let input = "let foo () =\n    use x = bar()\n    x"
        let result = rewriteInlineUseStatements input
        result |> Expect.stringContains "should keep indentation" "    let x ="
      }
      test "rewrites use in async block" {
        let input = "async {\n  use x = something()\n  return x\n}"
        let result = rewriteInlineUseStatements input
        result |> Expect.stringContains "should rewrite plain use" "let x ="
      }
      test "empty string returns same" {
        let result = rewriteInlineUseStatements ""
        result |> Expect.equal "should return empty" ""
      }
      test "no use keyword returns unchanged" {
        let input = "let result = compute()\nprintfn \"%A\" result"
        let result = rewriteInlineUseStatements input
        Object.ReferenceEquals(result, input) |> Expect.isTrue "should return same instance"
      }
      test "use in middle of line not rewritten" {
        let input = "  let _ = use_something()"
        let result = rewriteInlineUseStatements input
        result |> Expect.equal "should not rewrite use_ prefix" input
      }
      test "tabs count as indentation" {
        let input = "let foo =\n\tuse x = bar()\n\tx"
        let result = rewriteInlineUseStatements input
        result |> Expect.stringContains "should rewrite tabbed use" "let x ="
      }
    ]
  ]
