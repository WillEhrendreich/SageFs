namespace SageFs.Server

open System.Text.Json
open System.Text.Json.Serialization
open SageFs

/// The SSE `event:` name a case rides on. Editor clients (the in-repo VS Code
/// extension, and the external sagefs.nvim plugin) dispatch on this outer
/// name — pinned by SseParityTests — so it stays "state"/"session" even
/// though the F# side now has ONE type instead of two (roast-5 §1).
[<RequireQualifiedAccess>]
type SseChannel =
  | State
  | Session

/// Unified SSE event vocabulary for daemon -> editor/dashboard push
/// notifications. Replaces the formerly-separate DaemonStateChange
/// ("state" channel) and SessionEvents.SessionEvent ("session" channel)
/// types: the same worker occurrence (e.g. a hot-reload toggle) used to be
/// represented by two unrelated DUs, serialized by two different ad hoc
/// techniques (sprintf string-templating vs. a hand-rolled Utf8JsonWriter),
/// and SseWriter had to track "two event types" separately. Now there is one
/// DU, one classifier (`SseEvent.channel`), and one serializer
/// (`SseEvent.toJson`).
type SseEvent =
  // ── State channel (was DaemonStateChange) — thin, mostly-global notifications ──
  | SessionProgress
  | SessionReady of sessionId: WorkerProtocol.SessionId
  | SessionSwitched of sessionId: WorkerProtocol.SessionId
  | HotReloadChanged of sessionId: WorkerProtocol.SessionId
  | FileReloaded of sessionId: WorkerProtocol.SessionId * path: string
  | SessionFaulted of sessionId: WorkerProtocol.SessionId * error: string
  | ModelChanged of outputCount: int * diagCount: int
  | WarmupProgress of sessionId: WorkerProtocol.SessionId * step: int * total: int * message: string
  | SystemAlarm of phase: string * message: string
  /// The daemon's cohort changed: someone joined, left, claimed, released or
  /// landed. Global like `SystemAlarm`. The dashboard needs it to redraw the
  /// cohort panel, which only shows while members are present; before this
  /// nothing pushed on a cohort change, so the panel went stale until some
  /// unrelated event happened to arrive.
  | CohortChanged
  // ── Session channel (was SessionEvents.SessionEvent) — rich, session-scoped snapshots ──
  | WarmupContextSnapshot of sessionId: string * context: WarmupContext
  | HotReloadSnapshot of sessionId: string * watchedFiles: string list
  | HotReloadFileToggled of sessionId: string * file: string * watched: bool
  | SessionActivated of sessionId: string
  | SessionCreated of sessionId: string * projectNames: string list
  | SessionStopped of sessionId: string
  | WorkflowSwitching of sessionId: string * fromLabel: string * toLabel: string
  | WorkflowSwitched of sessionId: string * label: string * replCapability: string * hotReloadActive: bool
  /// A session's `SessionHealth` verdict (`SageFs.SessionHealth.classify`)
  /// changed since the last one pushed for it — the SSE half of roast-8 §1:
  /// `/health` and `/api/sessions` compute this verdict on every GET, but
  /// nothing pushed it, so a session going Healthy -> Degraded mid-session
  /// was invisible to every connected client until an unrelated refetch.
  /// Session-scoped like `WarmupContextSnapshot`/`HotReloadSnapshot`, so it
  /// rides the "session" channel and gets the same connect-time replay.
  | SessionHealthChanged of sessionId: string * health: SessionHealth

module SseEvent =
  /// Which SSE channel a case rides on. Exhaustive — a new case must be
  /// classified here before it compiles, so the channel split can never
  /// silently drift out of sync with the case list again.
  let channel = function
    | SessionProgress
    | SessionReady _
    | SessionSwitched _
    | HotReloadChanged _
    | FileReloaded _
    | SessionFaulted _
    | ModelChanged _
    | WarmupProgress _
    | SystemAlarm _
    | CohortChanged -> SseChannel.State
    | WarmupContextSnapshot _
    | HotReloadSnapshot _
    | HotReloadFileToggled _
    | SessionActivated _
    | SessionCreated _
    | SessionStopped _
    | WorkflowSwitching _
    | WorkflowSwitched _
    | SessionHealthChanged _ -> SseChannel.Session

  let channelName = function
    | SseChannel.State -> "state"
    | SseChannel.Session -> "session"

  /// SSE `event:` name for a given event. Single lookup — replaces the two
  /// separately-tracked sseEventType/sessionEventType constants.
  let sseEventType (evt: SseEvent) = evt |> channel |> channelName

  /// Preserved for the existing wire-contract name ("state" channel only).
  let sseEventTypeState = channelName SseChannel.State
  /// Preserved for the existing wire-contract name ("session" channel only).
  let sseEventTypeSession = channelName SseChannel.Session

  let private jsonOpts =
    let o = JsonSerializerOptions()
    o.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    o

  let private sid (s: WorkerProtocol.SessionId) = WorkerProtocol.SessionId.value s

  /// `jsonOpts` here has no F# converter registered (only
  /// `DefaultIgnoreCondition`), so an `Option` field must be converted to a
  /// nullable `obj` before serializing — same discipline as `diagnosticJson`'s
  /// `FileName |> Option.toObj` below.
  let private sessionHealthJson (health: SessionHealth) =
    {| status = SessionHealth.label health
       reason = SessionHealth.reason health |> Option.toObj |}

  let private diagnosticJson (d: WarmUp.WarmupFcsDiagnostic) =
    {| message = d.Message
       severity = d.Severity
       errorNumber = d.ErrorNumber
       fileName = d.FileName |> Option.toObj
       startLine = d.StartLine
       endLine = d.EndLine
       startColumn = d.StartColumn
       endColumn = d.EndColumn |}

  let private failedOpenJson (f: WarmUp.WarmupOpenFailure) =
    {| name = f.Name
       isModule = WarmUp.OpenableKind.toBool f.Kind
       error = f.ErrorMessage
       retryCount = f.RetryCount
       diagnostics = f.Diagnostics |> List.map diagnosticJson |}

  let private namespaceOpenedJson (b: WarmUp.OpenedBinding) =
    {| name = b.Name
       isModule = WarmUp.OpenableKind.toBool b.Kind
       source = b.Source
       durationMs = b.DurationMs |}

  let private assemblyLoadedJson (a: LoadedAssembly) =
    {| name = a.Name
       path = a.Path
       namespaceCount = a.NamespaceCount
       moduleCount = a.ModuleCount |}

  let private warmupContextJson (ctx: WarmupContext) =
    {| sourceFilesScanned = ctx.SourceFilesScanned
       warmupDurationMs = WarmupContext.totalDurationMs ctx
       phaseTiming =
         {| scanSourceFilesMs = ctx.PhaseTiming.ScanSourceFilesMs
            scanAssembliesMs = ctx.PhaseTiming.ScanAssembliesMs
            openNamespacesMs = ctx.PhaseTiming.OpenNamespacesMs
            totalMs = ctx.PhaseTiming.TotalMs |}
       assembliesLoaded = ctx.AssembliesLoaded |> List.map assemblyLoadedJson
       namespacesOpened = ctx.NamespacesOpened |> List.map namespaceOpenedJson
       failedOpens = ctx.FailedOpens |> List.map failedOpenJson |}

  /// Serialize an SseEvent to its JSON payload (the SSE frame's `data:`
  /// body — not the envelope). ONE function, ONE technique
  /// (JsonSerializer over anonymous records) for every case — replacing the
  /// former split between DaemonStateChange's sprintf string-templating and
  /// SessionEvents' hand-rolled Utf8JsonWriter. Wire shapes match the
  /// pre-unification output field-for-field (see DaemonStateChangeContractTests,
  /// McpWireProtocolTests, WorkflowSwitchTests).
  let toJson (evt: SseEvent) : string =
    match evt with
    // ── State channel ──
    | SessionProgress ->
      JsonSerializer.Serialize({| sessionProgress = true |}, jsonOpts)
    | SessionReady s ->
      JsonSerializer.Serialize({| sessionReady = sid s |}, jsonOpts)
    | SessionSwitched s ->
      JsonSerializer.Serialize({| sessionSwitched = sid s |}, jsonOpts)
    | HotReloadChanged s ->
      JsonSerializer.Serialize({| hotReloadChanged = true; sessionId = sid s |}, jsonOpts)
    | FileReloaded (s, path) ->
      JsonSerializer.Serialize({| fileReloaded = path; sessionId = sid s |}, jsonOpts)
    | SessionFaulted (s, error) ->
      JsonSerializer.Serialize({| sessionFaulted = sid s; error = error |}, jsonOpts)
    | ModelChanged (outputCount, diagCount) ->
      JsonSerializer.Serialize({| outputCount = outputCount; diagCount = diagCount |}, jsonOpts)
    | WarmupProgress (s, step, total, _msg) ->
      JsonSerializer.Serialize({| warmupProgress = true; sessionId = sid s; step = step; total = total |}, jsonOpts)
    | SystemAlarm (phase, message) ->
      JsonSerializer.Serialize({| systemAlarm = true; phase = phase; message = message |}, jsonOpts)
    | CohortChanged ->
      JsonSerializer.Serialize({| cohortChanged = true |}, jsonOpts)
    // ── Session channel ──
    | WarmupContextSnapshot (s, ctx) ->
      JsonSerializer.Serialize(
        {| ``type`` = "warmup_context_snapshot"; sessionId = s; context = warmupContextJson ctx |}, jsonOpts)
    | HotReloadSnapshot (s, watchedFiles) ->
      JsonSerializer.Serialize(
        {| ``type`` = "hotreload_snapshot"; sessionId = s; watchedFiles = watchedFiles |}, jsonOpts)
    | HotReloadFileToggled (s, file, watched) ->
      JsonSerializer.Serialize(
        {| ``type`` = "hotreload_file_toggled"; sessionId = s; file = file; watched = watched |}, jsonOpts)
    | SessionActivated s ->
      JsonSerializer.Serialize({| ``type`` = "session_activated"; sessionId = s |}, jsonOpts)
    | SessionCreated (s, projectNames) ->
      JsonSerializer.Serialize(
        {| ``type`` = "session_created"; sessionId = s; projectNames = projectNames |}, jsonOpts)
    | SessionStopped s ->
      JsonSerializer.Serialize({| ``type`` = "session_stopped"; sessionId = s |}, jsonOpts)
    | WorkflowSwitching (s, fromLabel, toLabel) ->
      JsonSerializer.Serialize(
        {| ``type`` = "workflow_switching"; sessionId = s; fromWorkflow = fromLabel; toWorkflow = toLabel |}, jsonOpts)
    | WorkflowSwitched (s, label, replCapability, hotReloadActive) ->
      JsonSerializer.Serialize(
        {| ``type`` = "workflow_switched"
           sessionId = s
           workflowLabel = label
           replCapability = replCapability
           hotReloadActive = hotReloadActive |}, jsonOpts)
    | SessionHealthChanged (s, health) ->
      JsonSerializer.Serialize(
        {| ``type`` = "session_health_changed"; sessionId = s; health = sessionHealthJson health |}, jsonOpts)

  /// Format a complete SSE frame (`event: ...\ndata: ...\n\n`) for an event.
  let format (evt: SseEvent) : string =
    SseWriter.formatSseEvent (sseEventType evt) (toJson evt)
