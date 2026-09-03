module SageFs.SessionEvents

open System.IO
open System.Text
open System.Text.Json
open SageFs.WarmUp

/// Typed session-scoped events emitted on the SSE stream.
type SessionEvent =
  | WarmupContextSnapshot of sessionId: string * context: WarmupContext
  | HotReloadSnapshot of sessionId: string * watchedFiles: string list
  | HotReloadFileToggled of sessionId: string * file: string * watched: bool
  | SessionActivated of sessionId: string
  | SessionCreated of sessionId: string * projectNames: string list
  | SessionStopped of sessionId: string
  | WorkflowSwitching of sessionId: string * fromLabel: string * toLabel: string
  | WorkflowSwitched of sessionId: string * label: string * replCapability: string * hotReloadActive: bool

/// SSE event type name for all session events.
let sessionEventType = "session"

/// JSON subtype discriminator values for session events.
module SessionEventSubtype =
  let warmupContextSnapshot = "warmup_context_snapshot"
  let hotReloadSnapshot = "hotreload_snapshot"
  let hotReloadFileToggled = "hotreload_file_toggled"
  let sessionActivated = "session_activated"
  let sessionCreated = "session_created"
  let sessionStopped = "session_stopped"
  let workflowSwitching = "workflow_switching"
  let workflowSwitched = "workflow_switched"

/// Serialize a SessionEvent to a JSON string.
let serializeSessionEvent (evt: SessionEvent) : string =
  use ms = new MemoryStream()
  use w = new Utf8JsonWriter(ms)
  let writeStr (k: string) (v: string) = w.WriteString(k, v)
  let writeInt (k: string) (v: int) = w.WriteNumber(k, v)
  let writeInt64 (k: string) (v: int64) = w.WriteNumber(k, v)
  let writeBool (k: string) (v: bool) = w.WriteBoolean(k, v)
  let writeFloat (k: string) (v: float) = w.WriteNumber(k, v)
  w.WriteStartObject()
  match evt with
  | WarmupContextSnapshot(sid, ctx) ->
    writeStr "type" SessionEventSubtype.warmupContextSnapshot
    writeStr "sessionId" sid
    w.WritePropertyName("context")
    w.WriteStartObject()
    writeInt "sourceFilesScanned" ctx.SourceFilesScanned
    writeInt64 "warmupDurationMs" (WarmupContext.totalDurationMs ctx)
    w.WritePropertyName("phaseTiming")
    w.WriteStartObject()
    writeInt64 "scanSourceFilesMs" ctx.PhaseTiming.ScanSourceFilesMs
    writeInt64 "scanAssembliesMs" ctx.PhaseTiming.ScanAssembliesMs
    writeInt64 "openNamespacesMs" ctx.PhaseTiming.OpenNamespacesMs
    writeInt64 "totalMs" ctx.PhaseTiming.TotalMs
    w.WriteEndObject()
    w.WritePropertyName("assembliesLoaded")
    w.WriteStartArray()
    for a in ctx.AssembliesLoaded do
      w.WriteStartObject()
      writeStr "name" a.Name
      writeStr "path" a.Path
      writeInt "namespaceCount" a.NamespaceCount
      writeInt "moduleCount" a.ModuleCount
      w.WriteEndObject()
    w.WriteEndArray()
    w.WritePropertyName("namespacesOpened")
    w.WriteStartArray()
    for ns in ctx.NamespacesOpened do
      w.WriteStartObject()
      writeStr "name" ns.Name
      writeBool "isModule" (OpenableKind.toBool ns.Kind)
      writeStr "source" ns.Source
      writeFloat "durationMs" ns.DurationMs
      w.WriteEndObject()
    w.WriteEndArray()
    w.WritePropertyName("failedOpens")
    w.WriteStartArray()
    for f in ctx.FailedOpens do
      w.WriteStartObject()
      writeStr "name" f.Name
      writeBool "isModule" (OpenableKind.toBool f.Kind)
      writeStr "error" f.ErrorMessage
      writeInt "retryCount" f.RetryCount
      w.WritePropertyName("diagnostics")
      w.WriteStartArray()
      for d in f.Diagnostics do
        w.WriteStartObject()
        writeStr "message" d.Message
        writeStr "severity" d.Severity
        writeInt "errorNumber" d.ErrorNumber
        match d.FileName with
        | Some fn -> writeStr "fileName" fn
        | None -> ()
        writeInt "startLine" d.StartLine
        writeInt "endLine" d.EndLine
        writeInt "startColumn" d.StartColumn
        writeInt "endColumn" d.EndColumn
        w.WriteEndObject()
      w.WriteEndArray()
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject()
  | HotReloadSnapshot(sid, files) ->
    writeStr "type" SessionEventSubtype.hotReloadSnapshot
    writeStr "sessionId" sid
    w.WritePropertyName("watchedFiles")
    w.WriteStartArray()
    for f in files do w.WriteStringValue(f)
    w.WriteEndArray()
  | HotReloadFileToggled(sid, file, watched) ->
    writeStr "type" SessionEventSubtype.hotReloadFileToggled
    writeStr "sessionId" sid
    writeStr "file" file
    writeBool "watched" watched
  | SessionActivated sid ->
    writeStr "type" SessionEventSubtype.sessionActivated
    writeStr "sessionId" sid
  | SessionCreated(sid, projects) ->
    writeStr "type" SessionEventSubtype.sessionCreated
    writeStr "sessionId" sid
    w.WritePropertyName("projectNames")
    w.WriteStartArray()
    for p in projects do w.WriteStringValue(p)
    w.WriteEndArray()
  | SessionStopped sid ->
    writeStr "type" SessionEventSubtype.sessionStopped
    writeStr "sessionId" sid
  | WorkflowSwitching(sid, fromLabel, toLabel) ->
    writeStr "type" SessionEventSubtype.workflowSwitching
    writeStr "sessionId" sid
    writeStr "fromWorkflow" fromLabel
    writeStr "toWorkflow" toLabel
  | WorkflowSwitched(sid, label, replCap, hotReload) ->
    writeStr "type" SessionEventSubtype.workflowSwitched
    writeStr "sessionId" sid
    writeStr "workflowLabel" label
    writeStr "replCapability" replCap
    writeBool "hotReloadActive" hotReload
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(ms.ToArray())

/// Format a SessionEvent as a complete SSE frame.
let formatSessionSseEvent (evt: SessionEvent) : string =
  let json = serializeSessionEvent evt
  SseWriter.formatSseEvent sessionEventType json

/// Bridge: convert HotReloadState to a snapshot event.
let hotReloadToSnapshot (sessionId: string) (state: HotReloadState.T) : SessionEvent =
  let sorted = state.Watched |> Set.toList |> List.sort
  HotReloadSnapshot(sessionId, sorted)

/// Bridge: create a hotreload file toggle event.
let hotReloadToggleEvent (sessionId: string) (file: string) (watched: bool) : SessionEvent =
  HotReloadFileToggled(sessionId, file, watched)
