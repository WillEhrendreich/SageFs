namespace SageFs.Features

open System

/// What the profile tool measures.
[<RequireQualifiedAccess>]
type ProfileMode =
  /// Where managed CPU time went: methods by self time and the user's own code by total time.
  | Cpu
  /// Where allocated bytes went: by type, and by type at the nearest frame of the user's code.
  | Alloc

/// Why a profile was not made, or could not be read. Each case carries what the caller does next (`ProfileFailure.describe`).
[<RequireQualifiedAccess>]
type ProfileFailure =
  /// The mode named is not one of the two.
  | UnknownMode of given: string
  /// The session has no process of its own to record (an in-process session).
  | NotRecordable of reason: string
  /// The recording tool is not installed.
  | ToolMissing of tool: string * installCommand: string
  /// The tool started and could not attach to the host process.
  | CouldNotAttach of detail: string
  /// The host process ended while it was being recorded.
  | HostGone of detail: string
  /// The tool did not finish writing the trace in the time allowed.
  | StopTimedOut of waited: TimeSpan
  /// The trace was written and could not be read.
  | TraceUnreadable of detail: string
  /// The trace holds no sample to report: no managed code running (cpu) or no sampled allocation (alloc).
  | NoSamples
  /// The trace is larger than a report is built from.
  | TraceTooLarge of bytes: int64 * limit: int64

module ProfileMode =
  let all : ProfileMode list = [ ProfileMode.Cpu; ProfileMode.Alloc ]

  /// The one spelling of a mode on the wire.
  let toWire (mode: ProfileMode) : string =
    match mode with
    | ProfileMode.Cpu -> "cpu"
    | ProfileMode.Alloc -> "alloc"

  /// Read the tool's `mode` argument at the edge. An empty one means cpu.
  let parse (text: string) : Result<ProfileMode, ProfileFailure> =
    match (match isNull text with | true -> "" | false -> text.Trim().ToLowerInvariant()) with
    | "" -> Result.Ok ProfileMode.Cpu
    | other ->
      match all |> List.tryFind (fun mode -> toWire mode = other) with
      | Some mode -> Result.Ok mode
      | None -> Result.Error(ProfileFailure.UnknownMode text)

module ProfileFailure =
  /// What went wrong and the next action, in one reply a caller can act on.
  let describe (failure: ProfileFailure) : string =
    match failure with
    | ProfileFailure.UnknownMode given ->
      sprintf "profile: mode '%s' is not one of %s. Send mode=cpu or mode=alloc." given (ProfileMode.all |> List.map ProfileMode.toWire |> String.concat ", ")
    | ProfileFailure.NotRecordable reason ->
      sprintf "profile: this session cannot be recorded (%s). Create a session with an isolated host and ask again." reason
    | ProfileFailure.ToolMissing(tool, installCommand) ->
      sprintf "profile: %s is not installed on the machine that runs the daemon. Run `%s` and ask again. Your code did not run." tool installCommand
    | ProfileFailure.CouldNotAttach detail ->
      sprintf "profile: the recorder could not attach to the session's host (%s). Your code did not run. Check get_session_status, then ask again." detail
    | ProfileFailure.HostGone detail ->
      sprintf "profile: the session's host ended while it was recorded (%s). Call get_session_status, then hard_reset_fsi_session if the session is faulted." detail
    | ProfileFailure.StopTimedOut waited ->
      sprintf "profile: the recorder did not finish writing the trace in %s. Ask again with a shorter eval." (waited.ToString())
    | ProfileFailure.TraceUnreadable detail ->
      sprintf "profile: the trace was written and could not be read (%s). The eval ran. Ask again; if it repeats, report it with report_friction." detail
    | ProfileFailure.NoSamples ->
      "profile: the trace holds no sample to report (no managed code running, or no sampled allocation). The eval was too short or too small to sample, or spent its time waiting. Profile one that runs about a second or more, or allocates a few hundred KB."
    | ProfileFailure.TraceTooLarge(bytes, limit) ->
      sprintf "profile: the trace is %d bytes, over the %d a report is built from. Profile a shorter eval." bytes limit

/// One frame of a stack: the assembly it ran in and the method, with the parameter list cut off.
type Frame = { Module: string; Method: string }

[<RequireQualifiedAccess>]
type FrameOwner =
  /// Code the user wrote: typed into the session, or in a project the session owns.
  | UserCode
  /// The runtime, the libraries and the host itself.
  | RuntimeCode

module Frame =
  /// The assembly name the F# compiler gives the dynamic assembly that holds everything typed into a session.
  let evalAssembly = "FSI-ASSEMBLY"

  /// Whether a frame is a thread waiting for a garbage collection to finish: the runtime's poll that a thread sits in while the
  /// collection runs (seen in a trace of a real FSI host as `Thread.<PollGC>g__PollGCWorker`).
  let isGcWait (frame: Frame) : bool = frame.Method.Contains("PollGC", StringComparison.Ordinal)

  /// The text a row shows for a frame.
  let label (frame: Frame) : string =
    match String.IsNullOrEmpty frame.Module with
    | true -> frame.Method
    | false -> sprintf "%s!%s" frame.Module frame.Method

  /// Whose frame this is. Code typed into the session lives in the dynamic assembly, in types named `FSI_0003` and so on, and a
  /// project the session loaded is one of `projectModules`. Total: every frame is one or the other.
  let ownerOf (projectModules: Set<string>) (frame: Frame) : FrameOwner =
    match
      frame.Module = evalAssembly
      || frame.Method.StartsWith("FSI_", StringComparison.Ordinal)
      || frame.Method.Contains("$FSI_", StringComparison.Ordinal)
      || Set.contains frame.Module projectModules
    with
    | true -> FrameOwner.UserCode
    | false -> FrameOwner.RuntimeCode

/// What the thread was doing when it was sampled, read from the marker frame the trace puts at the leaf.
[<RequireQualifiedAccess>]
type SampleKind =
  /// Managed code was running.
  | ManagedCpu
  /// The thread was in native code or waiting in it (a socket read, a lock). Idle threads sit here.
  | NativeCode
  /// The thread was blocked.
  | Blocked
  /// The trace carried no marker for the leaf.
  | Unmarked

/// A stretch of one thread with one stack, leaf first, and how long it lasted.
type StackSample = { Kind: SampleKind; Frames: Frame list; Ms: float }

/// One line of a table: what it is, how much, and its share of the total the table is about.
type Row = { Label: string; Weight: float; Percent: float }

type CpuReport =
  { /// The sum of the managed CPU samples; every percentage in the CPU tables is of this.
    ManagedMs: float
    NativeMs: float
    BlockedMs: float
    UnmarkedMs: float
    /// Managed samples with no frame at all, counted and named, never dropped.
    NoStackMs: float
    /// Part of `ManagedMs`: the time whose innermost frame is a thread waiting for a garbage collection to finish.
    GcWaitMs: float
    /// Methods by self time, then one "(other)" row, so the rows sum to `ManagedMs`.
    SelfRows: Row list
    /// The user's own frames by total time. A frame is counted once per sample however often recursion repeats it, so these
    /// rows overlap and do not sum to anything.
    UserRows: Row list }

type AllocSample = { TypeName: string; Bytes: int64; Frames: Frame list }

type AllocReport =
  { TotalBytes: int64
    /// Bytes by type, then "(other)": the rows sum to `TotalBytes`.
    ByType: Row list
    /// Bytes by type at the nearest frame of the user's code, walking from the leaf; then "(other)".
    BySite: Row list }

/// How much of a table to keep and how long the whole reply may be.
type ReportBounds = { Top: int; MaxChars: int }

module ProfileReport =
  let otherLabel = "(other)"
  let noStackLabel = "(no stack)"

  let defaultBounds : ReportBounds = { Top = 15; MaxChars = 6000 }

  /// What a row is worked out from: a name, an amount, and the total its share is of.
  type RowInput = { ShareOf: float; Name: string; Amount: float }

  /// One row: an amount and its share of the total.
  let rowOf (input: RowInput) : Row =
    let percent =
      match input.ShareOf > 0.0 with
      | true -> 100.0 * input.Amount / input.ShareOf
      | false -> 0.0
    { Label = input.Name; Weight = input.Amount; Percent = percent }

  /// The heaviest `top` groups, then one "(other)" row for the rest, so the rows always sum to `total`.
  let rowsWithOther (top: int) (total: float) (weights: (string * float) list) : Row list =
    let ranked = weights |> List.sortBy (fun (label, weight) -> -weight, label)
    let kept = ranked |> List.truncate (max 0 top)
    let rest = ranked |> List.skip (List.length kept) |> List.sumBy snd
    let others =
      match rest > 0.0 with
      | true -> [ rowOf { ShareOf = total; Name = otherLabel; Amount = rest } ]
      | false -> []
    (kept |> List.map (fun (label, weight) -> rowOf { ShareOf = total; Name = label; Amount = weight })) @ others

  let groupWeights (items: (string * float) list) : (string * float) list =
    items |> List.groupBy fst |> List.map (fun (label, group) -> label, group |> List.sumBy snd)

  let selfLabelOf (sample: StackSample) : string =
    match sample.Frames with
    | leaf :: _ -> Frame.label leaf
    | [] -> noStackLabel

  let cpu (projectModules: Set<string>) (top: int) (samples: StackSample list) : CpuReport =
    let msOf kind = samples |> List.filter (fun s -> s.Kind = kind) |> List.sumBy (fun s -> s.Ms)
    let managed = samples |> List.filter (fun s -> s.Kind = SampleKind.ManagedCpu)
    let managedMs = managed |> List.sumBy (fun s -> s.Ms)
    let selfRows =
      managed |> List.map (fun s -> selfLabelOf s, s.Ms) |> groupWeights |> rowsWithOther top managedMs
    let userFramesOf (sample: StackSample) : Frame list =
      sample.Frames |> List.filter (fun f -> Frame.ownerOf projectModules f = FrameOwner.UserCode) |> List.distinct
    let userRows =
      managed
      |> List.collect (fun s -> userFramesOf s |> List.map (fun f -> Frame.label f, s.Ms))
      |> groupWeights
      |> List.sortBy (fun (label, weight) -> -weight, label)
      |> List.truncate (max 0 top)
      |> List.map (fun (label, weight) -> rowOf { ShareOf = managedMs; Name = label; Amount = weight })
    { ManagedMs = managedMs
      NativeMs = msOf SampleKind.NativeCode
      BlockedMs = msOf SampleKind.Blocked
      UnmarkedMs = msOf SampleKind.Unmarked
      NoStackMs = managed |> List.filter (fun s -> List.isEmpty s.Frames) |> List.sumBy (fun s -> s.Ms)
      GcWaitMs =
        managed
        |> List.filter (fun s ->
          match s.Frames with
          | leaf :: _ -> Frame.isGcWait leaf
          | [] -> false)
        |> List.sumBy (fun s -> s.Ms)
      SelfRows = selfRows
      UserRows = userRows }

  /// The nearest frame of the user's code walking from the leaf, or None when no frame is the user's.
  let nearestUserFrame (projectModules: Set<string>) (frames: Frame list) : Frame option =
    frames |> List.tryFind (fun f -> Frame.ownerOf projectModules f = FrameOwner.UserCode)

  let alloc (projectModules: Set<string>) (top: int) (samples: AllocSample list) : AllocReport =
    let total = samples |> List.sumBy (fun s -> float s.Bytes)
    let byType = samples |> List.map (fun s -> s.TypeName, float s.Bytes) |> groupWeights |> rowsWithOther top total
    let siteLabel (sample: AllocSample) =
      match nearestUserFrame projectModules sample.Frames with
      | Some frame -> sprintf "%s at %s" sample.TypeName (Frame.label frame)
      | None -> sprintf "%s (no frame of yours)" sample.TypeName
    let bySite = samples |> List.map (fun s -> siteLabel s, float s.Bytes) |> groupWeights |> rowsWithOther top total
    { TotalBytes = samples |> List.sumBy (fun s -> s.Bytes)
      ByType = byType
      BySite = bySite }

  /// Keep whole lines while they fit, and say how many were left out. A reply longer than the bound would cost an agent its context.
  let boundLines (maxChars: int) (lines: string list) : string =
    let usedAfter = lines |> List.scan (fun used line -> used + line.Length + 1) 0 |> List.tail
    let kept =
      List.zip lines usedAfter
      |> List.takeWhile (fun (_, used) -> used <= maxChars)
      |> List.map fst
    let omitted = List.length lines - List.length kept
    match omitted > 0 with
    | true -> String.concat "\n" (kept @ [ sprintf "(%d more lines left out to keep this reply under %d characters)" omitted maxChars ])
    | false -> String.concat "\n" kept

  let fmtMs (ms: float) : string = sprintf "%.0f ms" ms

  let fmtBytes (bytes: float) : string =
    match bytes with
    | b when b >= 1024.0 * 1024.0 -> sprintf "%.1f MB" (b / 1024.0 / 1024.0)
    | b when b >= 1024.0 -> sprintf "%.1f KB" (b / 1024.0)
    | b -> sprintf "%.0f B" b

  let rowLine (fmt: float -> string) (row: Row) : string =
    sprintf "  %5.1f%%  %10s  %s" row.Percent (fmt row.Weight) row.Label

  let renderCpu (bounds: ReportBounds) (report: CpuReport) : string =
    let table (title: string) (rows: Row list) =
      match rows with
      | [] -> [ sprintf "%s: none" title ]
      | _ -> title :: (rows |> List.map (rowLine fmtMs))
    let notMeasured =
      [ "Not counted above:"
        sprintf "  %s in native code or waiting (idle threads are here)" (fmtMs report.NativeMs)
        sprintf "  %s blocked" (fmtMs report.BlockedMs)
        match report.UnmarkedMs > 0.0 with
        | true -> sprintf "  %s with no thread-state marker" (fmtMs report.UnmarkedMs)
        | false -> ()
        match report.NoStackMs > 0.0 with
        | true -> sprintf "  %s of managed time had no stack (named \"%s\" above)" (fmtMs report.NoStackMs) noStackLabel
        | false -> ()
        "  times are the trace's sampled time and run well under the wall clock: compare rows with each other, not with a stopwatch" ]
    [ sprintf "CPU profile: %s of managed CPU sampled. Percentages are of that." (fmtMs report.ManagedMs) ]
    @ table "Methods by self time" report.SelfRows
    @ table "Your code by total time (rows overlap)" report.UserRows
    @ [ sprintf
          "Waiting for a garbage collection: %s (%.1f%% of the managed time). A large share means allocation is the cost: profile mode=alloc."
          (fmtMs report.GcWaitMs)
          (rowOf { ShareOf = report.ManagedMs; Name = ""; Amount = report.GcWaitMs }).Percent ]
    @ notMeasured
    |> boundLines bounds.MaxChars

  let renderAlloc (bounds: ReportBounds) (report: AllocReport) : string =
    [ sprintf "Allocation profile: %s sampled. Percentages are of that." (fmtBytes (float report.TotalBytes))
      "Bytes by type" ]
    @ (report.ByType |> List.map (rowLine fmtBytes))
    @ [ "Bytes by type at the nearest frame of yours" ]
    @ (report.BySite |> List.map (rowLine fmtBytes))
    |> boundLines bounds.MaxChars
