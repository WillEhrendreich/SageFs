/// What the editor driver saw that the cmdc stream cannot show: calls that failed or were
/// refused, a lemming that never touched the editor, a session that never reached the
/// dashboard. Written to out/fellover.extra.json, which `LemScore score` folds into the
/// summary's fellOver list. Pure parsing of the numbered transcripts, plus one writer.
module LemDrive.FellOver

open System
open System.IO
open System.Text.Json

/// The most failed-call entries one run reports, so a lemming that loops on one mistake
/// does not bury the rest.
[<Literal>]
let MostCallEntries = 8

/// How much of a transcript is quoted as evidence.
[<Literal>]
let EvidenceWidth = 240

/// How a transcript's second line labels the call.
type CallResult =
  | CallOk
  | CallFailed
  | CallRefused
  | CallUsage

module CallResult =
  let tryParse (label: string) : CallResult option =
    match label with
    | "ok" -> Some CallOk
    | "failed" -> Some CallFailed
    | "refused" -> Some CallRefused
    | "usage" -> Some CallUsage
    | _ -> None

/// One driver call as its NNN.txt records it.
type Call =
  { Number: string
    Command: string
    Result: CallResult
    Body: string }

/// Reads "# <command>\n# <timestamp>  <label>\n<body>".
let parseTranscript (number: string) (text: string) : Call option =
  match text.Split('\n') |> List.ofArray with
  | first :: second :: rest when first.StartsWith "# " && second.StartsWith "# " ->
    let label = second.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.defaultValue ""
    CallResult.tryParse label
    |> Option.map (fun r -> { Number = number; Command = first.Substring 2; Result = r; Body = String.Join("\n", rest).Trim() })
  | _ -> None

let readCalls (screensDir: string) : Call list =
  match Directory.Exists screensDir with
  | false -> []
  | true ->
    Directory.GetFiles(screensDir, "*.txt")
    |> Array.sort
    |> Array.choose (fun f -> parseTranscript (Path.GetFileNameWithoutExtension f) (File.ReadAllText f))
    |> List.ofArray

type Entry =
  { Stage: string
    Symptom: string
    Evidence: string }

let private clip (text: string) : string =
  let flat = text.Replace('\n', ' ')
  match flat.Length > EvidenceWidth with
  | true -> flat.Substring(0, EvidenceWidth) + " ..."
  | false -> flat

/// The entries for a run. `expectSession` is whether the task should have made a session;
/// `sessionsOnDashboard` is how many sessions the shared daemon listed under the run
/// directory before cleanup.
let entries (calls: Call list) (expectSession: bool) (sessionsOnDashboard: int) : Entry list =
  let callEntries =
    calls
    |> List.choose (fun c ->
      match c.Result with
      | CallFailed -> Some { Stage = "Editor"; Symptom = sprintf "an editor call failed: %s" c.Command; Evidence = sprintf "%s: %s" c.Number (clip c.Body) }
      | CallRefused -> Some { Stage = "ToolSurface"; Symptom = sprintf "the driver refused a command that controls the shared daemon: %s" c.Command; Evidence = sprintf "%s: %s" c.Number (clip c.Body) }
      | CallUsage -> Some { Stage = "ToolSurface"; Symptom = sprintf "the lemming misused an editor command: %s" c.Command; Evidence = sprintf "%s: %s" c.Number (clip c.Body) }
      | CallOk -> None)
    |> List.truncate MostCallEntries
  let unused =
    match calls with
    | [] -> [ { Stage = "Adoption"; Symptom = "the lemming never drove the editor"; Evidence = "no calls in out/screens" } ]
    | _ -> []
  let missingSession =
    match expectSession, sessionsOnDashboard with
    | true, 0 -> [ { Stage = "SessionCreate"; Symptom = "no session from this run directory reached the shared dashboard"; Evidence = "the daemon listed no session under the run directory before cleanup" } ]
    | _ -> []
  unused @ missingSession @ callEntries

let toJson (items: Entry list) : string =
  use stream = new MemoryStream()
  use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
  w.WriteStartArray()
  for e in items do
    w.WriteStartObject()
    w.WriteString("stage", e.Stage)
    w.WriteString("symptom", e.Symptom)
    w.WriteString("evidence", e.Evidence)
    w.WriteEndObject()
  w.WriteEndArray()
  w.Flush()
  Text.Encoding.UTF8.GetString(stream.ToArray())

/// `LemDrive fellover --run-dir D --expect-session true|false --sessions N`
let write (runDir: string) (expectSession: bool) (sessionsOnDashboard: int) : int =
  let calls = readCalls (Path.Combine(runDir, "out", "screens"))
  let items = entries calls expectSession sessionsOnDashboard
  File.WriteAllText(Path.Combine(runDir, "out", "fellover.extra.json"), toJson items)
  List.length calls
