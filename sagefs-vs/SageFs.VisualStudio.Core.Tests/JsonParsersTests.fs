module SageFs.VisualStudio.Core.Tests.JsonParsersTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

let private parse (json: string) = JsonDocument.Parse(json).RootElement

// -- JsonHelpers.tryStr -------------------------------------------------------

[<Fact>]
let ``tryStr returns field value when present`` () =
  let el = parse """{"name":"alice"}"""
  tryStr el "name" "fallback" |> should equal "alice"

[<Fact>]
let ``tryStr returns fallback when field missing`` () =
  let el = parse """{}"""
  tryStr el "name" "fallback" |> should equal "fallback"

[<Fact>]
let ``tryStr returns fallback when field is not a string`` () =
  let el = parse """{"name":42}"""
  tryStr el "name" "fallback" |> should equal "fallback"

[<Fact>]
let ``tryStr returns empty string fallback`` () =
  let el = parse """{}"""
  tryStr el "missing" "" |> should equal ""

// -- JsonHelpers.tryInt -------------------------------------------------------

[<Fact>]
let ``tryInt returns field value when present`` () =
  let el = parse """{"count":7}"""
  tryInt el "count" 0 |> should equal 7

[<Fact>]
let ``tryInt returns fallback when field missing`` () =
  let el = parse """{}"""
  tryInt el "count" 99 |> should equal 99

[<Fact>]
let ``tryInt returns fallback when field is not a number`` () =
  let el = parse """{"count":"seven"}"""
  tryInt el "count" 0 |> should equal 0

// -- JsonHelpers.tryBool ------------------------------------------------------

[<Fact>]
let ``tryBool returns true when field is true`` () =
  let el = parse """{"flag":true}"""
  tryBool el "flag" false |> should equal true

[<Fact>]
let ``tryBool returns false when field is false`` () =
  let el = parse """{"flag":false}"""
  tryBool el "flag" true |> should equal false

[<Fact>]
let ``tryBool returns fallback when field missing`` () =
  let el = parse """{}"""
  tryBool el "flag" true |> should equal true

// -- parseDiffLineInfo --------------------------------------------------------

[<Fact>]
let ``parseDiffLineInfo parses Added kind`` () =
  let el = parse """{"kind":"added","text":"let x = 1"}"""
  let result = parseDiffLineInfo el
  result.Kind |> should equal DiffLineKind.Added
  result.Text |> should equal "let x = 1"

[<Fact>]
let ``parseDiffLineInfo parses Removed kind`` () =
  let el = parse """{"kind":"removed","text":"old line"}"""
  let result = parseDiffLineInfo el
  result.Kind |> should equal DiffLineKind.Removed

[<Fact>]
let ``parseDiffLineInfo parses Modified kind`` () =
  let el = parse """{"kind":"modified","text":"changed","oldText":"original"}"""
  let result = parseDiffLineInfo el
  result.Kind |> should equal DiffLineKind.Modified
  result.OldText |> should equal (Some "original")

[<Fact>]
let ``parseDiffLineInfo defaults to Unchanged for unknown kind`` () =
  let el = parse """{"kind":"unknown","text":"line"}"""
  parseDiffLineInfo el |> (fun r -> r.Kind) |> should equal DiffLineKind.Unchanged

[<Fact>]
let ``parseDiffLineInfo defaults to Unchanged when kind is missing`` () =
  let el = parse """{"text":"line"}"""
  parseDiffLineInfo el |> (fun r -> r.Kind) |> should equal DiffLineKind.Unchanged

[<Fact>]
let ``parseDiffLineInfo OldText is None when field absent`` () =
  let el = parse """{"kind":"added","text":"new"}"""
  parseDiffLineInfo el |> (fun r -> r.OldText) |> should equal None

// -- parseCellGraphInfo -------------------------------------------------------

[<Fact>]
let ``parseCellGraphInfo empty cells and edges`` () =
  let el = parse """{"cells":[],"edges":[]}"""
  let result = parseCellGraphInfo el
  result.Cells |> should be Empty
  result.Edges |> should be Empty

[<Fact>]
let ``parseCellGraphInfo single node no edges`` () =
  let el = parse """{"cells":[{"cellId":1,"source":"let x=1","produces":["x"],"consumes":[],"isStale":false}],"edges":[]}"""
  let result = parseCellGraphInfo el
  result.Cells |> should haveLength 1
  result.Cells.[0].CellId |> should equal 1
  result.Cells.[0].Source |> should equal "let x=1"
  result.Cells.[0].Produces |> should equal ["x"]
  result.Edges |> should be Empty

[<Fact>]
let ``parseCellGraphInfo with edges`` () =
  let el = parse """{"cells":[],"edges":[{"from":1,"to":2}]}"""
  let result = parseCellGraphInfo el
  result.Edges |> should haveLength 1
  result.Edges.[0].From |> should equal 1
  result.Edges.[0].To |> should equal 2

[<Fact>]
let ``parseCellGraphInfo missing edges field returns empty`` () =
  let el = parse """{"cells":[]}"""
  let result = parseCellGraphInfo el
  result.Edges |> should be Empty

[<Fact>]
let ``parseCellGraphInfo missing cells field returns empty`` () =
  let el = parse """{"edges":[]}"""
  let result = parseCellGraphInfo el
  result.Cells |> should be Empty

[<Fact>]
let ``parseCellGraphInfo node isStale field is parsed`` () =
  let el = parse """{"cells":[{"cellId":2,"source":"","produces":[],"consumes":[],"isStale":true}],"edges":[]}"""
  let result = parseCellGraphInfo el
  result.Cells.[0].IsStale |> should equal true

// -- parseBindingScopeInfo ----------------------------------------------------

[<Fact>]
let ``parseBindingScopeInfo empty bindings`` () =
  let el = parse """{"bindings":[],"activeCount":0,"shadowedCount":0}"""
  let result = parseBindingScopeInfo el
  result.Bindings |> should be Empty
  result.ActiveCount |> should equal 0

[<Fact>]
let ``parseBindingScopeInfo with binding details`` () =
  let json = """{"bindings":[{"name":"x","typeSig":"int","cellIndex":1,"isShadowed":false,"shadowedBy":[],"referencedIn":[2,3]}],"activeCount":1,"shadowedCount":0}"""
  let el = parse json
  let result = parseBindingScopeInfo el
  result.Bindings |> should haveLength 1
  result.Bindings.[0].Name |> should equal "x"
  result.Bindings.[0].TypeSig |> should equal "int"
  result.Bindings.[0].CellIndex |> should equal 1
  result.Bindings.[0].IsShadowed |> should equal false
  result.Bindings.[0].ReferencedIn |> should equal [2; 3]
  result.ActiveCount |> should equal 1

[<Fact>]
let ``parseBindingScopeInfo shadowed binding`` () =
  let json = """{"bindings":[{"name":"y","typeSig":"string","cellIndex":0,"isShadowed":true,"shadowedBy":[2],"referencedIn":[]}],"activeCount":0,"shadowedCount":1}"""
  let el = parse json
  let result = parseBindingScopeInfo el
  result.Bindings.[0].IsShadowed |> should equal true
  result.Bindings.[0].ShadowedBy |> should equal [2]
  result.ShadowedCount |> should equal 1

[<Fact>]
let ``parseBindingScopeInfo missing bindings field returns empty`` () =
  let el = parse """{"activeCount":0,"shadowedCount":0}"""
  let result = parseBindingScopeInfo el
  result.Bindings |> should be Empty

// -- parseTimelineStatsInfo ---------------------------------------------------

[<Fact>]
let ``parseTimelineStatsInfo parses count and percentiles`` () =
  let json = """{"count":10,"p50Ms":12.5,"p95Ms":45.0,"p99Ms":100.0,"meanMs":15.0,"sparkline":"▁▂▃"}"""
  let el = parse json
  let result = parseTimelineStatsInfo el
  result.Count |> should equal 10
  result.P50Ms |> should equal (Some 12.5)
  result.P95Ms |> should equal (Some 45.0)
  result.P99Ms |> should equal (Some 100.0)
  result.MeanMs |> should equal (Some 15.0)
  result.Sparkline |> should equal "▁▂▃"

[<Fact>]
let ``parseTimelineStatsInfo with missing percentile fields uses None`` () =
  let el = parse """{"count":5}"""
  let result = parseTimelineStatsInfo el
  result.Count |> should equal 5
  result.P50Ms |> should equal None
  result.P95Ms |> should equal None
  result.P99Ms |> should equal None
  result.MeanMs |> should equal None

[<Fact>]
let ``parseTimelineStatsInfo missing sparkline returns empty string`` () =
  let el = parse """{"count":0}"""
  parseTimelineStatsInfo el |> (fun r -> r.Sparkline) |> should equal ""

[<Fact>]
let ``parseTimelineStatsInfo missing count defaults to zero`` () =
  let el = parse """{}"""
  parseTimelineStatsInfo el |> (fun r -> r.Count) |> should equal 0
