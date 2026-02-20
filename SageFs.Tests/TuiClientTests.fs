module SageFs.Tests.TuiClientTests

open Expecto
open SageFs
open SageFs.DaemonClient

let parseStateEventTests = testList "parseStateEvent" [
  test "parses valid JSON with regions" {
    let json = """{"sessionState":"Ready","evalCount":5,"regions":[{"id":"output","content":"hello"},{"id":"editor","content":"code"}]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse valid JSON"
    let (sid, state, count, _avgMs, _, _, regions) = result.Value
    Expect.equal sid "" "missing sessionId defaults to empty"
    Expect.equal state "Ready" "session state"
    Expect.equal count 5 "eval count"
    Expect.equal regions.Length 2 "region count"
    Expect.equal regions.[0].Id "output" "first region id"
    Expect.equal regions.[0].Content "hello" "first region content"
    Expect.equal regions.[1].Id "editor" "second region id"
  }

  test "parses region with cursor" {
    let json = """{"sessionState":"Ready","evalCount":0,"regions":[{"id":"editor","content":"abc","cursor":{"line":1,"col":3}}]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (_, _, _avgMs, _, _, _, regions) = result.Value
    Expect.isSome regions.[0].Cursor "should have cursor"
    let cursor = regions.[0].Cursor.Value
    Expect.equal cursor.Line 1 "cursor line"
    Expect.equal cursor.Col 3 "cursor col"
  }

  test "parses region with null cursor" {
    let json = """{"sessionState":"Ready","evalCount":0,"regions":[{"id":"editor","content":"abc","cursor":null}]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (_, _, _avgMs, _, _, _, regions) = result.Value
    Expect.isNone regions.[0].Cursor "null cursor should be None"
  }

  test "parses region without cursor field" {
    let json = """{"sessionState":"Ready","evalCount":0,"regions":[{"id":"editor","content":"abc"}]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (_, _, _avgMs, _, _, _, regions) = result.Value
    Expect.isNone regions.[0].Cursor "missing cursor should be None"
  }

  test "returns None for invalid JSON" {
    let result = parseStateEvent "not json"
    Expect.isNone result "invalid JSON should be None"
  }

  test "returns None for missing fields" {
    let json = """{"sessionState":"Ready"}"""
    let result = parseStateEvent json
    Expect.isNone result "missing fields should be None"
  }

  test "empty regions array" {
    let json = """{"sessionState":"WarmingUp","evalCount":0,"regions":[]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse empty regions"
    let (_, state, count, _avgMs, _, _, regions) = result.Value
    Expect.equal state "WarmingUp" "state"
    Expect.equal count 0 "count"
    Expect.isEmpty regions "empty regions"
  }

  test "parses sessionId when present" {
    let json = """{"sessionId":"session-abc123","sessionState":"Ready","evalCount":3,"regions":[]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (sid, state, count, _avgMs, _, _, _regions) = result.Value
    Expect.equal sid "session-abc123" "sessionId"
    Expect.equal state "Ready" "session state"
    Expect.equal count 3 "eval count"
  }
]

let daemonRegionDataTests = testList "DaemonRegionData" [
  test "toRenderRegion maps all fields" {
    let data = { Id = "output"; Content = "hello\nworld"; Cursor = Some { Line = 0; Col = 5 }; Completions = None }
    let region = DaemonRegionData.toRenderRegion data
    Expect.equal region.Id "output" "id"
    Expect.equal region.Content "hello\nworld" "content"
    Expect.equal region.Flags RegionFlags.None "flags default to None"
    Expect.isEmpty region.Affordances "affordances empty"
    Expect.isSome region.Cursor "cursor preserved"
    Expect.equal region.Cursor.Value.Line 0 "cursor line"
    Expect.equal region.Cursor.Value.Col 5 "cursor col"
  }

  test "toRenderRegion with no cursor" {
    let data = { Id = "sessions"; Content = ""; Cursor = None; Completions = None }
    let region = DaemonRegionData.toRenderRegion data
    Expect.equal region.Id "sessions" "id"
    Expect.equal region.Content "" "empty content"
    Expect.isNone region.Cursor "no cursor"
  }
]

[<Tests>]
let allTuiClientTests = testList "TuiClient" [
  parseStateEventTests
  daemonRegionDataTests
]
