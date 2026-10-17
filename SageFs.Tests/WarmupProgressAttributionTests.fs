module SageFs.Tests.WarmupProgressAttributionTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.SharedGenerators

let sessionA = testSessionId "aa000001"
let sessionB = testSessionId "bb000002"
let sessionC = testSessionId "cc000003"
let sessionD = testSessionId "dd000004"
let allSessions = [| sessionA; sessionB; sessionC; sessionD |]

let viewing (sid: WorkerProtocol.SessionId) : SageFsModel =
  let m = SageFsModel.initial ()
  { m with Sessions = { m.Sessions with ActiveSessionId = ActiveSession.Viewing sid } }

let progress (sid: WorkerProtocol.SessionId) (step: int) =
  SageFsMsg.Event (TuiEvent.WarmupProgress (sid, step, 99, sprintf "step %d" step))

/// One input to the coalescer: a progress event for session `Session`, or a
/// message that is not progress and must pass through untouched.
type Input =
  | Progress of session: int * step: int
  | Marker of int

let toMsg (input: Input) : SageFsMsg =
  match input with
  | Progress (s, step) -> progress allSessions.[s] step
  | Marker n -> SageFsMsg.Event (TuiEvent.SessionStopped (sprintf "marker-%d" n))

/// Feed every input through an absorber the way the Elm loop does: a message
/// that is not absorbed is appended.
let drain (absorb: ResizeArray<SageFsMsg> -> SageFsMsg -> bool) (inputs: Input list) : SageFsMsg list =
  let pending = ResizeArray<SageFsMsg>()
  for input in inputs do
    let msg = toMsg input
    match absorb pending msg with
    | true -> ()
    | false -> pending.Add msg
  List.ofSeq pending

let isProgress (msg: SageFsMsg) =
  match msg with
  | SageFsMsg.Event (TuiEvent.WarmupProgress _) -> true
  | _ -> false

/// The twin: replaces the last WarmupProgress whatever session it belongs to.
/// If the property below passes for this, the property has no teeth.
let sessionBlindAbsorber (pending: ResizeArray<SageFsMsg>) (incoming: SageFsMsg) : bool =
  match incoming with
  | SageFsMsg.Event (TuiEvent.WarmupProgress _) ->
    let idx = pending.FindLastIndex(fun m -> isProgress m)
    match idx < 0 with
    | true -> false
    | false ->
      pending.[idx] <- incoming
      true
  | _ -> false

/// Property: after absorbing, pending holds exactly the LAST progress event
/// per session, and every non-progress message keeps its order.
let keepsLastPerSession (absorb: ResizeArray<SageFsMsg> -> SageFsMsg -> bool) (inputs: Input list) : bool =
  let result = drain absorb inputs
  let lastPerSession =
    inputs
    |> List.choose (function Progress (s, step) -> Some (s, step) | Marker _ -> None)
    |> List.groupBy fst
    |> List.map (fun (s, xs) -> toMsg (Progress (s, snd (List.last xs))))
  let keptProgress = result |> List.filter isProgress
  let sameSet =
    List.length keptProgress = List.length lastPerSession
    && lastPerSession |> List.forall (fun m -> List.contains m keptProgress)
  let othersIn = inputs |> List.choose (function Marker n -> Some (toMsg (Marker n)) | Progress _ -> None)
  let othersOut = result |> List.filter (isProgress >> not)
  sameSet && othersIn = othersOut

let genInput : Gen<Input> =
  Gen.oneof [
    Gen.map2 (fun s step -> Progress (s, step)) (Gen.choose (0, 3)) (Gen.choose (1, 50))
    Gen.map Marker (Gen.choose (0, 5))
  ]

let genInputs : Gen<Input list> = Gen.listOf genInput

[<Tests>]
let warmupProgressAttributionTests =
  testList "warmup progress attribution" [

    testCase "progress for session A lands in A's output and not in B's while B is viewed" <| fun _ ->
      let model, effects =
        SageFsUpdate.update (progress sessionA 2) (viewing sessionB)
      effects |> Expect.isEmpty "progress has no effects"
      let a = model.RecentOutput.GetBuffer(WorkerProtocol.SessionId.value sessionA)
      let b = model.RecentOutput.GetBuffer(WorkerProtocol.SessionId.value sessionB)
      a |> Expect.hasLength "A's buffer holds the line" 1
      a.[0].Text |> Expect.stringContains "the line carries its step" "[2/99]"
      b |> Expect.isEmpty "B's buffer never sees A's warmup"

    testCase "progress for the viewed session still lands in that session's output" <| fun _ ->
      let model, _ = SageFsUpdate.update (progress sessionB 3) (viewing sessionB)
      let b = model.RecentOutput.GetBuffer(WorkerProtocol.SessionId.value sessionB)
      b |> Expect.hasLength "one line in the viewed session" 1
      b.[0].Kind |> Expect.equal "progress is Info" OutputKind.Info
      b.[0].Text |> Expect.stringContains "the line carries its step" "[3/99]"

    testPropertyWithConfig propConfig "tryAbsorbPending keeps the last progress per session, never merges sessions, never reorders others"
      (Prop.forAll (Arb.fromGen genInputs) (fun inputs ->
        keepsLastPerSession SageFsMsgQueueCoalescing.tryAbsorbPending inputs))

    testCase "the property has teeth: an absorber blind to the session fails it" <| fun _ ->
      let interleaved = [ Progress (0, 1); Progress (1, 2) ]
      keepsLastPerSession SageFsMsgQueueCoalescing.tryAbsorbPending interleaved
      |> Expect.isTrue "the real absorber keeps both sessions' lines"
      keepsLastPerSession sessionBlindAbsorber interleaved
      |> Expect.isFalse "the session-blind twin merges A into B and loses a line"
  ]
