/// summary.json: what one lemming run did, measured by the harness, never self-reported.
module LemScore.Summary

open System
open System.IO
open System.Text
open System.Text.Json
open LemScore.Types
open LemScore.CmdcStream
open LemScore.Classify
open LemScore.SharedDaemon

/// A daemon reading taken by the harness (start of run, end of run).
type DaemonSnapshot =
  { Pressure: string
    AvailableBytes: int64
    ActiveLeases: int }

type SummaryInput =
  { Id: string
    Model: string
    Harness: Harness
    SagefsVersion: string
    Task: string
    Seconds: int
    Facts: RunFacts
    Teardown: CmdcTeardown
    DaemonStart: DaemonSnapshot option
    DaemonEnd: DaemonSnapshot option
    DashboardUrl: string
    ChangedFiles: string list
    Extra: FellOver list }

/// How many characters of a SageFs error and of the lemming's final words are kept.
let errorTextLimit = 600
let finalTextLimit = 900

let private clip (limit: int) (text: string) : string =
  match text.Length > limit with
  | true -> text.Substring(0, limit) + "..."
  | false -> text

let private snapshotJson (w: Utf8JsonWriter) (name: string) (s: DaemonSnapshot option) =
  match s with
  | None -> w.WriteNull name
  | Some snap ->
    w.WriteStartObject name
    w.WriteString("memoryPressure", snap.Pressure)
    w.WriteNumber("availableBytes", snap.AvailableBytes)
    w.WriteNumber("activeLeases", snap.ActiveLeases)
    w.WriteEndObject()

let private oracleName (o: OracleVerdict) : string =
  match o with
  | OracleNotRun -> "not-run"
  | OraclePassed -> "passed"
  | OracleFailed _ -> "failed"

let private leaseTools =
  set [ "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"; "release_work_lease"; "acquire_claim"; "release_claim" ]

let private createTools =
  set [ "create_project_session"; "create_solution_session"; "create_bare_session" ]

let render (input: SummaryInput) (assessment: Assessment) : string =
  use stream = new MemoryStream()
  let options = JsonWriterOptions(Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
  use w = new Utf8JsonWriter(stream, options)
  let facts = input.Facts
  let calls = facts.Stream.Calls
  let sage = sagefsCalls facts.Stream
  let sageErrors = sage |> List.filter isSagefsError
  w.WriteStartObject()
  w.WriteString("id", input.Id)
  w.WriteString("model", input.Model)
  w.WriteString("harness", Harness.toString input.Harness)
  w.WriteString("sagefsVersion", input.SagefsVersion)
  w.WriteString("task", input.Task)
  w.WriteString("outcome", Outcome.toString assessment.Outcome)
  w.WriteString("outcomeReason", assessment.Reason)
  match assessment.Provider with
  | Some kind -> w.WriteString("providerError", ProviderKind.toString kind)
  | None -> w.WriteNull "providerError"
  w.WriteNumber("seconds", input.Seconds)
  w.WriteNumber("turns", facts.Stream.Turns)
  w.WriteNumber("cmdcExit", facts.CmdcExit)
  // tool calls by name
  w.WriteStartObject "toolCalls"
  calls
  |> List.countBy _.Name
  |> List.sortByDescending snd
  |> List.iter (fun (name, n) -> w.WriteNumber(name, n))
  w.WriteEndObject()
  // SageFs MCP
  w.WriteStartObject "sagefsMcp"
  w.WriteNumber("calls", sage.Length)
  w.WriteNumber("errorCount", sageErrors.Length)
  w.WriteStartArray "callSequence"
  sage |> List.iter (fun c -> w.WriteStringValue(sprintf "%d:%s%s" c.Turn (sagefsToolName c) (if isSagefsError c then "!" else "")))
  w.WriteEndArray()
  w.WriteStartArray "errors"
  sageErrors
  |> List.iter (fun c ->
    w.WriteStartObject()
    w.WriteString("tool", sagefsToolName c)
    w.WriteNumber("turn", c.Turn)
    w.WriteString("text", clip errorTextLimit c.Text)
    w.WriteEndObject())
  w.WriteEndArray()
  w.WriteEndObject()
  // oracle
  w.WriteStartObject "oracle"
  w.WriteBoolean("ran", facts.Oracle <> OracleNotRun)
  w.WriteBoolean("passed", facts.Oracle = OraclePassed)
  w.WriteString("verdict", oracleName facts.Oracle)
  match facts.Oracle with
  | OracleFailed code -> w.WriteNumber("exit", code)
  | OraclePassed | OracleNotRun -> w.WriteNull "exit"
  w.WriteEndObject()
  // sessions, leases, residue
  w.WriteStartObject "residue"
  w.WriteNumber("sessionsCreatedByCalls", sage |> List.filter (fun c -> createTools.Contains(sagefsToolName c) && not (isSagefsError c)) |> List.length)
  w.WriteStartArray "sessionsLeftBehind"
  facts.ResidueSessions |> List.iter w.WriteStringValue
  w.WriteEndArray()
  w.WriteStartArray "leaseCalls"
  sage
  |> List.filter (fun c -> leaseTools.Contains(sagefsToolName c))
  |> List.iter (fun c -> w.WriteStringValue(sprintf "%d:%s%s" c.Turn (sagefsToolName c) (if isSagefsError c then "!" else "")))
  w.WriteEndArray()
  w.WriteEndObject()
  w.WriteStartObject "teardown"
  w.WriteString("cmdc", CmdcTeardown.toString input.Teardown)
  w.WriteString("sessionCleanup", Cleanup.toString facts.Cleanup)
  w.WriteEndObject()
  w.WriteString("dashboardUrl", input.DashboardUrl)
  w.WriteStartObject "daemon"
  snapshotJson w "start" input.DaemonStart
  snapshotJson w "end" input.DaemonEnd
  w.WriteEndObject()
  w.WriteStartObject "verification"
  w.WriteStartArray "changedFiles"
  input.ChangedFiles |> List.iter w.WriteStringValue
  w.WriteEndArray()
  w.WriteNumber("unreadableStreamLines", facts.Stream.Unreadable.Length)
  w.WriteEndObject()
  w.WriteStartArray "fellOver"
  (assessment.FellOver @ input.Extra)
  |> List.iter (fun f ->
    w.WriteStartObject()
    w.WriteString("stage", Stage.toString f.Stage)
    w.WriteString("symptom", f.Symptom)
    w.WriteString("evidence", f.Evidence)
    w.WriteEndObject())
  w.WriteEndArray()
  w.WriteString("finalText", clip finalTextLimit (facts.Stream.Result |> Option.map _.FinalText |> Option.defaultValue ""))
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(stream.ToArray())

/// `[ {"stage":"Editor","symptom":"...","evidence":"..."} ]`, written by an editor driver
/// that saw something the cmdc stream cannot show. Unknown stages are refused by name.
let parseExtraFellOver (json: string) : Result<FellOver list, string> =
  try
    use doc = JsonDocument.Parse json
    match doc.RootElement.ValueKind with
    | JsonValueKind.Array ->
      doc.RootElement.EnumerateArray()
      |> Seq.map (fun el ->
        let get (name: string) =
          match el.TryGetProperty name with
          | true, v when v.ValueKind = JsonValueKind.String -> Ok (v.GetString())
          | _ -> Error (sprintf "extra fellOver entry has no string '%s'" name)
        match get "stage", get "symptom", get "evidence" with
        | Ok s, Ok sym, Ok ev -> Stage.tryParse s |> Result.map (fun stage -> { Stage = stage; Symptom = sym; Evidence = ev })
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e)
      |> Seq.toList
      |> List.fold (fun acc r -> match acc, r with
                                 | Ok xs, Ok x -> Ok (xs @ [ x ])
                                 | (Error _ as e), _ -> e
                                 | _, Error e -> Error e) (Ok [])
    | _ -> Error "extra fellOver file must be a JSON array"
  with :? JsonException as ex -> Error (sprintf "extra fellOver file is not JSON: %s" ex.Message)
