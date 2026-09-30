namespace SageFs

open System
open SageFs.Utils
open SageFs.WorkerProtocol

/// What to do when a worker says it is Ready, decided without touching a
/// process, a proxy or the mailbox. Split out of SessionManager's WorkerReady
/// arm, which mixed this decision with the retirement thread and three
/// post-ready fetches.
///
/// The plan is generic over the session type because ManagedSession lives in
/// SessionManager.fs, after this file. `SessionOps` is the only thing it needs
/// to know about a session: its worker pid, and how to install a transport.
///
/// ORDER IS LOAD-BEARING and is the order the arm always had: the pid guard
/// first, then the transport check, then the retirement. The WorkerExited
/// arm's stale-exit handling depends on the registry already pointing at the
/// new pid by the time the old worker's exit event arrives.
module WorkerReadyCommit =

  /// An old session parked during a spawn-first restart, or none.
  [<RequireQualifiedAccess>]
  type ParkedSwap<'S> =
    | NoSwap
    | Parked of oldSession: 'S

  module ParkedSwap =
    let ofOption (parked: 'S option) : ParkedSwap<'S> =
      match parked with
      | Some old -> ParkedSwap.Parked old
      | None -> ParkedSwap.NoSwap

  /// Whether the worker now registered is the one that was already registered
  /// or a replacement for it. A replacement takes any hosted app with it.
  [<RequireQualifiedAccess>]
  type WorkerContinuity =
    | SameWorker
    | ReplacesWorker

  /// The transport a ready worker handed over, ready to install.
  type Transport =
    { BaseUrl: string
      Proxy: SessionProxy
      WorkerPid: int
      Port: int option
      Continuity: WorkerContinuity }

  type SessionOps<'S> =
    { WorkerPid: 'S -> int option
      Install: Transport -> 'S -> 'S }

  [<RequireQualifiedAccess>]
  type RetireOld<'S> =
    | NothingParked
    | Retire of oldSession: 'S

  type Committed<'S> =
    { Updated: 'S
      RetireOld: RetireOld<'S> }

  /// What a bad transport leaves behind. With a swap pending the old worker is
  /// still serving and comes back; otherwise there is nothing to restore and
  /// the session is tombstoned.
  [<RequireQualifiedAccess>]
  type Rejected<'S> =
    | NoSwapParked
    | RestoreOld of oldSession: 'S

  [<RequireQualifiedAccess>]
  type Plan<'S> =
    /// A straggler ready from a worker that is no longer the one awaited.
    | IgnoreStale
    /// The worker said Ready without a usable transport. The caller reaps it.
    | RejectTransport of message: string * Rejected<'S>
    | Commit of Committed<'S>

  let private portOf (baseUrl: string) : int option =
    let mutable uri : Uri = null
    match Uri.TryCreate(baseUrl, UriKind.Absolute, &uri) with
    | true when uri.Port > 0 -> Some uri.Port
    | _ -> None

  /// Decide what a WorkerReady does. `session` is the registered session (during
  /// a swap, the replacement-in-waiting), `parked` the old session parked by a
  /// spawn-first restart.
  let plan
    (ops: SessionOps<'S>)
    (session: 'S)
    (parked: ParkedSwap<'S>)
    (baseUrl: string)
    (proxy: SessionProxy)
    (eventPid: int)
    : Plan<'S> =
    let parkedPid =
      match parked with
      | ParkedSwap.Parked old -> ops.WorkerPid old
      | ParkedSwap.NoSwap -> None
    match WorkerEventGuard.classifyReady (ops.WorkerPid session) parkedPid eventPid with
    | WorkerEventGuard.ReadyDecision.IgnoreStale -> Plan.IgnoreStale
    | WorkerEventGuard.ReadyDecision.Commit ->
      match ReadyTransport.isValid baseUrl proxy with
      | false ->
        let rejected =
          match parked with
          | ParkedSwap.Parked old -> Rejected.RestoreOld old
          | ParkedSwap.NoSwap -> Rejected.NoSwapParked
        Plan.RejectTransport (ReadyTransport.describeInvalid "Worker" baseUrl proxy, rejected)
      | true ->
        let continuity, retire =
          match parked with
          | ParkedSwap.Parked old -> WorkerContinuity.ReplacesWorker, RetireOld.Retire old
          | ParkedSwap.NoSwap -> WorkerContinuity.SameWorker, RetireOld.NothingParked
        let transport =
          { BaseUrl = baseUrl
            Proxy = proxy
            WorkerPid = eventPid
            Port = portOf baseUrl
            Continuity = continuity }
        Plan.Commit { Updated = ops.Install transport session; RetireOld = retire }

  /// Reap the outgoing worker of a committed swap.
  ///
  /// SAFETY-CRITICAL: a dedicated thread, NOT Async.Start (the pool). A
  /// pool-queued retirement can be starved under memory pressure, leaking
  /// multi-GB workers (observed 2026-09-15). `stop` is invoked SYNCHRONOUSLY so
  /// the retirement intent is registered before the swap returns; the
  /// awaitable it returns runs to completion on the dedicated thread.
  let retireOnDedicatedThread (stop: 'S -> Async<unit>) (id: SessionId) (oldSession: 'S) : unit =
    let retireAsync = stop oldSession
    let retire () =
      try Async.RunSynchronously retireAsync
      with ex ->
        Log.warn "[SessionManager] Old-worker retirement failed for %s: %s" (SessionId.value id) ex.Message
    let thread = System.Threading.Thread(System.Threading.ThreadStart retire)
    thread.IsBackground <- true
    thread.Name <- sprintf "sagefs-retire-%s" (SessionId.value id)
    thread.Start()
