namespace SageFs

open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting

/// Pure dedup-key generation for the SSE state-change event.
/// Including test state fields ensures `/events` SSE fires
/// when tests change even if output/diagnostics stay the same.
module SseDedupKey =
  /// O(1) dedup key — reads cached StateVersion and CachedTestSummary
  /// instead of filtering+counting 3131 entries (was 50-100ms, now <0.01ms).
  let fromModel (model: SageFsModel) : string =
    let sb = System.Text.StringBuilder(128)
    sb.Append(model.RecentOutput.Version).Append('|') |> ignore
    let diagCount =
      model.Diagnostics |> Map.values |> Seq.sumBy List.length
    sb.Append(diagCount).Append('|') |> ignore
    sb.Append(model.Sessions.Sessions.Length).Append('|') |> ignore
    let activeSessionId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value |> Option.defaultValue ""
    sb.Append(activeSessionId).Append('|') |> ignore
    for s in model.Sessions.Sessions do
      sb.Append(s.Id).Append(':').Append(string s.Status).Append(';') |> ignore
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
      sb.Append(kvp.Key).Append(':').Append(string kvp.Value).Append(';') |> ignore
    sb.Append('|') |> ignore
    match lt.Activation = LiveTestingActivation.Active with
    | true -> sb.Append('1') |> ignore
    | false -> sb.Append('0') |> ignore
    sb.ToString()
