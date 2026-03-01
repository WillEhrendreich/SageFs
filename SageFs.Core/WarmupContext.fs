namespace SageFs

/// Represents an assembly that was loaded during warmup.
type LoadedAssembly = {
  Name: string
  Path: string
  NamespaceCount: int
  ModuleCount: int
}

/// Represents a namespace or module that was opened during warmup.
type OpenedBinding = {
  Name: string
  IsModule: bool
  Source: string
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
  FailedOpens: (string * string) list
  WarmupDurationMs: int64
  StartedAt: System.DateTimeOffset
}

module WarmupContext =
  let empty = {
    SourceFilesScanned = 0
    AssembliesLoaded = []
    NamespacesOpened = []
    FailedOpens = []
    WarmupDurationMs = 0L
    StartedAt = System.DateTimeOffset.UtcNow
  }

  let totalOpenedCount (ctx: WarmupContext) =
    ctx.NamespacesOpened |> List.length

  let totalFailedCount (ctx: WarmupContext) =
    ctx.FailedOpens |> List.length

  let assemblyNames (ctx: WarmupContext) =
    ctx.AssembliesLoaded |> List.map (fun a -> a.Name)

  let moduleNames (ctx: WarmupContext) =
    ctx.NamespacesOpened
    |> List.filter (fun b -> b.IsModule)
    |> List.map (fun b -> b.Name)

  let namespaceNames (ctx: WarmupContext) =
    ctx.NamespacesOpened
    |> List.filter (fun b -> not b.IsModule)
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
      ctx.Status loaded total opened failed ctx.Warmup.WarmupDurationMs

  let assemblyLine (asm: LoadedAssembly) =
    sprintf "📦 %s (%d ns, %d mod)" asm.Name asm.NamespaceCount asm.ModuleCount

  let openLine (b: OpenedBinding) =
    let kind = match b.IsModule with | true -> "module" | false -> "namespace"
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
      ctx.Status loaded total nsCount failCount ctx.Warmup.WarmupDurationMs

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
      for (name, err) in failed do
        lines.Add(sprintf "✖ %s: %s" name err)
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
