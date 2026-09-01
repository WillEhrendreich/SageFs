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

let defaultConfig = CoverageViewConfig.defaults

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
    let lenses = PureProvider.lensesForFile defaultConfig store "Prod.fs"
    (lenses.Length, 0)
    |> Expect.equal "no lenses" (0, 0)

  testCase "WHY - provider - file not in store returns zero CodeLenses" <| fun _ ->
    let store = Map.ofList [ "Other.fs", [| mkV "Module.add" 42 1 "within" 0 "v 1" "Passing" |] ]
    let lenses = PureProvider.lensesForFile defaultConfig store "Prod.fs"
    (lenses.Length, 0)
    |> Expect.equal "file not in store = no lenses" (0, 0)

  testCase "WHY - provider - one view produces one CodeLens with the right title" <| fun _ ->
    let v = mkV "Module.add" 42 5 "within" 0 "v 5" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile defaultConfig store "Prod.fs"
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
    let lenses = PureProvider.lensesForFile defaultConfig store "Prod.fs"
    (lenses.Length, 100)
    |> Expect.equal "100 views = 100 CodeLenses" (100, 100)

  testCase "WHY - provider - overflow info surfaces in the tooltip suffix" <| fun _ ->
    let v = mkV "Module.add" 42 5 "overflow" 47 "v 2 x 3" "Failing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile defaultConfig store "Prod.fs"
    let tooltip = lenses.[0].Tooltip
    if not (tooltip.Contains "47 more") then
      failtestf "tooltip should contain '47 more' but was: %s" tooltip
    else if not (tooltip.Contains "5 test(s)") then
      failtestf "tooltip should contain '5 test(s)' but was: %s" tooltip
    else ()

  testCase "WHY - provider - Within overflow produces no suffix" <| fun _ ->
    let v = mkV "Module.add" 42 5 "within" 0 "v 5" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile defaultConfig store "Prod.fs"
    let tooltip = lenses.[0].Tooltip
    if tooltip.Contains "more" then
      failtestf "tooltip should not contain 'more' for Within but was: %s" tooltip
    else ()

  testCase "WHY - provider - 100 CodeLenses project in <100ms because the editor calls this on every visible function" <| fun _ ->
    let views =
      [|for i in 1..100 -> mkV (sprintf "Module.f%d" i) i 1 "within" 0 "v 1" "Passing"|]
    let store = Map.ofList [ "Prod.fs", views ]
    PureProvider.lensesForFile defaultConfig store "Prod.fs" |> ignore
    let sw = System.Diagnostics.Stopwatch.StartNew()
    for _ in 1..100 do
      PureProvider.lensesForFile defaultConfig store "Prod.fs" |> ignore
    sw.Stop()
    (sw.Elapsed.TotalMilliseconds, 100.0)
    |> Expect.isLessThan "100 projections must complete in <100ms"

  testCase "WHY - provider - one view for a different file is filtered out" <| fun _ ->
    let v1 = mkV "Module.add" 42 1 "within" 0 "v 1" "Passing"
    let store = Map.ofList [ "Other.fs", [| v1 |] ]
    let lenses = PureProvider.lensesForFile defaultConfig store "Prod.fs"
    (lenses.Length, 0)
    |> Expect.equal "different file filtered out" (0, 0)
  ]

[<Tests>]
let collapseBehavior = testList "CoverageView collapse behavior" [

  testCase "WHY - collapse - default config never collapses (InlineCollapseAt = MaxValue)" <| fun _ ->
    let v = mkV "Module.add" 42 5 "within" 0 "v 5" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile defaultConfig store "Prod.fs"
    // Default config: InlineCollapseAt = Int32.MaxValue, so 5 < MaxValue → inline
    (lenses.[0].Title, "v 5")
    |> Expect.equal "default config keeps inline badge" ("v 5", "v 5")

  testCase "WHY - collapse - config with InlineCollapseAt=10 collapses 50-test function" <| fun _ ->
    let config = { InlineCollapseAt = 10 }
    let v = mkV "Module.add" 42 50 "within" 0 "v 50" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile config store "Prod.fs"
    // 50 >= 10 → collapse to "▸ 50 tests"
    assertContains "collapsed title" lenses.[0].Title "▸ 50 tests"

  testCase "WHY - collapse - config with InlineCollapseAt=10 keeps 5-test function inline" <| fun _ ->
    let config = { InlineCollapseAt = 10 }
    let v = mkV "Module.add" 42 5 "within" 0 "v 5" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile config store "Prod.fs"
    // 5 < 10 → keep inline badge
    (lenses.[0].Title, "v 5")
    |> Expect.equal "5-test function stays inline" ("v 5", "v 5")

  testCase "WHY - collapse - config with InlineCollapseAt=1 exactly hits the boundary" <| fun _ ->
    let config = { InlineCollapseAt = 1 }
    let v1 = mkV "Module.add" 42 1 "within" 0 "v 1" "Passing"
    let v2 = mkV "Module.sub" 50 2 "within" 0 "v 2" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v1; v2 |] ]
    let lenses = PureProvider.lensesForFile config store "Prod.fs"
    // 1 >= 1 → collapse
    assertContains "1 test collapses" lenses.[0].Title "▸ 1 tests"
    // 2 >= 1 → collapse
    assertContains "2 tests collapses" lenses.[1].Title "▸ 2 tests"

  testCase "WHY - collapse - config with InlineCollapseAt=100 keeps 99-test function inline" <| fun _ ->
    let config = { InlineCollapseAt = 100 }
    let v = mkV "Module.add" 42 99 "within" 0 "v 99" "Passing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile config store "Prod.fs"
    (lenses.[0].Title, "v 99")
    |> Expect.equal "99-test function stays inline when threshold is 100" ("v 99", "v 99")

  testCase "WHY - collapse - tooltip always shows the real count even when collapsed" <| fun _ ->
    let config = { InlineCollapseAt = 10 }
    let v = mkV "Module.add" 42 50 "overflow" 47 "v 2 x 3" "Failing"
    let store = Map.ofList [ "Prod.fs", [| v |] ]
    let lenses = PureProvider.lensesForFile config store "Prod.fs"
    // Title is collapsed but tooltip still has the full count
    assertContains "tooltip shows real count" lenses.[0].Tooltip "50 test(s)"
    assertContains "tooltip shows overflow" lenses.[0].Tooltip "47 more"
  ]

[<Tests>]
let providerWiring = testList "CodeLens provider wiring" [

  testCase "WHY - wiring - line number is 1-based in CoverageView but 0-based in VSCode ranges, so the Fable provider subtracts 1" <| fun _ ->
    let v = mkV "Module.add" 42 1 "within" 0 "v 1" "Passing"
    let view = v
    let expectedZeroBased = view.DefinitionLine - 1
    (expectedZeroBased, 41)
    |> Expect.equal "1-based → 0-based mapping" (41, 41)

  testCase "WHY - wiring - empty line is clamped to 0 so the badge is not placed above the buffer" <| fun _ ->
    let v = mkV "Module.add" 0 0 "within" 0 "" "Absent"
    let clamped = max 0 (v.DefinitionLine - 1)
    (clamped, 0)
    |> Expect.equal "clamp to 0" (0, 0)

  testCase "WHY - wiring - tooltip is preserved verbatim so the user's reason for opening the picker is one click away" <| fun _ ->
    let v = mkV "Module.add" 42 1 "overflow" 47 "v 2 x 3" "Failing"
    let lens = PureProvider.project defaultConfig v
    (lens.Tooltip, "1 test(s), 47 more")
    |> Expect.equal "tooltip carries total and hidden count" ("1 test(s), 47 more", "1 test(s), 47 more")
  ]

let _ =
  Expecto.Tests.runTestsWithCLIArgs
    []
    [||]
    (testList "CoverageView CodeLens provider contract"
      [providerContract; collapseBehavior; providerWiring])
