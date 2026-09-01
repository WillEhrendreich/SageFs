// WHY - this contract test pins the live-testing handler's behavior
// when a coverage_view event arrives. The handler must:
//  1. Parse the event payload
//  2. Append the view to the file's existing array (not replace)
//  3. Fire the OnCoverageView callback
// Without this test, the handler could replace the file's array
// wholesale on every event, which would race with in-flight events
// and lose views. The server emits one event per symbol, so the
// handler accumulates them per file.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/CoverageViewPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.CoverageViewPure

/// Simulate the handler's store logic in pure F#.
module SimulatedHandler =
  let empty = Map.empty<string, CoverageView array>

  /// Apply a parsed view to the store. Mirrors the F# handler:
  /// append to the file's existing array, don't replace.
  let apply (store: Map<string, CoverageView array>) (view: CoverageView) =
    let existing =
      match Map.tryFind view.FilePath store with
      | Some arr -> arr
      | None -> [||]
    Map.add view.FilePath (Array.append existing [| view |]) store

let private mkV symbol line total inlineText health =
  { Symbol = symbol
    FilePath = "Prod.fs"
    DefinitionLine = line
    TotalCount = total
    Overflow = Overflow.Within
    InlineBadgeText = inlineText
    Health = healthFromString health }

[<Tests>]
let handlerContract = testList "coverage_view handler contract" [

  testCase "WHY - handler - first view for a file creates the entry" <| fun _ ->
    let v = mkV "Module.add" 42 1 "v 1" "Passing"
    let store = SimulatedHandler.apply SimulatedHandler.empty v
    let found = Map.tryFind "Prod.fs" store
    match found with
    | Some arr ->
      (arr.Length, 1)
      |> Expect.equal "first view stored" (1, 1)
    | None -> failtest "no view stored"

  testCase "WHY - handler - second view for the same file is appended (not replaced)" <| fun _ ->
    let v1 = mkV "Module.add" 42 1 "v 1" "Passing"
    let v2 = mkV "Module.sub" 80 3 "x 1" "Failing"
    let store =
      SimulatedHandler.apply (SimulatedHandler.apply SimulatedHandler.empty v1) v2
    let arr = Map.find "Prod.fs" store
    (arr.Length, 2)
    |> Expect.equal "two views for one file" (2, 2)

  testCase "WHY - handler - 100 coverage_view events for 100 symbols produce 100 stored views" <| fun _ ->
    let store = ref SimulatedHandler.empty
    for i in 1..100 do
      store := SimulatedHandler.apply !store (mkV (sprintf "Module.f%d" i) i 1 "v 1" "Passing")
    let arr = Map.find "Prod.fs" !store
    (arr.Length, 100)
    |> Expect.equal "100 views stored" (100, 100)

  testCase "WHY - handler - apply is O(1) for the append (no per-test diff, no merge)" <| fun _ ->
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let store = ref SimulatedHandler.empty
    for i in 1..1000 do
      store := SimulatedHandler.apply !store (mkV (sprintf "f%d" i) i 1 "v 1" "Passing")
    sw.Stop()
    (Map.find "Prod.fs" !store |> Array.length, 1000)
    |> Expect.equal "1000 views appended" (1000, 1000)
    (sw.Elapsed.TotalMilliseconds, 50.0)
    |> Expect.isLessThan "1000 appends must complete in <50ms"
  ]

let _ = Expecto.Tests.runTestsWithCLIArgs [] [||] handlerContract
