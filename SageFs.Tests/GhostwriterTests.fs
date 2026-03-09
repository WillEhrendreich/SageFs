module SageFs.Tests.GhostwriterTests

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.Ghostwriter

[<Tests>]
let suggestionTests =
  testList "Ghostwriter suggestions" [

    testCase "list binding suggests List operations" <| fun _ ->
      let bindings = [ { Name = "items"; TypeSig = "int list"; Value = Some "[1;2;3]" } ]
      let suggestions = suggest bindings
      suggestions |> List.exists (fun s -> s.Code.Contains("List."))
      |> Expect.isTrue "has List suggestion"

    testCase "option binding suggests Option operations" <| fun _ ->
      let bindings = [ { Name = "result"; TypeSig = "string option"; Value = Some "Some \"hi\"" } ]
      let suggestions = suggest bindings
      suggestions |> List.exists (fun s -> s.Code.Contains("Option."))
      |> Expect.isTrue "has Option suggestion"

    testCase "map binding suggests Map operations" <| fun _ ->
      let bindings = [ { Name = "lookup"; TypeSig = "Map<string, int>"; Value = None } ]
      let suggestions = suggest bindings
      suggestions |> List.exists (fun s -> s.Code.Contains("Map."))
      |> Expect.isTrue "has Map suggestion"

    testCase "result binding suggests Result operations" <| fun _ ->
      let bindings = [ { Name = "outcome"; TypeSig = "Result<int, string>"; Value = None } ]
      let suggestions = suggest bindings
      suggestions |> List.exists (fun s -> s.Code.Contains("Result."))
      |> Expect.isTrue "has Result suggestion"

    testCase "string binding suggests String operations" <| fun _ ->
      let bindings = [ { Name = "name"; TypeSig = "string"; Value = None } ]
      let suggestions = suggest bindings
      suggestions |> List.exists (fun s ->
        s.Code.Contains("String.") || s.Code.Contains(".Length"))
      |> Expect.isTrue "has string suggestion"

    testCase "int binding suggests arithmetic" <| fun _ ->
      let bindings = [ { Name = "count"; TypeSig = "int"; Value = None } ]
      let suggestions = suggest bindings
      suggestions |> List.isEmpty |> Expect.isFalse "has suggestions"

    testCase "no bindings yields empty" <| fun _ ->
      let suggestions = suggest []
      suggestions |> Expect.hasLength "empty" 0

    testCase "suggestions have explanations" <| fun _ ->
      let bindings = [ { Name = "xs"; TypeSig = "int list"; Value = None } ]
      let suggestions = suggest bindings
      suggestions |> List.forall (fun s -> s.Explanation.Length > 0)
      |> Expect.isTrue "all have explanations"
  ]

[<Tests>]
let rankingTests =
  testList "Ghostwriter ranking" [

    testCase "pipeline continuations rank highest" <| fun _ ->
      let bindings = [ { Name = "items"; TypeSig = "int list"; Value = None } ]
      let suggestions = suggest bindings
      match suggestions with
      | [] -> failtest "expected suggestions"
      | first :: _ ->
        first.Code |> Expect.stringContains "pipeline" "|>"

    testCase "multiple bindings produce combined suggestions" <| fun _ ->
      let bindings = [
        { Name = "xs"; TypeSig = "int list"; Value = None }
        { Name = "name"; TypeSig = "string"; Value = None }
      ]
      let suggestions = suggest bindings
      (suggestions.Length > 2) |> Expect.isTrue "multiple suggestions"
  ]

[<Tests>]
let formatTests =
  testList "Ghostwriter formatting" [

    testCase "format suggestion" <| fun _ ->
      let s = { Code = "xs |> List.length"; Explanation = "Count items"; Confidence = 0.9 }
      let text = formatSuggestion s
      text |> Expect.stringContains "has code" "List.length"
      text |> Expect.stringContains "has explanation" "Count items"

    testCase "format panel" <| fun _ ->
      let suggestions = [
        { Code = "xs |> List.head"; Explanation = "Get first item"; Confidence = 0.8 }
        { Code = "xs |> List.length"; Explanation = "Count items"; Confidence = 0.7 }
      ]
      let panel = formatPanel suggestions
      panel |> Expect.stringContains "has header" "Suggestions"
      panel |> Expect.stringContains "has first" "List.head"
  ]
