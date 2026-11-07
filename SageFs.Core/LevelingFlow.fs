namespace SageFs

open System
open SageFs.WorkerProtocol

/// The daemon's half of bringing a session's host level, apart from the mailbox: the work that asks the worker and fetches again,
/// and the reads the manager makes before and after one. The manager owns the state; this owns what is done to the worker.
module LevelingFlow =

  /// Whether a level, or the fetch after a rebuilt host, is already out to the worker this session now has. An entry for a worker
  /// that has been replaced does not count: the pid is what it is keyed by.
  let flightOf (levels: Map<SessionId, int>) (id: SessionId) (status: SessionLifecycleStatus) : LevelFlight =
    match Map.tryFind id levels, SessionLifecycleStatus.workerPid status with
    | Some asked, Some serving when asked = serving -> LevelFlight.LevelInFlight
    | Some _, _
    | None, _ -> LevelFlight.NoLevelInFlight

  /// The in-flight mark once the answer from `askedPid` has landed: gone, if it was the mark of that worker.
  let landed (levels: Map<SessionId, int>) (id: SessionId) (askedPid: int) : Map<SessionId, int> =
    match Map.tryFind id levels with
    | Some asked when asked = askedPid -> Map.remove id levels
    | Some _
    | None -> levels

  /// What an answer does to a session's state: nothing, because it is from a worker the session no longer has, or the new state.
  [<RequireQualifiedAccess>]
  type AnswerStep =
    | Drop
    | Settle of ReplFreshness

  let onAnswer (askedPid: int) (status: SessionLifecycleStatus) (freshness: ReplFreshness) (answer: LevelAnswer) : AnswerStep =
    match WorkerPostReady.RefetchGate.judgeLevelAnswer askedPid (Some status) with
    | WorkerPostReady.ReplyFate.Stale -> AnswerStep.Drop
    | WorkerPostReady.ReplyFate.Current -> AnswerStep.Settle (ReplFreshness.afterLevelAnswer freshness answer)

  /// A failure that is a value: the exception's text, as the level's answer.
  let private failed (outcome: Choice<LevelAnswer, exn>) : LevelAnswer =
    match outcome with
    | Choice1Of2 answer -> answer
    | Choice2Of2 error -> LevelAnswer.LevelFailed error.Message

  /// Ask the worker to level its host (or take it that something else just did), then fetch its tests and maps again and wait
  /// for both. `Leveled` is only ever the answer once the daemon holds what the new host reports, and a fetch that failed is
  /// `LevelFailed` with the reason: the state is never called level on a guess. `onFetched` receives every answer a fetch gets.
  let work (label: string) (proxy: SessionProxy) (levelWork: LevelWork) (onFetched: WorkerPostReady.RefetchedState -> unit) : Async<LevelAnswer> =
    let askWorker =
      async {
        match! proxy (WorkerMessage.LevelRepl(Guid.NewGuid().ToString("N"))) with
        | WorkerResponse.LevelResult(_, answer) -> return answer
        | WorkerResponse.WorkerError error -> return LevelAnswer.LevelFailed(SageFsError.describe error)
        | other -> return LevelAnswer.LevelFailed(sprintf "the worker answered a level ask with %A" other)
      }
    let hostAnswer =
      match levelWork with
      | LevelWork.AskWorker -> askWorker
      | LevelWork.WorkerRebuiltItself -> async { return LevelAnswer.Leveled }
    let fetchAgain =
      async {
        match! WorkerPostReady.refetchAwaiting label proxy onFetched with
        | WorkerPostReady.RefetchOutcome.Refetched -> return LevelAnswer.Leveled
        | WorkerPostReady.RefetchOutcome.RefetchFailed reason -> return LevelAnswer.LevelFailed reason
      }
    async {
      let! asked = hostAnswer |> Async.Catch
      match failed asked with
      | LevelAnswer.Leveled ->
        let! fetched = fetchAgain |> Async.Catch
        return failed fetched
      | LevelAnswer.LevelDeferred _
      | LevelAnswer.LevelFailed _ as other -> return other
    }
