module SageFs.Tests.DashboardFailureNarrativesTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private makeNarrative
  (summary: string)
  (timeSince: TimeSpan option)
  (causal: CausalChange list)
  (propViolation: PropertyViolationDetail option)
  : FailureNarrative =
  { Summary = summary
    TimeSinceLastPass = timeSince
    LastPassedAt = None
    CausalChanges = causal
    PropertyViolation = propViolation }

let private defaultNarrative () =
  makeNarrative "Test failed: expected 42 but got 0" None [] None

let private renderToString (node: XmlNode) =
  renderNode node

[<Tests>]
let failureNarrativeEntryTests =
  testList "FailureNarrativeEntry" [

    test "fromNarrative populates TestName" {
      let n = defaultNarrative ()
      let entry = FailureNarrativeEntry.fromNarrative "My.Suite.myTest" n
      entry.TestName |> Expect.equal "should populate test name" "My.Suite.myTest"
    }

    test "fromNarrative populates Summary" {
      let n = makeNarrative "expected 5 but was 3" None [] None
      let entry = FailureNarrativeEntry.fromNarrative "some test" n
      entry.Summary |> Expect.equal "should populate summary" "expected 5 but was 3"
    }

    test "formatTimeSince None returns None" {
      let result = FailureNarrativeEntry.formatTimeSince None
      result |> Expect.isNone "should return None when no timing data"
    }

    test "formatTimeSince under 60s returns just now" {
      let result = FailureNarrativeEntry.formatTimeSince (Some (TimeSpan.FromSeconds 30.0))
      result |> Expect.equal "should say 'just now'" (Some "just now")
    }

    test "formatTimeSince 5 minutes returns minutes label" {
      let result = FailureNarrativeEntry.formatTimeSince (Some (TimeSpan.FromMinutes 5.0))
      result |> Expect.equal "should say '5 minutes ago'" (Some "5 minutes ago")
    }

    test "formatTimeSince 2 hours returns hours label" {
      let result = FailureNarrativeEntry.formatTimeSince (Some (TimeSpan.FromHours 2.0))
      result |> Expect.equal "should say '2 hours ago'" (Some "2 hours ago")
    }

    test "fromNarrative formats symbol causal change" {
      let n = makeNarrative "failed" None [ CausalChange.SymbolChanged "MyModule.myFn" ] None
      let entry = FailureNarrativeEntry.fromNarrative "t" n
      entry.CausalLabels |> Expect.contains "should include symbol label" "symbol: MyModule.myFn"
    }

    test "fromNarrative formats file causal change" {
      let n = makeNarrative "failed" None [ CausalChange.FileChanged "C:/src/Foo.fs" ] None
      let entry = FailureNarrativeEntry.fromNarrative "t" n
      entry.CausalLabels |> Expect.contains "should include file name" "file: Foo.fs"
    }

    test "fromNarrative HasPropertyViolation false when None" {
      let n = makeNarrative "failed" None [] None
      let entry = FailureNarrativeEntry.fromNarrative "t" n
      entry.HasPropertyViolation |> Expect.isFalse "should be false when no property violation"
    }

    test "fromNarrative HasPropertyViolation true when Some" {
      let pv : PropertyViolationDetail =
        { PropertyName = Some "commutativity"
          ShrunkCounterexample = "(0, -1)"
          AlgebraicCategory = Some "commutativity" }
      let n = makeNarrative "property failed" None [] (Some pv)
      let entry = FailureNarrativeEntry.fromNarrative "t" n
      entry.HasPropertyViolation |> Expect.isTrue "should be true when property violation exists"
    }

  ]

[<Tests>]
let failureNarrativesPanelViewTests =
  testList "FailureNarrativesPanelView" [

    test "fromNarratives empty list returns empty Entries" {
      let view = FailureNarrativesPanelView.fromNarratives []
      view.Entries |> Expect.isEmpty "should have no entries for empty input"
    }

    test "fromNarratives two items returns two Entries" {
      let pairs =
        [ "test.one", defaultNarrative ()
          "test.two", makeNarrative "boom" None [] None ]
      let view = FailureNarrativesPanelView.fromNarratives pairs
      view.Entries |> List.length |> Expect.equal "should have 2 entries" 2
    }

    test "fromNarratives preserves test name order" {
      let pairs =
        [ "alpha", defaultNarrative ()
          "beta", defaultNarrative () ]
      let view = FailureNarrativesPanelView.fromNarratives pairs
      view.Entries.[0].TestName |> Expect.equal "first should be alpha" "alpha"
      view.Entries.[1].TestName |> Expect.equal "second should be beta" "beta"
    }

  ]

[<Tests>]
let renderFailureNarrativesTests =
  testList "renderFailureNarratives HTML" [

    test "empty view renders with failure-narratives id" {
      let view = FailureNarrativesPanelView.fromNarratives []
      let html = renderFailureNarratives view |> renderToString
      html |> Expect.stringContains "should have correct id" "failure-narratives"
    }

    test "renders test name in output" {
      let pairs = [ "MyModule.criticalTest", defaultNarrative () ]
      let html = renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs) |> renderToString
      html |> Expect.stringContains "should contain test name" "criticalTest"
    }

    test "renders summary text" {
      let pairs = [ "t", makeNarrative "expected 42 but was 0" None [] None ]
      let html = renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs) |> renderToString
      html |> Expect.stringContains "should contain summary" "expected 42 but was 0"
    }

    test "renders time-since label when present" {
      let pairs = [ "t", makeNarrative "failed" (Some (TimeSpan.FromMinutes 7.0)) [] None ]
      let html = renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs) |> renderToString
      html |> Expect.stringContains "should contain timing label" "7 minutes ago"
    }

    test "renders causal change label" {
      let pairs = [ "t", makeNarrative "failed" None [ CausalChange.SymbolChanged "Foo.bar" ] None ]
      let html = renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs) |> renderToString
      html |> Expect.stringContains "should contain causal symbol" "Foo.bar"
    }

    test "renders property violation indicator" {
      let pv : PropertyViolationDetail =
        { PropertyName = Some "prop"
          ShrunkCounterexample = "(0)"
          AlgebraicCategory = None }
      let pairs = [ "t", makeNarrative "prop failed" None [] (Some pv) ]
      let html = renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs) |> renderToString
      html |> Expect.stringContains "should show property violation indicator" "property"
    }

    test "renders failure count badge for multiple failures" {
      let pairs =
        [ "t1", defaultNarrative ()
          "t2", defaultNarrative ()
          "t3", defaultNarrative () ]
      let html = renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs) |> renderToString
      html |> Expect.stringContains "should show count badge" "3"
    }

    test "each failure entry has narrative-entry class" {
      let pairs = [ "t", defaultNarrative () ]
      let html = renderFailureNarratives (FailureNarrativesPanelView.fromNarratives pairs) |> renderToString
      html |> Expect.stringContains "should have narrative-entry class" "narrative-entry"
    }

    test "empty view renders no content (silent when empty)" {
      let view = FailureNarrativesPanelView.fromNarratives []
      let html = renderFailureNarratives view |> renderToString
      html.Contains("no recent") |> Expect.isFalse "should NOT show no-failures message when empty (progressive disclosure)"
    }

  ]
