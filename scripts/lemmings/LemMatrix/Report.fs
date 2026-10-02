/// Reading each run's summary.json back, and the table the matrix prints at the end.
module LemMatrix.Report

open System
open System.IO
open System.Text.Json
open LemScore.Types

type RowStatus =
  | Scored of Outcome
  | Skipped of reason: string
  /// run-lemming-cmd ended without leaving a summary.json (it refused, or died early).
  | Unscored of reason: string

type Row =
  { RunId: string
    Model: string
    Fixture: string
    Task: string
    Status: RowStatus
    Seconds: int
    Turns: int
    SageCalls: int
    SageErrors: int
    FellOverCount: int }

let private intAt (path: string list) (root: JsonElement) : int =
  let rec walk (el: JsonElement) (keys: string list) =
    match keys with
    | [] -> if el.ValueKind = JsonValueKind.Number then el.GetInt32() else 0
    | k :: rest ->
      match el.ValueKind with
      | JsonValueKind.Object ->
        match el.TryGetProperty k with
        | true, v -> walk v rest
        | _ -> 0
      | _ -> 0
  walk root path

let emptyRow (runId: string) (model: string) (fixture: string) (task: string) (status: RowStatus) : Row =
  { RunId = runId; Model = model; Fixture = fixture; Task = task; Status = status
    Seconds = 0; Turns = 0; SageCalls = 0; SageErrors = 0; FellOverCount = 0 }

/// Reads <root>/<runId>/out/summary.json into a row, or says why it could not.
let readSummary (root: string) (runId: string) (model: string) (fixture: string) (task: string) : Result<Row, string> =
  let path = Path.Combine(root, runId, "out", "summary.json")
  match File.Exists path with
  | false -> Error (sprintf "no summary.json at %s" path)
  | true ->
    try
      use doc = JsonDocument.Parse(File.ReadAllText path)
      let r = doc.RootElement
      let outcomeText = match r.TryGetProperty "outcome" with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() | _ -> ""
      match Outcome.tryParse (outcomeText |> Option.ofObj |> Option.defaultValue "") with
      | Error e -> Error e
      | Ok outcome ->
        let fell = match r.TryGetProperty "fellOver" with | true, a when a.ValueKind = JsonValueKind.Array -> a.GetArrayLength() | _ -> 0
        Ok { RunId = runId; Model = model; Fixture = fixture; Task = task; Status = Scored outcome
             Seconds = intAt [ "seconds" ] r; Turns = intAt [ "turns" ] r
             SageCalls = intAt [ "sagefsMcp"; "calls" ] r; SageErrors = intAt [ "sagefsMcp"; "errorCount" ] r
             FellOverCount = fell }
    with :? JsonException as ex -> Error (sprintf "summary.json is not JSON: %s" ex.Message)

let statusText (s: RowStatus) : string =
  match s with
  | Scored o -> Outcome.toString o
  | Skipped _ -> "Skipped"
  | Unscored _ -> "NoSummary"

let private pad (width: int) (text: string) : string =
  match text.Length >= width with
  | true -> text
  | false -> text + String(' ', width - text.Length)

let private shortFixture (fixture: string) : string =
  match fixture.StartsWith "sagefs-copy:" with
  | true -> "sagefs-copy"
  | false -> fixture

let renderTable (rows: Row list) : string =
  let header = [ "run"; "outcome"; "secs"; "turns"; "sagefs"; "errs"; "fell"; "fixture/task" ]
  let body =
    rows
    |> List.map (fun r ->
      [ r.RunId; statusText r.Status; string r.Seconds; string r.Turns; string r.SageCalls; string r.SageErrors
        string r.FellOverCount; sprintf "%s/%s" (shortFixture r.Fixture) r.Task ])
  let all = header :: body
  let widths = header |> List.mapi (fun i _ -> all |> List.map (fun row -> (List.item i row).Length) |> List.max)
  let line (cells: string list) = cells |> List.mapi (fun i c -> pad (List.item i widths) c) |> String.concat "  " |> fun s -> s.TrimEnd()
  let notes =
    rows
    |> List.choose (fun r ->
      match r.Status with
      | Skipped why -> Some (sprintf "  %s skipped: %s" r.RunId why)
      | Unscored why -> Some (sprintf "  %s no summary: %s" r.RunId why)
      | Scored _ -> None)
  [ yield line header
    yield String('-', (line header).Length)
    yield! body |> List.map line
    if not notes.IsEmpty then
      yield ""
      yield! notes ]
  |> String.concat "\n"

/// Outcome counts, in the closed set's own order, for the line under the table.
let tally (rows: Row list) : string =
  let counts =
    Outcome.all
    |> List.map (fun o -> o, rows |> List.filter (fun r -> r.Status = Scored o) |> List.length)
    |> List.filter (fun (_, n) -> n > 0)
    |> List.map (fun (o, n) -> sprintf "%s %d" (Outcome.toString o) n)
  let skipped = rows |> List.filter (fun r -> match r.Status with Skipped _ -> true | _ -> false) |> List.length
  let unscored = rows |> List.filter (fun r -> match r.Status with Unscored _ -> true | _ -> false) |> List.length
  let extra = [ if skipped > 0 then yield sprintf "Skipped %d" skipped
                if unscored > 0 then yield sprintf "NoSummary %d" unscored ]
  String.Join(", ", counts @ extra)
