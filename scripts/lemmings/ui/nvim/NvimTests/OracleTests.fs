module NvimTests.OracleTests

/// WHY: the generic scorer writes "never called a SageFs MCP tool" for every lemming that did not
/// call MCP. A Neovim lemming has none on purpose, so the line is noise that buries the real findings.
open System.Text.Json.Nodes
open Expecto
open Expecto.Flip
open LemDrive

let private summaryWith (findings: string) : JsonObject =
  match JsonNode.Parse(sprintf "{ \"fellOver\": %s }" findings) with
  | :? JsonObject as o -> o
  | _ -> failwith "not an object"

/// Reads the summary back through a JSON document, which has no nulls to dereference.
let private fellOver (summary: JsonObject) : (string * string) list =
  use doc = System.Text.Json.JsonDocument.Parse(summary.ToJsonString())
  match doc.RootElement.TryGetProperty "fellOver" with
  | true, arr when arr.ValueKind = System.Text.Json.JsonValueKind.Array ->
    [ for f in arr.EnumerateArray() ->
        let text (name: string) = match f.TryGetProperty name with | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
        text "stage", text "evidence" ]
  | _ -> []

let private stages (summary: JsonObject) : string list = fellOver summary |> List.map fst

[<Tests>]
let tests =
  testList "the Neovim summary's fellOver list" [
    testCase "the MCP-only registration finding is removed and the real ones stay in order" <| fun _ ->
      let s =
        summaryWith """[
          { "stage": "Registration", "symptom": "never called a SageFs MCP tool", "evidence": "x" },
          { "stage": "Editor", "symptom": "the driver refused a command", "evidence": "call 3" },
          { "stage": "Oracle", "symptom": "the oracle failed (exit 1)", "evidence": "y" } ]"""
      NvimOracle.dropMcpOnlyFindings s
      stages s |> Expect.equal "kept" [ "Editor"; "Oracle" ]

    testCase "a Registration finding about something else is kept" <| fun _ ->
      let s = summaryWith """[ { "stage": "Registration", "symptom": "the server was refused", "evidence": "x" } ]"""
      NvimOracle.dropMcpOnlyFindings s
      stages s |> Expect.equal "kept" [ "Registration" ]

    testCase "the MCP-only clauses are cut from the budget finding and its turn count stays" <| fun _ ->
      let s =
        summaryWith """[ { "stage": "Budget", "symptom": "ran out of turns or time before finishing",
                          "evidence": "40 turns used; first SageFs call at turn never; first successful eval at turn never" } ]"""
      NvimOracle.dropMcpOnlyFindings s
      fellOver s |> Expect.equal "the budget finding keeps its turn count" [ "Budget", "40 turns used" ]

    testCase "a summary with no fellOver is left alone" <| fun _ ->
      let s = JsonObject()
      NvimOracle.dropMcpOnlyFindings s
      s.Count |> Expect.equal "still empty" 0
  ]
