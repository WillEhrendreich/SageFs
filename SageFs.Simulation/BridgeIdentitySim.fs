namespace SageFs.Simulation

open System
open SageFs
open SageFs.Cohort
open SageFs.MemberTable

/// Deterministic simulation of stdio bridges that connect, call, lose their stream, ride through daemon restarts and
/// go quiet for longer than the cohort's lease, folded through the REAL `ConnectionIdentity.resolve`,
/// `ClientKey.toMemberId`, `MemberLiveness` and `Cohort.decide`.
///
/// What it models of the daemon (the facts measured on 2026-11-07, findings C3 and C5): a re-initialize mints a NEW
/// session id and every call arrives on the latest one; a daemon restart ends every session and every open stream but
/// keeps the cohort (the ledger replays it) and forgets the liveness table; a bridge re-initializes by itself on its
/// next call, with the key it has held since the process started. The reaper runs every minute: it renews each Present
/// member that is live and then ticks, so a member silent past the lease is departed.
///
/// `Real` is the product. `SessionIdIdentityTwin` is the daemon before the client key: the member id is a fingerprint of
/// the session id. `CallsOnlyLeaseTwin` is the reaper before the stream counted: only calls keep a member live.
module BridgeIdentitySim =

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// A bridge starts: it initializes (new session) and opens its push stream.
    | Connect of bridge: int
    /// The bridge forwards a tool call. A bridge whose session the daemon forgot re-initializes first, as the product does.
    | Call of bridge: int
    /// The bridge's push stream closes while the bridge and its session stay.
    | StreamDrops of bridge: int
    /// The daemon stops and starts again: sessions and streams are gone, the cohort replays, the liveness table is empty.
    | DaemonRestart
    /// Quiet time, as a number of reaper ticks.
    | Idle of ticks: int

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    | SessionIdIdentityTwin
    | CallsOnlyLeaseTwin

  type Bridge =
    { Key: string
      /// The session id the daemon holds for this bridge right now, if it holds one.
      Session: string option
      StreamOpen: bool }

  type State =
    { Clock: DateTime
      Cohort: CohortState<MemberId>
      Bridges: Map<int, Bridge>
      Liveness: MemberLiveness.Liveness
      /// The member id each bridge's calls resolved to the first time, and the latest time.
      FirstId: Map<int, MemberId>
      LastId: Map<int, MemberId>
      Sessions: int
      Step: int }

  let private t0 = DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)

  let private machine = CohortScope.Machine

  let private entropy : Entropy = [| 1uy |]

  let private tick = Timeouts.cohortReaperInterval

  /// A number of reaper ticks that covers one and a half lease windows: a quiet spell that outlasts the lease.
  let pastTheLease : int = int (Math.Ceiling(1.5 * Cohort.leaseWindow.TotalSeconds / tick.TotalSeconds))

  let private initial : State =
    { Clock = t0
      Cohort = CohortState.empty ()
      Bridges = Map.empty
      Liveness = MemberLiveness.empty
      FirstId = Map.empty
      LastId = Map.empty
      Sessions = 0
      Step = 0 }

  /// A key as the bridge would generate it, but replayable by seed: 32 bytes in base64url.
  let keyOf (seed: int) (bridge: int) : string =
    System.Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(sprintf "bridge-key-%d-%d" seed bridge))
    |> Convert.ToBase64String
    |> fun text -> text.TrimEnd('=').Replace('+', '-').Replace('/', '_')

  let private keyText (b: Bridge) = b.Key

  let private idOf (behavior: Behavior) (bridge: Bridge) (session: string) : MemberId =
    let clientKey =
      match behavior with
      | Behavior.SessionIdIdentityTwin -> None
      | Behavior.Real
      | Behavior.CallsOnlyLeaseTwin ->
        match ClientKey.toMemberId (keyText bridge) with
        | Result.Ok who -> Some who
        | Result.Error _ -> None
    ConnectionIdentity.resolve None clientKey (Some session) "bridge"

  let private decideOrKeep (clock: DateTime) (cohort: CohortState<MemberId>) (command: CohortCommand<MemberId>) : CohortState<MemberId> =
    match decide clock entropy cohort command with
    | Result.Ok(next, _, _) -> next
    | Result.Error _ -> cohort

  /// One reaper tick: renew every Present member that is live, then tick the cohort.
  let private reaperTick (renewable: (MemberId -> bool) -> Map<MemberId, MemberRecord> -> MemberId list) (window: TimeSpan) (state: State) : State =
    let clock = state.Clock + tick
    let live (who: MemberId) = MemberLiveness.isLive (MemberId.display who) clock window state.Liveness
    let renewed =
      renewable live state.Cohort.Members
      |> List.fold (fun cohort who -> decideOrKeep clock cohort (CohortCommand.RenewLease(who, machine))) state.Cohort
    { state with Clock = clock; Cohort = decideOrKeep clock renewed (CohortCommand.Tick machine) }

  let private connect (behavior: Behavior) (state: State) (index: int) (key: string) : State =
    let session = sprintf "session-%d" (state.Sessions + 1)
    let bridge = { Key = key; Session = Some session; StreamOpen = true }
    let liveness =
      match behavior with
      | Behavior.CallsOnlyLeaseTwin -> state.Liveness
      | Behavior.Real
      | Behavior.SessionIdIdentityTwin ->
        MemberLiveness.streamOpened (MemberId.display (idOf behavior bridge session)) state.Liveness
    { state with Bridges = Map.add index bridge state.Bridges; Sessions = state.Sessions + 1; Liveness = liveness }

  let private call (behavior: Behavior) (state: State) (index: int) : State =
    match Map.tryFind index state.Bridges with
    | None -> state
    | Some bridge ->
      // A bridge whose session is gone re-initializes by itself, with the same key, and reopens its stream.
      let healed =
        match bridge.Session with
        | Some _ -> state
        | None -> connect behavior state index bridge.Key
      let current = healed.Bridges.[index]
      let session = Option.get current.Session
      let who = idOf behavior current session
      let cohort =
        match Map.tryFind who healed.Cohort.Members with
        | Some { Presence = MemberPresence.Present } -> healed.Cohort
        | Some _
        | None -> decideOrKeep healed.Clock healed.Cohort (CohortCommand.Join(who, JoinableRole.Implementer, None, machine))
      { healed with
          Cohort = cohort
          Liveness = MemberLiveness.callMade (MemberId.display who) healed.Clock healed.Liveness
          FirstId = (match Map.tryFind index healed.FirstId with Some _ -> healed.FirstId | None -> Map.add index who healed.FirstId)
          LastId = Map.add index who healed.LastId }

  let private apply
    (behavior: Behavior)
    (renewable: (MemberId -> bool) -> Map<MemberId, MemberRecord> -> MemberId list)
    (window: TimeSpan)
    (seed: int)
    (state: State)
    (event: SimEvent)
    : State =
    let stepped = { state with Step = state.Step + 1 }
    match event with
    | SimEvent.Connect index ->
      match Map.tryFind index stepped.Bridges with
      | Some _ -> stepped
      | None -> connect behavior stepped index (keyOf seed index)
    | SimEvent.Call index -> call behavior stepped index
    | SimEvent.StreamDrops index ->
      match Map.tryFind index stepped.Bridges with
      | Some({ Session = Some session; StreamOpen = true } as bridge) ->
        let liveness =
          match behavior with
          | Behavior.CallsOnlyLeaseTwin -> stepped.Liveness
          | Behavior.Real
          | Behavior.SessionIdIdentityTwin -> MemberLiveness.streamClosed (MemberId.display (idOf behavior bridge session)) stepped.Liveness
        { stepped with Bridges = Map.add index { bridge with StreamOpen = false } stepped.Bridges; Liveness = liveness }
      | Some _
      | None -> stepped
    | SimEvent.DaemonRestart ->
      // Every session and stream ends and the liveness table is empty. Each bridge notices as soon as the daemon is
      // back (its probe), re-initializes with the key it has held all along and opens a new stream, without waiting
      // for the client to say anything.
      let down = { stepped with Bridges = stepped.Bridges |> Map.map (fun _ bridge -> { bridge with Session = None; StreamOpen = false }); Liveness = MemberLiveness.empty }
      down.Bridges |> Map.fold (fun s index bridge -> connect behavior s index bridge.Key) down
    | SimEvent.Idle ticks ->
      [ 1 .. ticks ] |> List.fold (fun s _ -> reaperTick renewable window s) stepped

  /// Fold the events; the state after each one. `renewable` is the reaper's selection (the product's
  /// `DaemonMode.cohortMembersToRenew`, passed in by the test because this project does not reference the daemon).
  let run
    (behavior: Behavior)
    (renewable: (MemberId -> bool) -> Map<MemberId, MemberRecord> -> MemberId list)
    (window: TimeSpan)
    (seed: int)
    (events: SimEvent list)
    : State list =
    events
    |> List.scan (apply behavior renewable window seed) initial
    |> List.tail

  /// A seeded lifecycle of `bridges` clients: calls, dropped streams, daemon restarts and long quiet spells that run past the lease.
  let scenarioOf (seed: int) (bridges: int) (steps: int) : SimEvent list =
    let rnd = Random(seed)
    let longestIdle = pastTheLease
    [ for index in 0 .. bridges - 1 do
        yield SimEvent.Connect index
        yield SimEvent.Call index
      for _ in 1 .. steps do
        match rnd.Next 12 with
        | 0 | 1 | 2 | 3 -> yield SimEvent.Call(rnd.Next bridges)
        | 4 | 5 -> yield SimEvent.StreamDrops(rnd.Next bridges)
        | 6 | 7 -> yield SimEvent.DaemonRestart
        | 8 -> yield SimEvent.Connect(rnd.Next bridges)
        | _ -> yield SimEvent.Idle(1 + rnd.Next longestIdle) ]
