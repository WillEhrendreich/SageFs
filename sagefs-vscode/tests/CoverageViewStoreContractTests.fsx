// WHY - this contract test pins the storage layer shape for coverage
// views. The editor maintains a Map<string, CoverageView array> keyed
// by file path. Each entry is one CoverageView per function. The
// CodeLens provider iterates this map; the new event handler updates
// it. Without this test, the three layers (event handler, storage,
// provider) can drift apart.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/CoverageViewPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.CoverageViewPure

let private makeView symbol line total inlineText healthStr =
  { Symbol = symbol
    FilePath = "Prod.fs"
    DefinitionLine = line
    TotalCount = total
    Overflow = Overflow.Within
    InlineBadgeText = inlineText
    Health = healthFromString healthStr }

[<Tests>]
let coverageViewStore = testList "VS Code CoverageView store" [

  testCase "WHY - store - file path is the key so the CodeLens provider can match views to the active document" <| fun _ ->
    let store : Map<string, CoverageView array> = Map.empty
    Map.containsKey "Prod.fs" store |> Expect.isFalse "empty store has no key"

  testCase "WHY - store - one file with one view round-trips" <| fun _ ->
    let view = makeView "Module.add" 42 1 "v 1" "Passing"
    let store = Map.ofList [ "Prod.fs", [| view |] ]
    (Map.find "Prod.fs" store |> Array.length, 1)
    |> Expect.equal "one view" (1, 1)

  testCase "WHY - store - multiple views for the same file coexist (one per function definition)" <| fun _ ->
    let v1 = makeView "Module.add" 42 1 "v 1" "Passing"
    let v2 = makeView "Module.sub" 80 3 "x 1" "SomeFailing"
    let store = Map.ofList [ "Prod.fs", [| v1; v2 |] ]
    let arr = Map.find "Prod.fs" store
    (arr.Length, 2)
    |> Expect.equal "two views for the same file" (2, 2)
    (arr.[0].Symbol, "Module.add")
    |> Expect.equal "first view" ("Module.add", "Module.add")
    (arr.[1].Symbol, "Module.sub")
    |> Expect.equal "second view" ("Module.sub", "Module.sub")

  testCase "WHY - store - replacing a file's views is one Map.add (no merge, no per-test diff)" <| fun _ ->
    let v1 = makeView "Module.add" 42 1 "v 1" "Passing"
    let v2 = makeView "Module.add" 42 1 "v 1" "Failing" // same symbol/line, different health
    let store =
      Map.ofList [ "Prod.fs", [| v1 |] ]
      |> Map.add "Prod.fs" [| v2 |]
    let arr = Map.find "Prod.fs" store
    (arr.Length, 1)
    |> Expect.equal "replaced, not merged" (1, 1)
    (arr.[0].Health, CoverageHealth.Failing)
    |> Expect.equal "new health" (CoverageHealth.Failing, CoverageHealth.Failing)

  testCase "WHY - store - 1000 views for one file iterate in O(n) because the CodeLens provider must render the visible functions on every change" <| fun _ ->
    let views =
      [|for i in 1..1000 -> makeView (sprintf "Module.f%d" i) i 1 "v 1" "Passing"|]
    let store = Map.ofList [ "Prod.fs", views ]
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let found = Map.find "Prod.fs" store |> Array.length
    sw.Stop()
    (found, 1000)
    |> Expect.equal "1000 views found" (1000, 1000)
    (sw.Elapsed.TotalMilliseconds, 50.0)
    |> Expect.isLessThan "Map.find on 1000 views must complete in <50ms"
  ]

let _ = Expecto.Tests.runTestsWithCLIArgs [] [||] coverageViewStore
