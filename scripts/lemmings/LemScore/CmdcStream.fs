/// Reads the NDJSON stream `cmdc -p --output-format json` writes: one
/// `{"type":"event","event":{...}}` per line and a closing `{"type":"result",...}` line.
/// Shapes were read off real runs (see LemScore.Tests/samples) and the cmdc 1.73 bundle.
module LemScore.CmdcStream

open System
open System.Text.Json

type ToolOutcome =
  | ToolSucceeded
  | ToolErrored
  | ToolDeniedByPolicy
  | ToolNeverFinished

/// The closing `result` line.
type ResultLine =
  { Subtype: string
    StopReason: string option
    Error: string option
    FinalText: string
    DurationMs: int64
    InputTokens: int64
    OutputTokens: int64 }

type Event =
  | RunStart of sessionId: string
  | TurnStart of turn: int
  | ToolQueued of id: string * name: string * input: string
  | ToolCompleted of id: string * name: string * text: string
  | ToolErroredEvent of id: string * name: string * text: string
  | ToolDeniedEvent of id: string * name: string
  /// Command Code fixed up the model's tool input before sending it (tool_input_repaired,
  /// tool_input_coerced): a sign the tool's schema is easy for a weak model to get wrong.
  | InputRepaired of tool: string * rules: string
  | RunErrorEvent of text: string
  | RunEnd of stopReason: string * turnCount: int
  | Result of ResultLine
  /// A line that is valid and known, and carries nothing the harness scores
  /// (text deltas, thinking, usage, message boundaries).
  | Noise
  /// A line the parser could not read, with the reason. Counted, never silently dropped.
  | Unreadable of reason: string

let private tryProp (name: string) (el: JsonElement) : JsonElement option =
  match el.ValueKind with
  | JsonValueKind.Object ->
    match el.TryGetProperty name with
    | true, v -> Some v
    | _ -> None
  | _ -> None

let private strProp (name: string) (el: JsonElement) : string option =
  tryProp name el
  |> Option.bind (fun v -> match v.ValueKind with JsonValueKind.String -> Some (v.GetString()) | _ -> None)

let private intProp (name: string) (el: JsonElement) : int64 option =
  tryProp name el
  |> Option.bind (fun v -> match v.ValueKind with JsonValueKind.Number -> Some (v.GetInt64()) | _ -> None)

/// A tool result is an array of `{type:"text",text}` blocks (or, for errors, a string).
let private textOf (el: JsonElement) : string =
  match el.ValueKind with
  | JsonValueKind.String -> el.GetString() |> Option.ofObj |> Option.defaultValue ""
  | JsonValueKind.Array ->
    el.EnumerateArray()
    |> Seq.choose (strProp "text")
    |> String.concat "\n"
  | JsonValueKind.Null | JsonValueKind.Undefined -> ""
  | _ -> el.GetRawText()

let private parseEvent (ev: JsonElement) : Event =
  let kind = strProp "type" ev |> Option.defaultValue ""
  let str name = strProp name ev |> Option.defaultValue ""
  match kind with
  | "run_start" -> RunStart (str "sessionId")
  | "turn_start" -> TurnStart (intProp "turnNumber" ev |> Option.map int |> Option.defaultValue 0)
  | "tool_queued" ->
    let input = tryProp "input" ev |> Option.map _.GetRawText() |> Option.defaultValue "{}"
    ToolQueued (str "toolCallId", str "toolName", input)
  | "tool_completed" ->
    ToolCompleted (str "toolCallId", str "toolName", tryProp "result" ev |> Option.map textOf |> Option.defaultValue "")
  | "tool_errored" ->
    ToolErroredEvent (str "toolCallId", str "toolName", tryProp "error" ev |> Option.map textOf |> Option.defaultValue "")
  | "tool_denied" -> ToolDeniedEvent (str "toolCallId", str "toolName")
  | "tool_input_repaired" ->
    let rules =
      match tryProp "rulesFired" ev with
      | Some a when a.ValueKind = JsonValueKind.Array ->
        a.EnumerateArray() |> Seq.map (fun r -> r.ToString()) |> String.concat ","
      | _ -> "unspecified"
    InputRepaired (str "toolName", rules)
  | "tool_input_coerced" -> InputRepaired (str "toolName", sprintf "coerced from %s" (str "rawType"))
  | "run_error" ->
    let text =
      tryProp "error" ev
      |> Option.map (fun e -> match strProp "message" e with Some m -> m | None -> textOf e)
      |> Option.defaultValue "run_error with no error body"
    RunErrorEvent text
  | "run_end" ->
    let result = tryProp "result" ev
    let stop = result |> Option.bind (strProp "stopReason") |> Option.defaultValue ""
    let turns = result |> Option.bind (intProp "turnCount") |> Option.map int |> Option.defaultValue 0
    RunEnd (stop, turns)
  | "" -> Unreadable "event has no type"
  | _ -> Noise

let parseLine (line: string) : Event =
  match line.Trim() with
  | "" -> Noise
  | text ->
    try
      use doc = JsonDocument.Parse text
      let root = doc.RootElement
      match strProp "type" root with
      | Some "event" ->
        match tryProp "event" root with
        | Some ev -> parseEvent ev
        | None -> Unreadable "event line without an event body"
      | Some "result" ->
        let usage = tryProp "usage" root
        Result
          { Subtype = strProp "subtype" root |> Option.defaultValue ""
            StopReason = strProp "stopReason" root
            Error = strProp "error" root
            FinalText = strProp "finalText" root |> Option.defaultValue ""
            DurationMs = intProp "durationMs" root |> Option.defaultValue 0L
            InputTokens = usage |> Option.bind (intProp "inputTokens") |> Option.defaultValue 0L
            OutputTokens = usage |> Option.bind (intProp "outputTokens") |> Option.defaultValue 0L }
      | Some other -> Unreadable (sprintf "unknown line type '%s'" other)
      | None -> Unreadable "line has no type"
    with :? JsonException as ex -> Unreadable (sprintf "not JSON: %s" ex.Message)

/// One tool call, from the moment it was queued to its result.
type ToolCall =
  { Id: string
    Name: string
    Input: string
    Turn: int
    Outcome: ToolOutcome
    Text: string }

type RunStream =
  { SessionId: string option
    Turns: int
    Calls: ToolCall list
    RunErrors: string list
    /// (tool, rules) for every input Command Code had to repair.
    Repairs: (string * string) list
    EndStopReason: string option
    Result: ResultLine option
    Unreadable: string list }

let emptyStream : RunStream =
  { SessionId = None; Turns = 0; Calls = []; RunErrors = []; Repairs = []; EndStopReason = None; Result = None; Unreadable = [] }

let private updateCall (id: string) (f: ToolCall -> ToolCall) (calls: ToolCall list) : ToolCall list =
  calls |> List.map (fun c -> if c.Id = id then f c else c)

/// Folds one event into the stream. Calls are kept newest-first while folding.
let step (stream: RunStream) (event: Event) : RunStream =
  match event with
  | RunStart sid -> { stream with SessionId = Some sid }
  | TurnStart turn -> { stream with Turns = max stream.Turns turn }
  | ToolQueued (id, name, input) ->
    let call = { Id = id; Name = name; Input = input; Turn = stream.Turns; Outcome = ToolNeverFinished; Text = "" }
    { stream with Calls = call :: stream.Calls }
  | ToolCompleted (id, _, text) ->
    { stream with Calls = updateCall id (fun c -> { c with Outcome = ToolSucceeded; Text = text }) stream.Calls }
  | ToolErroredEvent (id, _, text) ->
    { stream with Calls = updateCall id (fun c -> { c with Outcome = ToolErrored; Text = text }) stream.Calls }
  | ToolDeniedEvent (id, name) ->
    let exists = stream.Calls |> List.exists (fun c -> c.Id = id)
    match exists with
    | true -> { stream with Calls = updateCall id (fun c -> { c with Outcome = ToolDeniedByPolicy }) stream.Calls }
    | false ->
      let call = { Id = id; Name = name; Input = "{}"; Turn = stream.Turns; Outcome = ToolDeniedByPolicy; Text = "" }
      { stream with Calls = call :: stream.Calls }
  | InputRepaired (tool, rules) -> { stream with Repairs = (tool, rules) :: stream.Repairs }
  | RunErrorEvent text -> { stream with RunErrors = text :: stream.RunErrors }
  | RunEnd (stop, turns) -> { stream with EndStopReason = Some stop; Turns = max stream.Turns turns }
  | Result r -> { stream with Result = Some r }
  | Noise -> stream
  | Unreadable reason -> { stream with Unreadable = reason :: stream.Unreadable }

let ofLines (lines: string seq) : RunStream =
  let folded = lines |> Seq.map parseLine |> Seq.fold step emptyStream
  { folded with Calls = List.rev folded.Calls; RunErrors = List.rev folded.RunErrors; Repairs = List.rev folded.Repairs; Unreadable = List.rev folded.Unreadable }

/// The prefix Command Code gives every tool a registered MCP server exposes.
let sagefsPrefix = "mcp__sagefs__"

let isSagefsCall (call: ToolCall) : bool = call.Name.StartsWith(sagefsPrefix, StringComparison.Ordinal)

let sagefsToolName (call: ToolCall) : string = call.Name.Substring sagefsPrefix.Length
