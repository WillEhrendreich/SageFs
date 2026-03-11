module SageFs.Tests.SyntaxHighlightTests

open Expecto
open Expecto.Flip
open System.Diagnostics
open SageFs

[<Tests>]
let syntaxHighlightTests = testSequenced <| testList "SyntaxHighlight" [
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

  testCase "bounded cache stays ≤ 512 after 513 unique inserts" <| fun _ ->
    SyntaxHighlight.clearCache()
    let theme = Theme.defaults
    for i in 1..513 do
      SyntaxHighlight.tokenize theme (sprintf "let x%d = %d" i i) |> ignore
    Expect.isLessThanOrEqual "cache bounded at 512" (SyntaxHighlight.cacheSize(), 512)

  testCase "bounded cache evicts oldest entry at capacity" <| fun _ ->
    SyntaxHighlight.clearCache()
    let theme = Theme.defaults
    let firstCode = "let evict_me = 0"
    SyntaxHighlight.tokenize theme firstCode |> ignore
    // Fill 512 more unique entries to push firstCode out
    for i in 1..512 do
      SyntaxHighlight.tokenize theme (sprintf "let filler_%d = %d" i i) |> ignore
    // Cache should be at capacity; firstCode key should no longer inflate it
    Expect.equal "cache at capacity" 512 (SyntaxHighlight.cacheSize())

  testCase "tokenize returns correct line count after cache eviction" <| fun _ ->
    SyntaxHighlight.clearCache()
    let theme = Theme.defaults
    let firstCode = "let a = 1\nlet b = 2"
    SyntaxHighlight.tokenize theme firstCode |> ignore
    for i in 1..512 do
      SyntaxHighlight.tokenize theme (sprintf "let repl_%d = %d" i i) |> ignore
    // firstCode was evicted; re-tokenizing should recompute correctly
    let result = SyntaxHighlight.tokenize theme firstCode
    Expect.equal "2 lines after recompute" 2 result.Length

  testCase "clearCache resets size to 0" <| fun _ ->
    let theme = Theme.defaults
    SyntaxHighlight.tokenize theme "let z = 99" |> ignore
    SyntaxHighlight.clearCache()
    Expect.equal "cache empty after clear" 0 (SyntaxHighlight.cacheSize())
]
