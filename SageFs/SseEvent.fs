namespace SageFs.Server

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
  /// The worker said what a save did (patched, needs a restart, did not
  /// compile...). The daemon has already recorded it on the session.
  | ReloadReported of sessionId: WorkerProtocol.SessionId * reload: SessionReload
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
  ///
  /// Carries the SCOPE that changed. One daemon now serves one cohort per
  /// repository, so "a cohort changed" no longer identifies WHICH one — a bare
  /// flag would have every client redraw everything it happens to be showing and
  /// had no way to tell a stale panel from a live one.
  | CohortChanged of scope: CohortScope
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
    | ReloadReported _
    | FileReloaded _
    | SessionFaulted _
    | ModelChanged _
    | WarmupProgress _
    | SystemAlarm _
    | CohortChanged _ -> SseChannel.State
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

  /// Keys as written, compact, and a null or `None` field is left out of the payload.
  let private payloadProfile = Json.omitNulls Json.standard

  let private write (payload: 'T) : string = Json.serialize payloadProfile payload

  let private sid (s: WorkerProtocol.SessionId) = WorkerProtocol.SessionId.value s

  /// `reason` is an `Option`; the profile's F# converter writes `Some` as the
  /// value and leaves `None` out.
  let private sessionHealthJson (health: SessionHealth) =
    {| status = SessionHealth.label health
       reason = SessionHealth.reason health |}

  let private diagnosticJson (d: WarmUp.WarmupFcsDiagnostic) =
    {| message = d.Message
       severity = d.Severity
       errorNumber = d.ErrorNumber
       fileName = d.FileName
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
  /// (`SageFs.Json` over anonymous records) for every case — replacing the
  /// former split between DaemonStateChange's sprintf string-templating and
  /// SessionEvents' hand-rolled Utf8JsonWriter. Wire shapes match the
  /// pre-unification output field-for-field (see DaemonStateChangeContractTests,
  /// McpWireProtocolTests, WorkflowSwitchTests).
  let toJson (evt: SseEvent) : string =
    match evt with
    // ── State channel ──
    | SessionProgress ->
      write({| sessionProgress = true |})
    | SessionReady s ->
      write({| sessionReady = sid s |})
    | SessionSwitched s ->
      write({| sessionSwitched = sid s |})
    | HotReloadChanged s ->
      write({| hotReloadChanged = true; sessionId = sid s |})
    | ReloadReported (s, reload) ->
      write({| reloadReported = SessionReload.toWire reload; sessionId = sid s |})
    | FileReloaded (s, path) ->
      write({| fileReloaded = path; sessionId = sid s |})
    | SessionFaulted (s, error) ->
      write({| sessionFaulted = sid s; error = error |})
    | ModelChanged (outputCount, diagCount) ->
      write({| outputCount = outputCount; diagCount = diagCount |})
    | WarmupProgress (s, step, total, _msg) ->
      write({| warmupProgress = true; sessionId = sid s; step = step; total = total |})
    | SystemAlarm (phase, message) ->
      write({| systemAlarm = true; phase = phase; message = message |})
    | CohortChanged scope ->
      // The scope's own label, not a machine path: this is what a client filters and displays,
      // and it is the same spelling a refusal quotes, so one cohort reads one way everywhere.
      write({| cohortChanged = true; scope = SageFs.Scope.label scope |})
    // ── Session channel ──
    | WarmupContextSnapshot (s, ctx) ->
      write(
        {| ``type`` = "warmup_context_snapshot"; sessionId = s; context = warmupContextJson ctx |})
    | HotReloadSnapshot (s, watchedFiles) ->
      write(
        {| ``type`` = "hotreload_snapshot"; sessionId = s; watchedFiles = watchedFiles |})
    | HotReloadFileToggled (s, file, watched) ->
      write(
        {| ``type`` = "hotreload_file_toggled"; sessionId = s; file = file; watched = watched |})
    | SessionActivated s ->
      write({| ``type`` = "session_activated"; sessionId = s |})
    | SessionCreated (s, projectNames) ->
      write(
        {| ``type`` = "session_created"; sessionId = s; projectNames = projectNames |})
    | SessionStopped s ->
      write({| ``type`` = "session_stopped"; sessionId = s |})
    | WorkflowSwitching (s, fromLabel, toLabel) ->
      write(
        {| ``type`` = "workflow_switching"; sessionId = s; fromWorkflow = fromLabel; toWorkflow = toLabel |})
    | WorkflowSwitched (s, label, replCapability, hotReloadActive) ->
      write(
        {| ``type`` = "workflow_switched"
           sessionId = s
           workflowLabel = label
           replCapability = replCapability
           hotReloadActive = hotReloadActive |})
    | SessionHealthChanged (s, health) ->
      write(
        {| ``type`` = "session_health_changed"; sessionId = s; health = sessionHealthJson health |})

  /// Format a complete SSE frame (`event: ...\ndata: ...\n\n`) for an event.
  let format (evt: SseEvent) : string =
    SseWriter.formatSseEvent (sseEventType evt) (toJson evt)
