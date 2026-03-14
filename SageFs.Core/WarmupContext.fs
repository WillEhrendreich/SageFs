namespace SageFs

open SageFs.WarmUp

/// Represents an assembly that was loaded during warmup.
type LoadedAssembly = {
  Name: string
  Path: string
  NamespaceCount: int
  ModuleCount: int
}

/// File readiness in the FSI session.
type FileReadiness =
  | NotLoaded
  | Loaded
  | Stale
  | LoadFailed

/// Per-file status combining readiness and hot-reload watch state.
type FileStatus = {
  Path: string
  Readiness: FileReadiness
  LastLoadedAt: System.DateTimeOffset option
  IsWatched: bool
}

/// Captures everything that happened during session startup.
type WarmupContext = {
  SourceFilesScanned: int
  AssembliesLoaded: LoadedAssembly list
  NamespacesOpened: OpenedBinding list
  FailedOpens: WarmupOpenFailure list
  PhaseTiming: WarmupPhaseTiming
  StartedAt: System.DateTimeOffset
}

module WarmupContext =
  let private nonNegative durationMs =
    max 0L durationMs

  let private phaseTimingFromMilestones scanPhaseMs assemblyPhaseMs openPhaseMs = {
    ScanSourceFilesMs = nonNegative scanPhaseMs
    ScanAssembliesMs = nonNegative (assemblyPhaseMs - scanPhaseMs)
    OpenNamespacesMs = nonNegative (openPhaseMs - assemblyPhaseMs)
    TotalMs = nonNegative openPhaseMs
  }

  let empty = {
    SourceFilesScanned = 0
    AssembliesLoaded = []
    NamespacesOpened = []
    FailedOpens = []
    PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 0L }
    StartedAt = System.DateTimeOffset.UtcNow
  }

  let completeWarmup
    startedAt
    sourceFilesScanned
    assembliesLoaded
    namespacesOpened
    failedOpens
    scanPhaseMs
    assemblyPhaseMs
    openPhaseMs = {
    SourceFilesScanned = sourceFilesScanned
    AssembliesLoaded = assembliesLoaded
    NamespacesOpened = namespacesOpened
    FailedOpens = failedOpens
    PhaseTiming = phaseTimingFromMilestones scanPhaseMs assemblyPhaseMs openPhaseMs
    StartedAt = startedAt
  }

  let totalOpenedCount (ctx: WarmupContext) =
    ctx.NamespacesOpened |> List.length

  let totalFailedCount (ctx: WarmupContext) =
    ctx.FailedOpens |> List.length

  let totalDurationMs (ctx: WarmupContext) =
    ctx.PhaseTiming.TotalMs

  let completionDuration (ctx: WarmupContext) =
    System.TimeSpan.FromMilliseconds(float ctx.PhaseTiming.TotalMs)

  let assemblyNames (ctx: WarmupContext) =
    ctx.AssembliesLoaded |> List.map (fun a -> a.Name)

  let moduleNames (ctx: WarmupContext) =
    ctx.NamespacesOpened
    |> List.filter (fun b -> b.Kind = OpenableKind.Module)
    |> List.map (fun b -> b.Name)

  let namespaceNames (ctx: WarmupContext) =
    ctx.NamespacesOpened
    |> List.filter (fun b -> b.Kind = OpenableKind.Namespace)
    |> List.map (fun b -> b.Name)

module FileReadiness =
  let label = function
    | NotLoaded -> "not loaded"
    | Loaded -> "loaded"
    | Stale -> "stale"
    | LoadFailed -> "load failed"

  let icon = function
    | NotLoaded -> "○"
    | Loaded -> "●"
    | Stale -> "~"
    | LoadFailed -> "✖"

  let isAvailable = function
    | Loaded -> true
    | _ -> false

/// Combined view for session context display.
type SessionContext = {
  SessionId: string
  ProjectNames: string list
  WorkingDir: string
  Status: string
  Warmup: WarmupContext
  FileStatuses: FileStatus list
}

module SessionContext =
  let summary (ctx: SessionContext) =
    let opened = WarmupContext.totalOpenedCount ctx.Warmup
    let failed = WarmupContext.totalFailedCount ctx.Warmup
    let loaded =
      ctx.FileStatuses
      |> List.filter (fun f -> f.Readiness = Loaded)
      |> List.length
    let total = ctx.FileStatuses |> List.length
    sprintf "%s | %d/%d files loaded | %d namespaces (%d failed) | %dms"
      ctx.Status loaded total opened failed (WarmupContext.totalDurationMs ctx.Warmup)

  let assemblyLine (asm: LoadedAssembly) =
    sprintf "📦 %s (%d ns, %d mod)" asm.Name asm.NamespaceCount asm.ModuleCount

  let openLine (b: OpenedBinding) =
    let kind = OpenableKind.label b.Kind
    sprintf "open %s // %s via %s" b.Name kind b.Source

  let fileLine (f: FileStatus) =
    sprintf "%s %s%s"
      (FileReadiness.icon f.Readiness)
      f.Path
      (match f.IsWatched with | true -> " 👁" | false -> "")

/// TUI-specific formatting for session context — plain text lines.
module SessionContextTui =
  let summaryLine (ctx: SessionContext) =
    let loaded =
      ctx.FileStatuses
      |> List.filter (fun f -> f.Readiness = Loaded)
      |> List.length
    let total = ctx.FileStatuses |> List.length
    let nsCount = WarmupContext.totalOpenedCount ctx.Warmup
    let failCount = WarmupContext.totalFailedCount ctx.Warmup
    sprintf "[%s] %d/%d files | %d ns (%d fail) | %dms"
      ctx.Status loaded total nsCount failCount (WarmupContext.totalDurationMs ctx.Warmup)

  let detailLines (ctx: SessionContext) =
    let lines = System.Collections.Generic.List<string>()
    lines.Add(sprintf "Session: %s" ctx.SessionId)
    match ctx.WorkingDir.Length > 0 with
    | true -> lines.Add(sprintf "Dir: %s" ctx.WorkingDir)
    | false -> ()

    let asms = ctx.Warmup.AssembliesLoaded
    match asms.Length > 0 with
    | true ->
      lines.Add(sprintf "── Assemblies (%d) ──" asms.Length)
      for a in asms do
        lines.Add(SessionContext.assemblyLine a)
    | false -> ()

    let opened = ctx.Warmup.NamespacesOpened
    match opened.Length > 0 with
    | true ->
      lines.Add(sprintf "── Opened (%d) ──" opened.Length)
      for b in opened do
        lines.Add(SessionContext.openLine b)
    | false -> ()

    let failed = ctx.Warmup.FailedOpens
    match failed.Length > 0 with
    | true ->
      lines.Add(sprintf "── Failed (%d) ──" failed.Length)
      for f in failed do
        let kind = OpenableKind.label f.Kind
        lines.Add(sprintf "✖ %s (%s): %s" f.Name kind f.ErrorMessage)
        for d in f.Diagnostics do
          let loc =
            match d.FileName with
            | Some fn -> sprintf "%s:%d:%d" fn d.StartLine d.StartColumn
            | None -> "unknown"
          lines.Add(sprintf "    FS%04d %s — %s" d.ErrorNumber loc d.Message)
    | false -> ()

    let files = ctx.FileStatuses
    match files.Length > 0 with
    | true ->
      lines.Add(sprintf "── Files (%d) ──" files.Length)
      for f in files do
        lines.Add(SessionContext.fileLine f)
    | false -> ()

    lines |> Seq.toList

  let renderContent (ctx: SessionContext) =
    let summary = summaryLine ctx
    let details = detailLines ctx
    summary :: details |> String.concat "\n"
