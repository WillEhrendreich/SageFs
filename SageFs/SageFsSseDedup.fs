namespace SageFs

open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting

/// Pure dedup-key generation for the SSE state-change event.
/// Including test state fields ensures `/events` SSE fires
/// when tests change even if output/diagnostics stay the same.
module SseDedupKey =
  /// A closed set rendered with one exhaustive function each, never F#'s
  /// `string` on a union: that goes through the generated `ToString`, which
  /// formats structurally by reflection (measured at ~130 microseconds and
  /// ~54 KB per session per call in Release, on a path that runs once per
  /// output line).
  let private appendStatus (sb: System.Text.StringBuilder) (status: SessionDisplayStatus) : unit =
    match status with
    | SessionDisplayStatus.Running -> sb.Append('r') |> ignore
    | SessionDisplayStatus.Starting -> sb.Append('s') |> ignore
    | SessionDisplayStatus.Restarting -> sb.Append('R') |> ignore
    | SessionDisplayStatus.Faulted reason ->
      // Length-prefixed, so fault text holding a delimiter can never read as
      // the start of the next entry.
      sb.Append('f').Append(reason.Length).Append('~').Append(reason) |> ignore
    | SessionDisplayStatus.Lost -> sb.Append('l') |> ignore
    | SessionDisplayStatus.Stopped -> sb.Append('x') |> ignore
    | SessionDisplayStatus.Idle -> sb.Append('i') |> ignore

  let private appendPhase (sb: System.Text.StringBuilder) (phase: TestRunPhase) : unit =
    match phase with
    | TestRunPhase.Idle -> sb.Append('i') |> ignore
    | TestRunPhase.Running (RunGeneration gen) -> sb.Append('r').Append(gen) |> ignore
    | TestRunPhase.RunningButEdited (RunGeneration gen) -> sb.Append('e').Append(gen) |> ignore

  /// Dedup key: cached counters and the few fields the SSE payload shows,
  /// written with plain appends. Reads the cached StateVersion and
  /// CachedTestSummary instead of filtering and counting every test entry.
  /// Cost is linear in sessions and run phases (a handful of appends each),
  /// with no reflection and no per-entry formatting.
  let fromModel (model: SageFsModel) : string =
    let sb = System.Text.StringBuilder(128)
    sb.Append(model.RecentOutput.Version).Append('|') |> ignore
    let diagCount =
      model.Diagnostics |> Map.fold (fun acc _ errors -> acc + List.length errors) 0
    sb.Append(diagCount).Append('|') |> ignore
    sb.Append(model.Sessions.Sessions.Length).Append('|') |> ignore
    match ActiveSession.sessionId model.Sessions.ActiveSessionId with
    | Some sid -> sb.Append(SessionId.value sid) |> ignore
    | None -> ()
    sb.Append('|') |> ignore
    for s in model.Sessions.Sessions do
      sb.Append(SessionId.value s.Id).Append(':') |> ignore
      appendStatus sb s.Status
      sb.Append(';') |> ignore
    sb.Append('|') |> ignore
    let lt = model.LiveTesting.TestState
    let ts = lt.Cached.TestSummary
    sb.Append(ts.Total).Append(',')
      .Append(ts.Passed).Append(',')
      .Append(ts.Failed).Append(',')
      .Append(ts.Running).Append(',')
      .Append(ts.Stale).Append('|') |> ignore
    sb.Append(lt.Cached.StateVersion).Append('|') |> ignore
    let (RunGeneration gen) = lt.LastGeneration
    sb.Append(gen).Append('|') |> ignore
    for kvp in lt.RunPhases do
      // The map key is a session id string; length-prefix it like fault text.
      sb.Append(kvp.Key.Length).Append('~').Append(kvp.Key).Append(':') |> ignore
      appendPhase sb kvp.Value
      sb.Append(';') |> ignore
    sb.Append('|') |> ignore
    match lt.Activation with
    | LiveTestingActivation.Active -> sb.Append('1') |> ignore
    | LiveTestingActivation.Inactive -> sb.Append('0') |> ignore
    sb.ToString()
