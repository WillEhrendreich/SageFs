/// Loading the captured samples, and building event lines in the exact shape cmdc writes.
module LemScore.Tests.Samples

open System
open System.IO
open System.Text.Json
open LemScore.CmdcStream

let private samplePath (name: string) : string =
  Path.Combine(AppContext.BaseDirectory, "samples", name)

let readSample (name: string) : string = File.ReadAllText(samplePath name)

let sampleLines (name: string) : string[] = File.ReadAllLines(samplePath name)

let sampleStream (name: string) : RunStream = sampleLines name |> ofLines

// ---- event lines, shaped like `cmdc -p --output-format json` -------------------------------

let private ev (inner: string) : string = sprintf """{"type":"event","event":%s}""" inner

/// Wraps an event body the way cmdc does.
let eventLine (inner: string) : string = ev inner

let private jsonString (text: string) : string = JsonSerializer.Serialize text

let runStart : string = ev """{"type":"run_start","sessionId":"s1"}"""

let turnStart (n: int) : string = ev (sprintf """{"type":"turn_start","turnNumber":%d}""" n)

let toolQueued (id: string) (name: string) (input: string) : string =
  ev (sprintf """{"type":"tool_queued","toolCallId":"%s","toolName":"%s","input":%s}""" id name input)

let toolCompleted (id: string) (name: string) (text: string) : string =
  ev (sprintf """{"type":"tool_completed","toolCallId":"%s","toolName":"%s","result":[{"type":"text","text":%s}]}""" id name (jsonString text))

let toolErrored (id: string) (name: string) (text: string) : string =
  ev (sprintf """{"type":"tool_errored","toolCallId":"%s","toolName":"%s","error":%s}""" id name (jsonString text))

let toolDenied (id: string) (name: string) : string =
  ev (sprintf """{"type":"tool_denied","toolCallId":"%s","toolName":"%s"}""" id name)

let runEnd (stop: string) (turns: int) : string =
  ev (sprintf """{"type":"run_end","result":{"finalText":"x","stopReason":"%s","turnCount":%d}}""" stop turns)

let resultLine (subtype: string) (error: string option) (finalText: string) : string =
  let err = error |> Option.map (fun e -> sprintf ""","error":%s""" (jsonString e)) |> Option.defaultValue ""
  sprintf """{"type":"result","subtype":"%s","stopReason":"end_turn","usage":{"inputTokens":10,"outputTokens":2},"durationMs":1234,"finalText":%s%s}""" subtype (jsonString finalText) err

/// One SageFs MCP call that queued, ran and completed.
let sagefsCall (n: int) (tool: string) (input: string) (text: string) : string list =
  let id = sprintf "c%d" n
  [ toolQueued id ("mcp__sagefs__" + tool) input
    toolCompleted id ("mcp__sagefs__" + tool) text ]

let shellCall (n: int) (command: string) (text: string) : string list =
  let id = sprintf "c%d" n
  let input = sprintf """{"command":%s}""" (jsonString command)
  [ toolQueued id "shell_command" input
    toolCompleted id "shell_command" text ]

/// A turn-numbered stream: calls grouped per turn, then the closing lines.
let stream (turns: string list list) (closing: string list) : RunStream =
  let lines =
    [ yield runStart
      for i, calls in List.indexed turns do
        yield turnStart (i + 1)
        yield! calls
      yield! closing ]
  ofLines lines

let finishedOk : string list =
  [ runEnd "end_turn" 3; resultLine "success" None "done" ]
