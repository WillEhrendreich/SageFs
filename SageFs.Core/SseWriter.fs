module SageFs.SseWriter

open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Pure: format an SSE event string.
/// Handles data containing newlines per SSE spec (each line as separate data: field).
let formatSseEvent (eventType: string) (data: string) : string =
  if data.Contains("\n") then
    let dataLines = data.Split('\n') |> Array.map (sprintf "data: %s") |> String.concat "\n"
    sprintf "event: %s\n%s\n\n" eventType dataLines
  else
    sprintf "event: %s\ndata: %s\n\n" eventType data

/// Pure: format SSE event with multiline data
let formatSseEventMultiline (eventType: string) (lines: string list) : string =
  match lines with
  | [] -> sprintf "event: %s\n\n" eventType
  | _ ->
    let dataLines = lines |> List.map (sprintf "data: %s") |> String.concat "\n"
    sprintf "event: %s\n%s\n\n" eventType dataLines

/// Safely write bytes to a stream, returning Result instead of throwing
let trySendBytes (stream: Stream) (bytes: byte[]) : Task<Result<unit, string>> =
  task {
    try
      do! stream.WriteAsync(bytes)
      do! stream.FlushAsync()
      return Ok ()
    with ex ->
      return Error (sprintf "SSE write failed: %s" ex.Message)
  }

/// Format + send an SSE event, returning Result instead of throwing
let trySendSseEvent (stream: Stream) (eventType: string) (data: string) : Task<Result<unit, string>> =
  let text = formatSseEvent eventType data
  let bytes = Encoding.UTF8.GetBytes(text)
  trySendBytes stream bytes

/// Inject a SessionId field into a JSON object string. None = no change (backward compat).
let injectSessionId (sessionId: string option) (json: string) : string =
  match sessionId with
  | None -> json
  | Some sid ->
    if json.StartsWith("{") then
      sprintf """{"SessionId":"%s",%s""" sid (json.Substring(1))
    else json

/// Format a TestSummary as an SSE event string
let formatTestSummaryEvent (opts: JsonSerializerOptions) (sessionId: string option) (summary: Features.LiveTesting.TestSummary) : string =
  let json = JsonSerializer.Serialize(summary, opts) |> injectSessionId sessionId
  formatSseEvent "test_summary" json

/// Format a TestResultsBatchPayload as an SSE event string
let formatTestResultsBatchEvent (opts: JsonSerializerOptions) (sessionId: string option) (payload: Features.LiveTesting.TestResultsBatchPayload) : string =
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "test_results_batch" json

/// Format a FileAnnotations as an SSE event string
let formatFileAnnotationsEvent (opts: JsonSerializerOptions) (sessionId: string option) (annotations: Features.LiveTesting.FileAnnotations) : string =
  let json = JsonSerializer.Serialize(annotations, opts) |> injectSessionId sessionId
  formatSseEvent "file_annotations" json

// ── Bindings snapshot (CQRS: server-side parsing, push via SSE) ──

/// A single FSI binding tracked server-side
type FsiBinding = {
  Name: string
  TypeSig: string
  ShadowCount: int
}

// ── Binding parser: Option.bind pipeline (ROP) ──

/// Try to strip a prefix, returning the rest or None
let private tryStripPrefix (prefix: string) (s: string) =
  match s.Trim() with
  | t when t.StartsWith(prefix) -> Some (t.Substring(prefix.Length))
  | _ -> None

/// Strip "mutable " prefix if present (total function, always succeeds)
let private stripMutablePrefix (s: string) =
  match s with
  | t when t.StartsWith("mutable ") -> t.Substring(8)
  | t -> t

/// Split at first colon into (name, typeSig), or None
let private splitAtColon (s: string) =
  match s.IndexOf(':') with
  | i when i > 0 -> Some (s.Substring(0, i).Trim(), s.Substring(i + 1).Trim())
  | _ -> None

/// Strip trailing "= value" from a type signature
let private cleanTypeSig (typeSig: string) =
  match typeSig.LastIndexOf('=') with
  | i when i > 0 -> typeSig.Substring(0, i).Trim()
  | _ -> typeSig

/// Validate a binding name: skip "it" (expression results) and tuple patterns
let private validateBindingName (name: string, typeSig: string) =
  match name with
  | "it" -> None
  | n when n.Contains("(") -> None
  | _ -> Some (name, typeSig)

/// Parse a single FSI output line into (name, typeSig) via Option.bind pipeline
let private tryParseBinding (line: string) =
  line
  |> tryStripPrefix "val "
  |> Option.map stripMutablePrefix
  |> Option.bind splitAtColon
  |> Option.map (fun (name, ts) -> name, cleanTypeSig ts)
  |> Option.bind validateBindingName

/// Parse `val name : type = value` lines from FSI output.
/// Skips `val it` (expression results) and tuple patterns.
let parseBindingsFromOutput (output: string) : (string * string) array =
  output.Split('\n') |> Array.choose tryParseBinding

/// Accumulate parsed bindings into a running map, tracking shadow counts.
let accumulateBindings
  (existing: Map<string, FsiBinding>)
  (parsed: (string * string) array)
  : Map<string, FsiBinding> =
  parsed
  |> Array.fold (fun acc (name, typeSig) ->
    let count =
      match Map.tryFind name acc with
      | Some b -> b.ShadowCount + 1
      | None -> 1
    Map.add name { Name = name; TypeSig = typeSig; ShadowCount = count } acc
  ) existing

/// Format a bindings snapshot as an SSE event string
let formatBindingsSnapshotEvent (opts: JsonSerializerOptions) (sessionId: string option) (bindings: FsiBinding array) : string =
  let json = JsonSerializer.Serialize({| Bindings = bindings |}, opts) |> injectSessionId sessionId
  formatSseEvent "bindings_snapshot" json

/// Format a pipeline trace as an SSE event string
let formatPipelineTraceEvent (sessionId: string option) (traceJson: string) : string =
  let json = injectSessionId sessionId traceJson
  formatSseEvent "pipeline_trace" json
