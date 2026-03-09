module SageFs.Tests.ScratchPadTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features

[<Tests>]
let scratchPadBasicTests =
  testList "ScratchPad basics" [

    testCase "create pad with label" <| fun _ ->
      let pad = ScratchPad.create "exploration"
      pad.Label |> Expect.equal "label" "exploration"
      ScratchPad.snippetCount pad |> Expect.equal "empty" 0

    testCase "add snippet increments count" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "let x = 1"
        |> ScratchPad.addSnippet "let y = 2"
      ScratchPad.snippetCount pad |> Expect.equal "two snippets" 2

    testCase "snippets are ordered newest-first" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "first"
        |> ScratchPad.addSnippet "second"
      let snippets = ScratchPad.snippets pad
      snippets.[0].Code |> Expect.equal "newest" "second"
      snippets.[1].Code |> Expect.equal "oldest" "first"

    testCase "snippets have sequential ids" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "a"
        |> ScratchPad.addSnippet "b"
      let snippets = ScratchPad.snippets pad
      (snippets.[0].Id > snippets.[1].Id) |> Expect.isTrue "newer has higher id"

    testCase "clear removes all snippets" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "x"
        |> ScratchPad.addSnippet "y"
        |> ScratchPad.clear
      ScratchPad.snippetCount pad |> Expect.equal "cleared" 0
  ]

[<Tests>]
let scratchPadResultTests =
  testList "ScratchPad results" [

    testCase "record result for snippet" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "1 + 1"
        |> ScratchPad.recordResult 1 (Ok "val it: int = 2")
      let snippets = ScratchPad.snippets pad
      snippets.[0].Result |> Expect.equal "has result" (Some (Ok "val it: int = 2"))

    testCase "record error for snippet" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "bad code"
        |> ScratchPad.recordResult 1 (Error "FS0001: type mismatch")
      let snippets = ScratchPad.snippets pad
      snippets.[0].Result |> Expect.equal "has error" (Some (Error "FS0001: type mismatch"))

    testCase "unexecuted snippet has no result" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "pending"
      let snippets = ScratchPad.snippets pad
      snippets.[0].Result |> Expect.equal "no result" None
  ]

[<Tests>]
let scratchPadExportTests =
  testList "ScratchPad export" [

    testCase "export as fsx" <| fun _ ->
      let pad =
        ScratchPad.create "exploration"
        |> ScratchPad.addSnippet "let x = 1"
        |> ScratchPad.addSnippet "let y = x + 1"
      let fsx = ScratchPad.exportFsx pad
      fsx |> Expect.stringContains "has first" "let x = 1"
      fsx |> Expect.stringContains "has second" "let y = x + 1"
      fsx |> Expect.stringContains "has marker" "@sagefs-scratch"

    testCase "export empty pad" <| fun _ ->
      let fsx = ScratchPad.create "empty" |> ScratchPad.exportFsx
      fsx |> Expect.stringContains "has header" "@sagefs-scratch"

    testCase "promote successful snippet" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "let x = 42"
        |> ScratchPad.recordResult 1 (Ok "val x: int = 42")
      let promoted = ScratchPad.promoteSuccessful pad
      promoted |> Expect.hasLength "one promoted" 1
      promoted.[0] |> Expect.equal "the code" "let x = 42"

    testCase "promote skips failed snippets" <| fun _ ->
      let pad =
        ScratchPad.create "test"
        |> ScratchPad.addSnippet "good"
        |> ScratchPad.recordResult 1 (Ok "val it: string")
        |> ScratchPad.addSnippet "bad"
        |> ScratchPad.recordResult 2 (Error "type error")
      let promoted = ScratchPad.promoteSuccessful pad
      promoted |> Expect.hasLength "only good" 1
      promoted.[0] |> Expect.equal "good one" "good"
  ]
