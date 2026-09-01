// WHY - this contract test pins the CodeLens provider's pure logic.
// The provider iterates the listener's CoverageViews map, filters
// by the active file, and emits one CodeLens per view. The rendering
// of the title/tooltip happens in the Fable module, but the
// filtering and per-view projection can be tested in pure F#.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/CoverageViewPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.CoverageViewPure

type PureCodeLens = {
  Line: int
  Title: string
  Tooltip: string
  CommandLabel: string
}

module PureProvider =
  let tooltipSuffix (v: CoverageView) : string =
    match v.Overflow with
    | Overflow.Within -> ""
    | Overflow.Overflow n -> sprintf "%d more" n

  let project (v: CoverageView) : PureCodeLens =
    {
      Line = v.DefinitionLine
      Title = v.InlineBadgeText
      Tooltip = sprintf "%d test(s), %s" v.TotalCount (tooltipSuffix v)
      CommandLabel = "sagefs.showCoveringTests"
    }

  let lensesForFile
    (store: Map<string, CoverageView array>)
    (file: string)
    : PureCodeLens array =
    match Map.tryFind file store with
    | Some arr -> arr |> Array.map project
    | None -> [||]

let private mkV symbol line total overflowKind hidden inlineText healthStr =
  { Symbol = symbol
    FilePath = "Prod.fs"
    DefinitionLine = line
    TotalCount = total
    Overflow =
      match overflowKind with
      | "overflow" -> Overflow.Overflow hidden
      | _ -> Overflow.Within
    InlineBadgeText = inlineText
    Health = healthFromString healthStr }

let private assertContains (msg: string) (s: string) (sub: string) =
  if s.Contains sub then ()
  else failtestf "%s\n  actual: %s\n  expected to contain: %s" msg s sub

let private assertNotContains (msg: string) (s: string) (sub: string) =
  if s.Contains sub then failtestf "%s\n  actual: %s\n  expected NOT to contain: %s" msg s sub
  else ()

[<Tests>]
let providerContract = testList "CoverageView CodeLens provider contract" [

  testCase "WHY - provider - empty store returns zero CodeLenses" <| fun _ ->
    let store = Map.empty<string, CoverageView array>
    let lenses = PureProvider.lensesForFile store "Prod.fs"
    (lenses.Length, 0)
    |> Expect.equal "no lenses" (0, 0)

  testCase "WHY - provider - file not in store returns zero CodeLenses" <| fun _ ->
    let store = Map.ofList [ "Other.fs", [| mkV "Module.add" 42 1 "within" 0 "v 1" "Passing" |] ]
    let lenses = PureProvider.lensesForFile store "Prod.fs"
    (lenses.Length, 0)
    |> Expect.equal "file not in store = no lenses" (0, 0)

  testCase "WHY - provider - one view produces one CodeLens with the right title" <| fun _ ->
    let v = mkV "Module.add" 42 5 "within" 0 "v 5" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile store "Prod.fs"
    (lenses.Length, 1)
    |> Expect.equal "one view = one CodeLens" (1, 1)
    (lenses.[0].Title, "v 5")
    |> Expect.equal "title is the inline badge"
    (lenses.[0].Line, 42)
    |> Expect.equal "line is the definition line" (42, 42)

  testCase "WHY - provider - 100 views for one file produce 100 CodeLenses (one per function)" <| fun _ ->
    let views =
      [|for i in 1..100 -> mkV (sprintf "Module.f%d" i) i 1 "within" 0 "v 1" "Passing"|]
    let store = Map.ofList [ "Prod.fs", views ]
    let lenses = PureProvider.lensesForFile store "Prod.fs"
    (lenses.Length, 100)
    |> Expect.equal "100 views = 100 CodeLenses" (100, 100)

  testCase "WHY - provider - overflow info surfaces in the tooltip suffix" <| fun _ ->
    let v = mkV "Module.add" 42 5 "overflow" 47 "v 2 x 3" "Failing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile store "Prod.fs"
    let tooltip = lenses.[0].Tooltip
    if not (tooltip.Contains "47 more") then
      failtestf "tooltip should contain '47 more' but was: %s" tooltip
    else if not (tooltip.Contains "5 test(s)") then
      failtestf "tooltip should contain '5 test(s)' but was: %s" tooltip
    else ()

  testCase "WHY - provider - Within overflow produces no suffix" <| fun _ ->
    let v = mkV "Module.add" 42 5 "within" 0 "v 5" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile store "Prod.fs"
    let tooltip = lenses.[0].Tooltip
    if tooltip.Contains "more" then
      failtestf "tooltip should not contain 'more' for Within but was: %s" tooltip
    else ()

  testCase "WHY - provider - 100 CodeLenses project in <100ms because the editor calls this on every visible function" <| fun _ ->
    let views =
      [|for i in 1..100 -> mkV (sprintf "Module.f%d" i) i 1 "within" 0 "v 1" "Passing"|]
    let store = Map.ofList [ "Prod.fs", views ]
    PureProvider.lensesForFile store "Prod.fs" |> ignore
    let sw = System.Diagnostics.Stopwatch.StartNew()
    for _ in 1..100 do
      PureProvider.lensesForFile store "Prod.fs" |> ignore
    sw.Stop()
    (sw.Elapsed.TotalMilliseconds, 100.0)
    |> Expect.isLessThan "100 projections must complete in <100ms"

  testCase "WHY - provider - one view for a different file is filtered out" <| fun _ ->
    let v1 = mkV "Module.add" 42 1 "within" 0 "v 1" "Passing"
    let store = Map.ofList [ "Other.fs", [| v1 |] ]
    let lenses = PureProvider.lensesForFile store "Prod.fs"
    (lenses.Length, 0)
    |> Expect.equal "different file filtered out" (0, 0)
  ]

let _ = Expecto.Tests.runTestsWithCLIArgs [] [||] providerContract
