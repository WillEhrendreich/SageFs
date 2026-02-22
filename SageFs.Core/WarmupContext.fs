namespace SageFs.Core

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
    let kind = if b.IsModule then "module" else "namespace"
    sprintf "open %s // %s via %s" b.Name kind b.Source

  let fileLine (f: FileStatus) =
    sprintf "%s %s%s"
      (FileReadiness.icon f.Readiness)
      f.Path
      (if f.IsWatched then " 👁" else "")
