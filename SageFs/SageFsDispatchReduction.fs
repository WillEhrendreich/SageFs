namespace SageFs

open System
open SageFs.WorkerProtocol
open SageFs.WarmUp
open SageFs.Features.Diagnostics
open SageFs.Features.LiveTesting

// Split from SageFsApp.fs (file-size ratchet): dispatch-batch reduction is its own
// slice, used only by the Elm daemon's dispatch path (ElmDaemon.fs).

module SageFsDispatchReduction =
  let [<Literal>] private MaxDispatchBufferedTestResultCount = 1600

  let private tryExtractBufferedTestResults (msg: SageFsMsg) =
    match msg with
    | SageFsMsg.Event (TuiEvent.TestResultsBatch (sessionId, results)) ->
      Some {
        SessionId = sessionId
        TotalResultCount = results.Length
        Batches = [ results ]
      }
    | SageFsMsg.BufferedTestResults buffered ->
      Some buffered
    | _ ->
      None

  let private tryAppendBufferedTestResults
    (buffered: BufferedTestResultsPayload)
    (incoming: BufferedTestResultsPayload)
    =
    // Never merge batches across sessions — each carries its own SessionId,
    // and results must land in their owning session's cycle (SageFsUpdate).
    match buffered.SessionId = incoming.SessionId with
    | false -> None
    | true ->
      let total = buffered.TotalResultCount + incoming.TotalResultCount
      match total <= MaxDispatchBufferedTestResultCount with
      | true ->
        Some {
          buffered with
            TotalResultCount = total
            Batches = buffered.Batches @ incoming.Batches
        }
      | false ->
        None

  let reduceDispatchBatch (batch: SageFsMsg array) : SageFsMsg array =
    let reduced = ResizeArray<SageFsMsg>()
    let mutable pending: BufferedTestResultsPayload option = None

    let flushPending () =
      match pending with
      | Some buffered ->
        reduced.Add(SageFsMsg.BufferedTestResults buffered)
        pending <- None
      | None ->
        ()

    for msg in batch do
      match tryExtractBufferedTestResults msg, pending with
      | Some incoming, Some buffered ->
        match tryAppendBufferedTestResults buffered incoming with
        | Some merged ->
          pending <- Some merged
        | None ->
          flushPending ()
          pending <- Some incoming
      | Some incoming, None ->
        pending <- Some incoming
      | None, _ ->
        flushPending ()
        reduced.Add msg

    flushPending ()
    reduced.ToArray()

