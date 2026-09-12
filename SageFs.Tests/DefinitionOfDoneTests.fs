module SageFs.Tests.DefinitionOfDoneTests

open System
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip

let matrixPath =
  Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "quality", "definition-of-done.json"))

let requiredClients = set [ "dashboard"; "vscode"; "neovim" ]
let requiredCapabilities = set [ "hot-reload"; "live-testing"; "friction" ]
let allowedStatuses = set [ "verified"; "deferred"; "not-applicable" ]

let stringProperty (name: string) (row: JsonElement) =
  match row.TryGetProperty name with
  | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
  | _ -> ""

let validateMatrix (releaseReady: bool) (today: DateOnly) (json: string) =
  try
    use doc = JsonDocument.Parse json
    let rows = doc.RootElement.GetProperty("rows").EnumerateArray() |> Seq.toArray
    let errors = ResizeArray<string>()
    let ids = rows |> Array.map (stringProperty "id")
    ids
    |> Array.countBy id
    |> Array.filter (fun (id, count) -> String.IsNullOrWhiteSpace id || count > 1)
    |> Array.iter (fun (id, _) -> errors.Add(sprintf "duplicate or blank id: %s" id))
    let combinations =
      rows
      |> Array.map (fun row -> stringProperty "capability" row, stringProperty "client" row)
      |> Set.ofArray
    for capability in requiredCapabilities do
      for client in requiredClients do
        match combinations |> Set.contains (capability, client) with
        | true -> ()
        | false -> errors.Add(sprintf "missing obligation: %s/%s" capability client)
    for row in rows do
      let id = stringProperty "id" row
      let status = stringProperty "status" row
      match allowedStatuses |> Set.contains status with
      | true -> ()
      | false -> errors.Add(sprintf "%s has unknown status %s" id status)
      match status with
      | "verified" ->
        match String.IsNullOrWhiteSpace(stringProperty "evidence" row) with
        | true -> errors.Add(sprintf "%s needs executable evidence" id)
        | false -> ()
      | "deferred" ->
        let hasIssue =
          match row.TryGetProperty "issue" with
          | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32() > 0
          | _ -> false
        let expiry = stringProperty "expires" row
        match hasIssue with
        | true -> ()
        | false -> errors.Add(sprintf "%s needs a tracking issue" id)
        match DateOnly.TryParse expiry with
        | true, date when date >= today -> ()
        | _ -> errors.Add(sprintf "%s has an invalid or expired deferral" id)
        match releaseReady with
        | true -> errors.Add(sprintf "%s blocks release readiness" id)
        | false -> ()
      | "not-applicable" ->
        match String.IsNullOrWhiteSpace(stringProperty "reason" row) with
        | true -> errors.Add(sprintf "%s needs a not-applicable reason" id)
        | false -> ()
      | _ -> ()
    errors |> Seq.toList
  with ex ->
    [ sprintf "invalid matrix: %s" ex.Message ]

[<Tests>]
let definitionOfDoneTests =
  testList "Definition of Done matrix" [
    testCase "WHY — development matrix is structurally complete because every client and capability needs an owned obligation" <| fun () ->
      File.ReadAllText matrixPath
      |> validateMatrix false (DateOnly.FromDateTime DateTime.UtcNow)
      |> Expect.isEmpty "development matrix should be structurally valid"

    testCase "WHY — release readiness is green when no obligation is deferred" <| fun () ->
      let errors =
        File.ReadAllText matrixPath
        |> validateMatrix true (DateOnly.FromDateTime DateTime.UtcNow)
      errors
      |> List.exists (fun error -> error.Contains "blocks release readiness")
      |> Expect.isFalse "no current deferred journeys may block release readiness"

    testCase "WHY — every verified row cites resolvable evidence (an Actions run id or a commit SHA) and never 'green locally', because evidence nobody else can open is not evidence (roast-4 #12)" <| fun () ->
      use doc = JsonDocument.Parse(File.ReadAllText matrixPath)
      let rows = doc.RootElement.GetProperty("rows").EnumerateArray() |> Seq.toArray
      let runId = System.Text.RegularExpressions.Regex(@"\b\d{8,}\b")
      let sha = System.Text.RegularExpressions.Regex(@"\b[0-9a-f]{7,40}\b")
      let offenders =
        rows
        |> Array.filter (fun row -> stringProperty "status" row = "verified")
        |> Array.choose (fun row ->
          let id = stringProperty "id" row
          let evidence = stringProperty "evidence" row
          let saysGreenLocally = evidence.Contains("green locally", StringComparison.OrdinalIgnoreCase)
          let resolvable = runId.IsMatch evidence || sha.IsMatch evidence
          match saysGreenLocally, resolvable with
          | false, true -> None
          | true, _ -> Some (sprintf "%s: 'green locally' is not evidence" id)
          | false, false -> Some (sprintf "%s: no run id or commit SHA to resolve" id))
        |> Array.toList
      offenders |> Expect.isEmpty "every verified row must be resolvable by someone else"
  ]
