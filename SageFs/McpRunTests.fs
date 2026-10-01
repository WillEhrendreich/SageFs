namespace SageFs

open System
open System.Threading.Tasks
open SageFs.McpTools
open SageFs.McpSessionRouting
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.RunReceipts

/// `run_tests`: one more door into the live-testing engine, not a second runner.
/// It dispatches the same `RunTestsRequested` the dashboard and the editors send,
/// tagged with a request id, and reads the engine's own durable record of that
/// request back as a receipt (`TestRunReceipt.observe`). Nothing here runs a test
/// or keeps a result of its own.
module McpRunTests =

  /// What the caller asked for. `Continue` re-reads an earlier request instead of
  /// starting a new run, so a long run never needs a second engine to be polled.
  type RunTestsRequest =
    { SessionId: string option
      WorkingDirectory: string option
      Pattern: string option
      File: string option
      Category: TestCategory option
      Continue: RunRequestId option
      Wait: TimeSpan }

  [<RequireQualifiedAccess>]
  type RunTestsOutcome =
    | Receipt of RunReceipt
    /// The session could not be resolved; `error` is the typed cause when there is one.
    | NotRoutable of message: string * error: SageFsError option
    /// No Elm loop (not a daemon): there is no engine to ask.
    | NoEngine

  /// Every live-testing state the engine keeps: the primary cycle and each session's own.
  let private statesOf (model: SageFsModel) : LiveTestState list =
    model.LiveTesting.TestState
    :: (model.PerSessionLiveTesting |> Map.values |> Seq.map (fun cycle -> cycle.TestState) |> List.ofSeq)

  let private observeAnywhere (requestId: RunRequestId) (model: SageFsModel) : RunReceipt =
    match statesOf model |> List.tryFind (fun state -> Map.containsKey requestId state.RunRequests) with
    | Some state -> TestRunReceipt.observe requestId state
    | None -> RunReceipt.Unattributable requestId

  let private isSettled (receipt: RunReceipt) : bool =
    match receipt with
    | RunReceipt.Ran _
    | RunReceipt.Refused _
    | RunReceipt.Unattributable _ -> true
    | RunReceipt.Pending _
    | RunReceipt.Started _ -> false

  /// Wait, event-driven off the engine's model-changed notification, until the request
  /// settles or `wait` runs out. `justDispatched` covers the gap between dispatching
  /// and the engine recording the request: an unrecorded id is then "queued", not
  /// "unknown". Returns the receipt as it stands when the wait ends.
  let private awaitReceipt
    (ctx: McpContext)
    (getModel: unit -> SageFsModel)
    (requestId: RunRequestId)
    (justDispatched: int option)
    (wait: TimeSpan)
    : Task<RunReceipt> =
    let recorded = ref false
    let current () =
      match observeAnywhere requestId (getModel ()), justDispatched with
      | RunReceipt.Unattributable _, Some requested when not recorded.Value -> RunReceipt.Pending (requestId, requested)
      | receipt, _ ->
        recorded.Value <- true
        receipt
    task {
      match isSettled (current ()) || wait <= TimeSpan.Zero, ctx.StateChanged with
      | true, _
      | _, None -> return current ()
      | false, Some changed ->
        let settled = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        use _subscription = changed.Subscribe(fun _ -> match isSettled (current ()) with true -> settled.TrySetResult () |> ignore | false -> ())
        // Look again after subscribing, so a change between the first look and the
        // subscription is not lost.
        match isSettled (current ()) with
        | true -> return current ()
        | false ->
          let! _ = Task.WhenAny(settled.Task, Task.Delay wait)
          return current ()
    }

  // ── what source a receipt reflects ───────────────────────────────────────────────────────
  //
  // The engine's record says what the tests did. It cannot say whether the build they ran against was behind the files on disk,
  // because it never looks at the disk. The daemon does, at the edge: once when a run is dispatched, and once the first time
  // the run is seen settled. The receipt carries the worse of the two (a file edited during the run counts), and that reading
  // is frozen when the run settles, so the same receipt_id never reads differently because of an edit made after it finished.

  [<RequireQualifiedAccess>]
  type private SourceStamp =
    /// Read when the run was dispatched; the run has not been seen settled.
    | AtDispatch of SourceState
    /// The worse of the dispatch reading and the one taken when the run was first seen settled. Final.
    | Settled of SourceState

  /// The stamps of recent runs, as many as the engine keeps requests for, twice over: a stamp for a request the engine has
  /// forgotten is never read, and the bound keeps this from outliving the daemon's memory of the runs.
  type private Stamps() =
    let gate = obj ()
    let stamps = System.Collections.Generic.Dictionary<RunRequestId, SourceStamp>()
    let order = System.Collections.Generic.Queue<RunRequestId>()
    let capacity = RequestedRuns.maxTracked * 2
    member _.Find (requestId: RunRequestId) : SourceStamp voption =
      lock gate (fun () -> match stamps.TryGetValue requestId with | true, stamp -> ValueSome stamp | false, _ -> ValueNone)
    /// A new reading for the request. Once a request is `Settled` it keeps that reading.
    member _.Put (requestId: RunRequestId) (stamp: SourceStamp) : SourceStamp =
      lock gate (fun () ->
        match stamps.TryGetValue requestId with
        | true, (SourceStamp.Settled _ as final) -> final
        | true, _ ->
          stamps[requestId] <- stamp
          stamp
        | false, _ ->
          stamps[requestId] <- stamp
          order.Enqueue requestId
          while order.Count > capacity do
            stamps.Remove (order.Dequeue ()) |> ignore
          stamp)

  let private runStamps = Stamps()

  /// The source state of a session as the daemon can read it now.
  let private sourceOfSession (ctx: McpContext) (sid: string) : Task<SourceState> =
    task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      let! warmup = warmupOf ctx sid
      return SourceStateProbe.ofSessionRecord info warmup
    }

  /// A receipt with the source it reflects. A run that has not finished has no verdict to qualify, so only a settled run is
  /// read against the disk, and only once.
  let private withReflectedSource (ctx: McpContext) (receipt: RunReceipt) : Task<RunReceipt> =
    task {
      match receipt with
      | RunReceipt.Ran ran ->
        let settle (atDispatch: SourceState) =
          task {
            let! atCompletion = sourceOfSession ctx ran.Session
            match runStamps.Put ran.RequestId (SourceStamp.Settled (SourceState.worse atDispatch atCompletion)) with
            | SourceStamp.Settled source
            | SourceStamp.AtDispatch source -> return TestRunReceipt.withSource source receipt
          }
        match runStamps.Find ran.RequestId with
        | ValueSome (SourceStamp.Settled source) -> return TestRunReceipt.withSource source receipt
        | ValueSome (SourceStamp.AtDispatch atDispatch) -> return! settle atDispatch
        // No reading was taken when this run was dispatched (its stamp aged out), so the run cannot be called in sync.
        | ValueNone -> return! settle (SourceState.Unknown UnknownReason.NotAssessed)
      | RunReceipt.Refused _
      | RunReceipt.Pending _
      | RunReceipt.Started _
      | RunReceipt.Unattributable _ -> return receipt
    }

  let private describeFilters (request: RunTestsRequest) : string =
    [ request.Pattern |> Option.map (sprintf "pattern=%s")
      request.File |> Option.map (sprintf "file=%s")
      request.Category |> Option.map (sprintf "category=%A") ]
    |> List.choose id
    |> function
      | [] -> "no filters"
      | parts -> String.concat ", " parts

  let runTests (ctx: McpContext) (agent: string) (request: RunTestsRequest) : Task<RunTestsOutcome> =
    task {
      match ctx.Dispatch, ctx.GetElmModel with
      | Some dispatch, Some getModel ->
        match request.Continue with
        | Some requestId ->
          let! observed = awaitReceipt ctx getModel requestId None request.Wait
          let! receipt = withReflectedSource ctx observed
          return RunTestsOutcome.Receipt receipt
        | None ->
          let! resolution = resolveSessionId ctx agent request.SessionId request.WorkingDirectory
          match resolution with
          | Routable sid ->
            let cycleState () = (SageFsModel.cycleOwnedBySession sid (getModel ())).TestState
            let discovered = (cycleState ()).DiscoveredTests
            let matched =
              LiveTestCycleState.filterTestsForExplicitRun discovered request.File request.Pattern request.Category
            // Whether the build is behind the files is not a reason to refuse a run: the receipt says it (`source`).
            let! observation = sessionStatusObservation ctx sid None
            let trust = Verification.SessionTrust.classify observation
            match TestRunReceipt.plan trust discovered matched (describeFilters request) with
            | RunPlan.Refuse refusal -> return RunTestsOutcome.Receipt (RunReceipt.Refused refusal)
            | RunPlan.Run cases ->
              // The same attribution check the cohort landing gate makes: the tests must have been
              // discovered into THIS session's own cycle, or the run would verify another session's code.
              match CohortLandingVerify.preflight sid (cases |> Array.map (fun tc -> tc.Id) |> List.ofArray) (cycleState ()) with
              | CohortLandingVerify.Preflight.NotAttributed ids ->
                return RunTestsOutcome.Receipt (RunReceipt.Refused (RunRefusal.NotAttributed ids))
              | CohortLandingVerify.Preflight.NotDiscovered
              | CohortLandingVerify.Preflight.NothingToRun ->
                return RunTestsOutcome.Receipt (RunReceipt.Refused RunRefusal.NotDiscovered)
              | CohortLandingVerify.Preflight.Dispatch dispatchCases ->
                let requestId = RunRequestId.fresh ()
                // The disk, as it is at the moment the run is asked for.
                let! atDispatch = sourceOfSession ctx sid
                runStamps.Put requestId (SourceStamp.AtDispatch atDispatch) |> ignore
                dispatch (SageFsMsg.Event (TuiEvent.RunTestsRequested (Some sid, dispatchCases, Some requestId)))
                let! observed = awaitReceipt ctx getModel requestId (Some dispatchCases.Length) request.Wait
                let! receipt = withReflectedSource ctx observed
                return RunTestsOutcome.Receipt receipt
          | other ->
            let! blocker = sessionRoutingError ctx request.SessionId request.WorkingDirectory other
            return RunTestsOutcome.NotRoutable (formatSessionResolution other, blocker)
      | _ -> return RunTestsOutcome.NoEngine
    }

  /// The category names a caller may filter on, one spelling each. Anything else is a
  /// custom category; blank means no filter.
  let parseCategory (text: string) : TestCategory option =
    match (match isNull text with true -> "" | false -> text).Trim().ToLowerInvariant() with
    | "" -> None
    | "unit" -> Some TestCategory.Unit
    | "integration" -> Some TestCategory.Integration
    | "browser" -> Some TestCategory.Browser
    | "benchmark" -> Some TestCategory.Benchmark
    | "architecture" -> Some TestCategory.Architecture
    | "property" -> Some TestCategory.Property
    | other -> Some (TestCategory.Custom other)
