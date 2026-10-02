/// The dispatch decisions of the matrix, pure: what to start next, what to skip, and when to
/// stop sending a model more work because its free quota is gone.
module LemMatrix.Scheduler

open LemScore.Types
open LemMatrix.Plan

/// After this many provider/quota errors a model gets no more runs in this matrix. Free models
/// have daily provider limits; hammering one after it said no helps nobody.
let defaultQuotaLimit = 2

/// How many lemmings run at once unless told otherwise.
let defaultConcurrency = 3

type State =
  { Pending: Cell list
    Running: int
    QuotaHits: Map<string, int>
    /// Set when the shared daemon refused a run: nothing more is dispatched.
    Aborted: string option }

type Step =
  | Dispatch of Cell
  | Skip of Cell * reason: string
  | Wait
  | Finished

let start (cells: Cell list) : State =
  { Pending = cells; Running = 0; QuotaHits = Map.empty; Aborted = None }

let quotaHits (state: State) (model: string) : int =
  state.QuotaHits |> Map.tryFind model |> Option.defaultValue 0

let isExhausted (limit: int) (state: State) (model: string) : bool =
  quotaHits state model >= limit

/// The next thing to do. Skips come first and one at a time, so each skipped cell gets its own
/// row in the table; then a dispatch if there is a free slot; then wait or finish.
let next (limit: int) (concurrency: int) (state: State) : Step * State =
  let skipReason (cell: Cell) : string option =
    match state.Aborted with
    | Some why -> Some (sprintf "matrix aborted: %s" why)
    | None when isExhausted limit state cell.Model -> Some (sprintf "%s hit its quota %d times, so no more runs were sent to it" cell.Model limit)
    | None -> None
  match state.Pending |> List.tryFind (fun c -> (skipReason c).IsSome) with
  | Some cell ->
    let reason = skipReason cell |> Option.defaultValue "skipped"
    Skip (cell, reason), { state with Pending = state.Pending |> List.filter (fun c -> c.RunId <> cell.RunId) }
  | None ->
    match state.Pending, state.Running < concurrency with
    | cell :: rest, true -> Dispatch cell, { state with Pending = rest; Running = state.Running + 1 }
    | [], _ when state.Running = 0 -> Finished, state
    | _ -> Wait, state

/// A run ended. A ProviderQuota outcome counts against its model.
let finished (outcome: Outcome option) (model: string) (state: State) : State =
  let hits =
    match outcome with
    | Some ProviderQuota -> state.QuotaHits |> Map.add model (quotaHits state model + 1)
    | _ -> state.QuotaHits
  { state with Running = state.Running - 1; QuotaHits = hits }

let abort (why: string) (state: State) : State =
  match state.Aborted with
  | Some _ -> state
  | None -> { state with Aborted = Some why }
