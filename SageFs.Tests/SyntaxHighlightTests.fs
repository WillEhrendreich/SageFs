module SageFs.Tests.SyntaxHighlightTests

open Expecto
open Expecto.Flip
open System.Diagnostics
open SageFs

[<Tests>]
let syntaxHighlightTests = testList "SyntaxHighlight" [
  testCase "tokenize cached lookup is < 5µs" <| fun _ ->
    let theme = Theme.defaults
    let code = "let x = 1\nlet y = x + 2\nprintfn \"%d\" y"
    for _ in 1..1000 do
      SyntaxHighlight.tokenize theme code |> ignore
    let sw = Stopwatch.StartNew()
    let n = 50_000
    for _ in 1..n do
      SyntaxHighlight.tokenize theme code |> ignore
    sw.Stop()
    let usPerOp = sw.Elapsed.TotalMicroseconds / float n
    printfn "tokenize cached: %.3fµs/op" usPerOp
    Expect.isLessThan "should be < 5µs" (usPerOp, 5.0)

  testCase "tokenize returns correct line count" <| fun _ ->
    let theme = Theme.defaults
    let code = "let x = 1\nlet y = 2\nlet z = 3"
    let result = SyntaxHighlight.tokenize theme code
    Expect.equal "3 lines" 3 result.Length

  testCase "tokenize empty string returns empty" <| fun _ ->
    let theme = Theme.defaults
    let result = SyntaxHighlight.tokenize theme ""
    Expect.equal "empty" 0 result.Length

  testCase "tokenize produces spans with keyword colors" <| fun _ ->
    if not (SyntaxHighlight.isAvailable()) then
      Tests.skiptest "tree-sitter-fsharp not available on this platform"
    let theme = Theme.defaults
    let result = SyntaxHighlight.tokenize theme "let x = 1"
    Expect.isGreaterThan "should have spans" (result.[0].Length, 0)
]
