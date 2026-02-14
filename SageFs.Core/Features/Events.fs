module SageFs.Features.Events

open System

/// Identifies who caused the event
type EventSource =
  | Console
  | McpAgent of agentName: string
  | FileSync of fileName: string
  | System

  override this.ToString() =
    match this with
    | Console -> "console"
    | McpAgent name -> sprintf "mcp:%s" name
    | FileSync name -> sprintf "file:%s" name
    | System -> "system"

/// Diagnostic data captured in events (serialization-friendly)
type DiagnosticEvent = {
  Message: string
  Severity: Diagnostics.DiagnosticSeverity
  StartLine: int
  StartColumn: int
  EndLine: int
  EndColumn: int
}

/// Completion item captured in events
type CompletionItemEvent = {
  Label: string
  Kind: string
  Detail: string option
}

/// All events that can occur in an SageFs session
type SageFsEvent =
  // Session lifecycle
  | SessionStarted of sessionStarted: {| Config: Map<string, string>; StartedAt: DateTimeOffset |}
  | SessionWarmUpCompleted of warmUpCompleted: {| Duration: TimeSpan; Errors: string list |}
  | SessionReady
  | SessionFaulted of sessionFaulted: {| Error: string; StackTrace: string option |}
  | SessionReset
  | SessionHardReset of hardReset: {| Rebuild: bool |}

  // Evaluation
  | EvalRequested of evalRequested: {| Code: string; Source: EventSource |}
  | EvalCompleted of evalCompleted: {| Code: string; Result: string; TypeSignature: string option; Duration: TimeSpan |}
  | EvalFailed of evalFailed: {| Code: string; Error: string; Diagnostics: DiagnosticEvent list |}

  // Diagnostics
  | DiagnosticsChecked of diagChecked: {| Code: string; Diagnostics: DiagnosticEvent list; Source: EventSource |}
  | DiagnosticsCleared

  // Script loading
  | ScriptLoaded of scriptLoaded: {| FilePath: string; StatementCount: int; Source: EventSource |}
  | ScriptLoadFailed of scriptFailed: {| FilePath: string; Error: string |}

  // MCP activity tracking
  | McpInputReceived of mcpInput: {| Source: EventSource; Content: string |}
  | McpOutputSent of mcpOutput: {| Source: EventSource; Content: string |}

/// Metadata attached to every event
type EventMetadata = {
  Source: EventSource
  CorrelationId: Guid option
}

module DiagnosticEvent =
  /// Convert from Features.Diagnostics.Diagnostic to serializable DiagnosticEvent
  let fromDiagnostic (d: Diagnostics.Diagnostic) : DiagnosticEvent =
    { Message = d.Message
      Severity = d.Severity
      StartLine = d.Range.StartLine
      StartColumn = d.Range.StartColumn
      EndLine = d.Range.EndLine
      EndColumn = d.Range.EndColumn }
