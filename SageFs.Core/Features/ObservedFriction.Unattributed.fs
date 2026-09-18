/// UnattributedFailure detector — Brief B3 (observed-friction-plan.md §c).
///
/// Target behavior: emit UnattributedFailure(rawTool, count) for every
/// event whose Tool name starts with "unknown" (the McpServer.fs:175-179
/// sentinel class — a required-argument parse failure that happens before
/// the tool name resolves) and whose outcome is EncounteredBlocker
/// InvalidRequest, aggregated by raw name. Confidence = Strong
/// (structurally certain — the tool literally could not be named). This
/// names the harvest's one real observed blocker: 4x
/// "unknown (missing argument)".
module SageFs.Features.ObservedFrictionUnattributed

open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes

/// The prefix McpServer.fs's `recordToolFailure` stamps when the tool name
/// could not be recovered from the arg-parse exception (e.g.
/// "unknown (missing 'code' argument)", "unknown (missing argument)",
/// "unknown"). Any event whose Tool starts with this prefix is, by
/// construction, an unattributed failure — not a real tool name.
[<Literal>]
let private unattributedPrefix = "unknown"

let private isUnattributedFailure (e: FrictionEvent) =
  match e.Outcome with
  | FrictionOutcome.EncounteredBlocker BlockerKind.InvalidRequest ->
    (ToolName.value e.Tool).StartsWith(unattributedPrefix, System.StringComparison.Ordinal)
  | _ -> false

/// Pure, IO-free: groups unattributed-failure events by their raw tool
/// string (the only identity an unnamed tool has) and emits one
/// UnattributedFailure per distinct raw string, scoped to the stream it
/// was detected on.
let detect : Detector =
  fun _cfg stream ->
    stream.Events
    |> List.filter isUnattributedFailure
    |> List.groupBy (fun e -> ToolName.value e.Tool)
    |> List.map (fun (rawTool, evs) ->
      { Signal = FrictionSignal.UnattributedFailure(rawTool, List.length evs)
        Scope = stream.Scope
        Confidence = SignalConfidence.Strong })
