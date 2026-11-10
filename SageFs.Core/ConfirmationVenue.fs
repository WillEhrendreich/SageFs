namespace SageFs

open SageFs.WorkerProtocol

/// ROLE: where a confirming build's tests run, and who keeps the worker that runs them.
///   A confirming build used to replace the session's worker, so the REPL's definitions and the unsaved buffers it had
///   evaluated went with it, and a test held for a debugger died in the worker it was held in. Now a session that holds
///   something a swap would lose is confirmed in a throwaway worker built from the same build: the session's own worker
///   is never touched. A session that holds nothing is swapped as before, which refreshes its coverage maps.
/// Everything here is pure. The daemon gathers the facts at its edge (`SageFs/ConfirmWorkers.fs`), and
/// `SageFs.Simulation.ConfirmThrowawaySim` folds `ThrowawayBook.step` under seeded chaos.

/// What a worker's life has put in it that a swap would lose, as the daemon saw it.
module SessionHandWork =

  /// What one worker holds: how many times a person or an agent evaluated by hand (`EvalCode`), and which files'
  /// editor buffers the live loop evaluated into it (`EvalLiveTestFile`).
  type Held =
    { HandEvals: int
      BufferFiles: Set<string> }

  let nothing : Held = { HandEvals = 0; BufferFiles = Set.empty }

  /// The worker a count belongs to. A count for another worker says nothing about this one: a swap starts from nothing.
  type ForWorker =
    { WorkerPid: int
      Held: Held }

  type Ledger = Map<SessionId, ForWorker>

  /// One session's entry after `change`, counted against `workerPid`. An entry for another worker is dropped: a swap
  /// starts from nothing.
  let changeFor (workerPid: int) (change: Held -> Held) (current: ForWorker option) : ForWorker =
    let basis =
      match current with
      | Some known when known.WorkerPid = workerPid -> known.Held
      | Some _
      | None -> nothing
    { WorkerPid = workerPid; Held = change basis }

  /// A person or an agent evaluated by hand in this worker.
  let noteHandEvalFor (workerPid: int) (current: ForWorker option) : ForWorker =
    changeFor workerPid (fun held -> { held with HandEvals = held.HandEvals + 1 }) current

  /// The live loop evaluated this file's editor buffer into this worker.
  let noteBufferFor (workerPid: int) (file: string) (current: ForWorker option) : ForWorker =
    changeFor workerPid (fun held -> { held with BufferFiles = Set.add file held.BufferFiles }) current

  let noteHandEval (session: SessionId) (workerPid: int) (ledger: Ledger) : Ledger =
    Map.add session (noteHandEvalFor workerPid (Map.tryFind session ledger)) ledger

  let noteBuffer (session: SessionId) (workerPid: int) (file: string) (ledger: Ledger) : Ledger =
    Map.add session (noteBufferFor workerPid file (Map.tryFind session ledger)) ledger

  /// What this worker holds. Nothing for a worker the ledger has not seen.
  let heldBy (session: SessionId) (workerPid: int) (ledger: Ledger) : Held =
    match Map.tryFind session ledger with
    | Some known when known.WorkerPid = workerPid -> known.Held
    | Some _
    | None -> nothing

  /// The files whose evaluated buffer a swap would drop, other than the one the confirmation is about: that file is
  /// on disk, so the build confirms it.
  let buffersElsewhere (confirmedFile: string) (held: Held) : string list =
    held.BufferFiles |> Set.remove confirmedFile |> Set.toList

/// Whether the machine can take a second FSI host while the first one stays up.
[<RequireQualifiedAccess>]
type MemoryHeadroom =
  | Roomy
  /// The memory supervisor refuses new work. The reason is its own sentence.
  | Short of reason: string

/// What the throwaway worker leaves alone, for the status line and the log.
[<RequireQualifiedAccess>]
type KeptState =
  | ReplDefinitions of evals: int
  | UnsavedBuffers of files: string list

/// Why a confirmation was not attempted. The rows say so, with the sentence from `ConfirmRefusal.describe`.
[<RequireQualifiedAccess>]
type ConfirmRefusal =
  /// Confirming here would cost the memory the machine does not have, and a swap would lose what the session holds.
  | MemoryPressure of reason: string
  /// The session starts an app when its worker loads, so a second worker would fight the first for the app's port, and
  /// a swap would lose what the session holds.
  | StartupBindsAtLoad of script: string

[<RequireQualifiedAccess>]
type SwapReason =
  /// The session holds nothing a swap would lose.
  | NothingWouldBeLost
  /// This daemon has no throwaway host (a test harness, or a daemon started without one).
  | NoThrowawayHost

[<RequireQualifiedAccess>]
type Venue =
  /// Build, then replace the session's worker as before. Its coverage maps and its compiled binaries are refreshed.
  | SwapInPlace of SwapReason
  /// Build, then run in a worker that is retired after the run. The session keeps everything it holds.
  | ThrowawayWorker of keeps: KeptState list
  /// Not confirmed. The rows say why and the session is untouched.
  | Refused of ConfirmRefusal

/// What the daemon found out about a session before it confirms.
type VenueFacts =
  { /// The file the confirmation is about.
    File: string
    Held: SessionHandWork.Held
    Startup: RespawnAfterReap.Sequencing
    Memory: MemoryHeadroom }

module ConfirmRefusal =
  let describe (refusal: ConfirmRefusal) : string =
    match refusal with
    | ConfirmRefusal.MemoryPressure reason ->
      sprintf "confirming would need a second worker, and %s. Your REPL state was left alone" reason
    | ConfirmRefusal.StartupBindsAtLoad script ->
      sprintf "confirming would need a second worker, and %s starts an app when a worker loads, which would fight the first for its port. Your REPL state was left alone" script

module Venue =

  /// Where a confirmation runs. Total: every combination of facts has an answer, and an answer that would cost the
  /// session its REPL state is a refusal, never a swap.
  let decide (facts: VenueFacts) : Venue =
    let keeps =
      [ match facts.Held.HandEvals with
        | 0 -> ()
        | evals -> KeptState.ReplDefinitions evals
        match SessionHandWork.buffersElsewhere facts.File facts.Held with
        | [] -> ()
        | files -> KeptState.UnsavedBuffers files ]
    match keeps with
    | [] -> Venue.SwapInPlace SwapReason.NothingWouldBeLost
    | _ ->
      match facts.Startup, facts.Memory with
      | RespawnAfterReap.Sequencing.ReapThenSpawn script, _ -> Venue.Refused (ConfirmRefusal.StartupBindsAtLoad script)
      | RespawnAfterReap.Sequencing.SpawnFirst, MemoryHeadroom.Short reason -> Venue.Refused (ConfirmRefusal.MemoryPressure reason)
      | RespawnAfterReap.Sequencing.SpawnFirst, MemoryHeadroom.Roomy -> Venue.ThrowawayWorker keeps

/// The throwaway workers a daemon holds. At most one per session, and every one of them is retired when its
/// generation ends, when a newer generation replaces it, when its session goes, or when it turns up late. The daemon's
/// owner folds this and performs the effects; it holds the processes and this holds the decisions.
module ThrowawayBook =

  [<RequireQualifiedAccess>]
  type Phase =
    /// Building, then starting the worker.
    | Starting
    /// The worker answers on this handle (its pid).
    | Serving of handle: int

  type Entry =
    { Generation: int64
      Phase: Phase }

  type Book = Map<SessionId, Entry>

  [<RequireQualifiedAccess>]
  type Event =
    /// A confirmation of this generation wants a throwaway.
    | Wanted of SessionId * generation: int64
    /// The worker for this generation is up, and answers on `handle`.
    | Started of SessionId * generation: int64 * handle: int
    /// The build failed or the worker would not start, for this generation.
    | StartFailed of SessionId * generation: int64 * reason: string
    /// The generation is over: its verdict was delivered, or a newer text, a deadline or a failure ended it.
    | Ended of SessionId * generation: int64
    | SessionGone of SessionId

  [<RequireQualifiedAccess>]
  type Effect =
    | Start of SessionId * generation: int64
    /// Stop this worker and delete what it holds.
    | Retire of handle: int
    /// Tell whoever waits for this generation's worker that it is up.
    | Serve of SessionId * generation: int64 * handle: int
    /// Tell whoever waits for this generation's worker that there will not be one, and why.
    | Fail of SessionId * generation: int64 * reason: string

  let empty : Book = Map.empty

  /// What leaving an entry costs: its worker is retired, or the caller waiting for it is told.
  let leaving (session: SessionId) (entry: Entry) (reason: string) : Effect list =
    match entry.Phase with
    | Phase.Serving handle -> [ Effect.Retire handle ]
    | Phase.Starting -> [ Effect.Fail (session, entry.Generation, reason) ]

  let step (book: Book) (event: Event) : Book * Effect list =
    match event with
    | Event.Wanted (session, generation) ->
      match Map.tryFind session book with
      | None -> Map.add session { Generation = generation; Phase = Phase.Starting } book, [ Effect.Start (session, generation) ]
      | Some held when held.Generation >= generation ->
        // An older or repeated ask: the newer one stands.
        book, [ Effect.Fail (session, generation, "a newer confirmation already has the throwaway worker") ]
      | Some held ->
        Map.add session { Generation = generation; Phase = Phase.Starting } book,
        leaving session held "a newer confirmation replaced it" @ [ Effect.Start (session, generation) ]
    | Event.Started (session, generation, handle) ->
      match Map.tryFind session book with
      | Some { Generation = current; Phase = Phase.Starting } when current = generation ->
        Map.add session { Generation = generation; Phase = Phase.Serving handle } book, [ Effect.Serve (session, generation, handle) ]
      // It arrived after its generation ended, or after a newer one took the place: nobody wants it.
      | Some _
      | None -> book, [ Effect.Retire handle ]
    | Event.StartFailed (session, generation, reason) ->
      match Map.tryFind session book with
      | Some { Generation = current; Phase = Phase.Starting } when current = generation ->
        Map.remove session book, [ Effect.Fail (session, generation, reason) ]
      | Some _
      | None -> book, []
    | Event.Ended (session, generation) ->
      match Map.tryFind session book with
      | Some held when held.Generation = generation -> Map.remove session book, leaving session held "its confirmation ended"
      | Some _
      | None -> book, []
    | Event.SessionGone session ->
      match Map.tryFind session book with
      | Some held -> Map.remove session book, leaving session held "its session went"
      | None -> book, []
