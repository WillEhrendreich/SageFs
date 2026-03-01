namespace SageFs.VisualStudio.Core

open System

/// Result of evaluating F# code via the daemon.
type EvalResult =
  { Output: string
    Diagnostics: string list
    ExitCode: int }

/// Info about an FSI session from the daemon.
type SessionInfo =
  { Id: string
    ProjectNames: string list
    State: string
    WorkingDirectory: string
    EvalCount: int }

/// Status of the SageFs daemon connection.
[<RequireQualifiedAccess>]
type DaemonStatus =
  | Offline
  | Connecting
  | Connected of sessionCount: int

/// Info about a loaded assembly from warmup context.
type LoadedAssemblyInfo =
  { Name: string
    Path: string
    NamespaceCount: int
    ModuleCount: int }

/// Info about an opened namespace/module from warmup context.
type OpenedBindingInfo =
  { Name: string
    IsModule: bool
    Source: string }

/// Warmup context for a session.
type WarmupContextInfo =
  { SourceFilesScanned: int
    AssembliesLoaded: LoadedAssemblyInfo list
    NamespacesOpened: OpenedBindingInfo list
    FailedOpens: string list list
    WarmupDurationMs: int }

/// Hot reload file info.
type HotReloadFile =
  { Path: string
    Watched: bool }

/// Hot reload state for a session.
type HotReloadState =
  { Files: HotReloadFile list
    WatchedCount: int }

/// Kind of change in an eval diff line.
type DiffLineKind = Unchanged = 0 | Added = 1 | Removed = 2 | Modified = 3

/// A single line from an eval diff result.
type DiffLineInfo =
  { Kind: DiffLineKind
    Text: string
    OldText: string option }

/// Summary counts for an eval diff.
type DiffSummaryInfo =
  { Added: int
    Removed: int
    Modified: int
    Unchanged: int }

/// Complete eval diff result.
type EvalDiffInfo =
  { Lines: DiffLineInfo list
    Summary: DiffSummaryInfo
    HasDiff: bool }

/// A cell node in the dependency graph.
type CellNodeInfo =
  { CellId: int
    Source: string
    Produces: string list
    Consumes: string list
    IsStale: bool }

/// An edge in the cell dependency graph.
type CellEdgeInfo = { From: int; To: int }

/// Complete cell dependency graph.
type CellGraphInfo =
  { Cells: CellNodeInfo list
    Edges: CellEdgeInfo list }

/// Details about a binding in scope.
type BindingDetailInfo =
  { Name: string
    TypeSig: string
    CellIndex: int
    IsShadowed: bool
    ShadowedBy: int list
    ReferencedIn: int list }

/// Snapshot of all bindings in scope.
type BindingScopeInfo =
  { Bindings: BindingDetailInfo list
    ActiveCount: int
    ShadowedCount: int }

/// A single eval timeline entry.
type TimelineEntryInfo =
  { CellId: int
    DurationMs: float
    Status: string
    Timestamp: float }

/// Aggregated eval timeline statistics.
type TimelineStatsInfo =
  { Count: int
    P50Ms: float option
    P95Ms: float option
    P99Ms: float option
    MeanMs: float option
    Sparkline: string }

/// A cell in an exported notebook.
type NotebookCellInfo =
  { Index: int
    Label: string option
    Code: string
    Output: string option
    Deps: int list
    Bindings: string list }
