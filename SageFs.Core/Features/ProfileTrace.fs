namespace SageFs.Features

open System
open System.Text.Json

/// Reads the Speedscope file `dotnet-trace convert --format Speedscope` writes (the exporter is TraceEvent's) into stack
/// samples. Nothing here touches a process or a disk: the text goes in and samples or a named failure come out.
///
/// The file holds one profile per thread. Each profile is a list of open and close events for frames, with a time on each,
/// so a stretch of time between two events belongs to the stack that was open then. A frame is named
/// `Module!Namespace.Type.Method(parameters)`. The exporter puts a marker frame at the leaf of each sampled stack: `CPU_TIME`
/// for managed code running, `UNMANAGED_CODE_TIME` for a thread in native code or waiting in it, `BLOCKED_TIME` for a blocked
/// one. Frames with no `!` in the name (the process, the thread, "(Non-Activities)") are structure, not code.
module SpeedscopeTrace =
  /// The largest trace text a report is built from, in characters.
  let maxCharacters : int64 = 64L * 1024L * 1024L

  [<RequireQualifiedAccess>]
  type FrameEntry =
    | Marker of SampleKind
    | Code of Frame
    | Structure

  /// Read one frame name from the file. The parameter list is cut off, so overloads of one method are one row.
  let classify (name: string) : FrameEntry =
    match name with
    | "CPU_TIME" -> FrameEntry.Marker SampleKind.ManagedCpu
    | "UNMANAGED_CODE_TIME" -> FrameEntry.Marker SampleKind.NativeCode
    | "BLOCKED_TIME" -> FrameEntry.Marker SampleKind.Blocked
    | _ ->
      match name.IndexOf '!' with
      | bang when bang > 0 ->
        let rest = name.Substring(bang + 1)
        let methodName =
          match rest.IndexOf '(' with
          | paren when paren > 0 -> rest.Substring(0, paren)
          | _ -> rest
        FrameEntry.Code { Module = name.Substring(0, bang); Method = methodName }
      | _ -> FrameEntry.Structure

  type Walk =
    { Stack: int list
      Last: float
      Acc: Map<int list, float> }

  /// One open or close event of a profile: its type ("O" or "C"), the frame it names and the time.
  type TraceEvent = { EventType: string; FrameIndex: int; AtMs: float }

  /// A file that does not read, with what is wrong with it.
  let unreadable (detail: string) : ProfileFailure = ProfileFailure.TraceUnreadable detail

  let eventOf (frameCount: int) (walk: Walk) (event: TraceEvent) : Result<Walk, ProfileFailure> =
    let spent =
      match List.isEmpty walk.Stack || event.AtMs <= walk.Last with
      | true -> walk.Acc
      | false ->
        walk.Acc |> Map.change walk.Stack (fun held -> Some((defaultArg held 0.0) + (event.AtMs - walk.Last)))
    match event.EventType with
    | _ when event.FrameIndex < 0 || event.FrameIndex >= frameCount ->
      Result.Error(unreadable (sprintf "an event names frame %d and the file lists %d" event.FrameIndex frameCount))
    | "O" -> Result.Ok { Stack = event.FrameIndex :: walk.Stack; Last = event.AtMs; Acc = spent }
    | "C" ->
      match walk.Stack with
      | top :: below when top = event.FrameIndex -> Result.Ok { Stack = below; Last = event.AtMs; Acc = spent }
      | _ -> Result.Error(unreadable (sprintf "an event closes frame %d and it is not the innermost open one" event.FrameIndex))
    | other -> Result.Error(unreadable (sprintf "an event has type '%s'" other))

  let eventsOf (profile: JsonElement) : TraceEvent seq =
    profile.GetProperty("events").EnumerateArray()
    |> Seq.map (fun e ->
      { EventType = e.GetProperty("type").GetString()
        FrameIndex = e.GetProperty("frame").GetInt32()
        AtMs = e.GetProperty("at").GetDouble() })

  let walkProfile (frameCount: int) (profile: JsonElement) : Result<Map<int list, float>, ProfileFailure> =
    let start = Result.Ok { Stack = []; Last = 0.0; Acc = Map.empty }
    eventsOf profile
    |> Seq.fold (fun state event -> state |> Result.bind (fun walk -> eventOf frameCount walk event)) start
    |> Result.map (fun walk -> walk.Acc)

  let sampleOf (entries: FrameEntry[]) (stackAndMs: int list * float) : StackSample =
    let stack, ms = stackAndMs
    let entriesLeafFirst = stack |> List.map (fun i -> entries.[i])
    let kind =
      match entriesLeafFirst with
      | FrameEntry.Marker k :: _ -> k
      | FrameEntry.Code _ :: _
      | FrameEntry.Structure :: _
      | [] -> SampleKind.Unmarked
    let frames =
      entriesLeafFirst
      |> List.choose (fun entry ->
        match entry with
        | FrameEntry.Code frame -> Some frame
        | FrameEntry.Marker _
        | FrameEntry.Structure -> None)
    { Kind = kind; Frames = frames; Ms = ms }

  /// Samples with the same kind and stack are one sample, with their times added.
  let merge (samples: StackSample list) : StackSample list =
    samples
    |> List.groupBy (fun s -> s.Kind, s.Frames)
    |> List.map (fun ((kind, frames), group) -> { Kind = kind; Frames = frames; Ms = group |> List.sumBy (fun s -> s.Ms) })

  let read (json: string) : Result<StackSample list, ProfileFailure> =
    use document = JsonDocument.Parse json
    let root = document.RootElement
    let entries =
      root.GetProperty("shared").GetProperty("frames").EnumerateArray()
      |> Seq.map (fun f -> classify (f.GetProperty("name").GetString()))
      |> Array.ofSeq
    let profiles = root.GetProperty("profiles").EnumerateArray() |> List.ofSeq
    let notEvented =
      profiles |> List.tryFind (fun p -> p.GetProperty("type").GetString() <> "evented")
    match notEvented with
    | Some p -> Result.Error(unreadable (sprintf "a profile has type '%s' and only 'evented' is read" (p.GetProperty("type").GetString())))
    | None ->
      profiles
      |> List.fold
        (fun state profile ->
          state
          |> Result.bind (fun acc ->
            walkProfile entries.Length profile
            |> Result.map (fun walked -> walked |> Map.toList |> List.map (sampleOf entries) |> List.append acc)))
        (Result.Ok [])
      |> Result.map merge

  /// Read the text of a Speedscope file into samples. A file that is not Speedscope, or whose events do not nest, is a
  /// named failure with what was wrong.
  let parse (json: string) : Result<StackSample list, ProfileFailure> =
    match int64 json.Length > maxCharacters with
    | true -> Result.Error(ProfileFailure.TraceTooLarge(int64 json.Length, maxCharacters))
    | false ->
      match SageFs.Guarded.attempt (fun () -> read json) with
      | SageFs.Guarded.Attempt.Completed outcome -> outcome
      | SageFs.Guarded.Attempt.Threw ex -> Result.Error(unreadable ex.Message)

  /// The CPU report of a trace, or why there is none. A trace with no managed CPU sample is `NoSamples`, so an empty table is
  /// never presented as an answer.
  let cpuReport (projectModules: Set<string>) (top: int) (json: string) : Result<CpuReport, ProfileFailure> =
    parse json
    |> Result.bind (fun samples ->
      let report = ProfileReport.cpu projectModules top samples
      match report.ManagedMs > 0.0 with
      | true -> Result.Ok report
      | false -> Result.Error ProfileFailure.NoSamples)
