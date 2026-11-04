namespace SageFs

/// Where a `Coalescer` is in handing out values: nothing in flight, one being delivered, or one being
/// delivered with a newer one waiting behind it.
[<RequireQualifiedAccess>]
type CoalesceState<'a> =
  | Idle
  | Delivering
  | DeliveringWithNewer of 'a

/// What the caller of a transition does next.
[<RequireQualifiedAccess>]
type CoalesceStep<'a> =
  | StartDelivery of 'a
  | Wait

/// The rules, as pure transitions. At most one delivery is in flight. A value that arrives while one is
/// in flight waits, and a newer one replaces it: whoever reads the value wants the latest, and has the
/// model to read besides, so the ones in between were already out of date when they were posted.
module Coalesce =
  /// `merge older newer` says what waits when a value arrives while one already waits. Latest-wins is
  /// `fun _ newer -> newer`; a caller whose values carry facts that exist nowhere else (a session that
  /// stopped is not in the model afterwards) merges them together instead.
  let postWith (merge: 'a -> 'a -> 'a) (value: 'a) (state: CoalesceState<'a>) : CoalesceState<'a> * CoalesceStep<'a> =
    match state with
    | CoalesceState.Idle -> CoalesceState.Delivering, CoalesceStep.StartDelivery value
    | CoalesceState.Delivering -> CoalesceState.DeliveringWithNewer value, CoalesceStep.Wait
    | CoalesceState.DeliveringWithNewer older -> CoalesceState.DeliveringWithNewer (merge older value), CoalesceStep.Wait

  let post (value: 'a) (state: CoalesceState<'a>) : CoalesceState<'a> * CoalesceStep<'a> =
    postWith (fun _ newer -> newer) value state

  /// The delivery in flight has ended.
  let finished (state: CoalesceState<'a>) : CoalesceState<'a> * CoalesceStep<'a> =
    match state with
    | CoalesceState.DeliveringWithNewer newer -> CoalesceState.Delivering, CoalesceStep.StartDelivery newer
    | CoalesceState.Idle
    | CoalesceState.Delivering -> CoalesceState.Idle, CoalesceStep.Wait

/// Hands values to `deliver` one at a time, newest first when they pile up. `start` runs a piece of work on
/// another thread (the pool, in the daemon), so `Post` never waits for a delivery.
///
/// The daemon's model-change hook used to queue one pool work item per change, and each ran every
/// subscriber to completion on its own thread. A change that arrived while an earlier one was still
/// projecting every file of a big session started a second projection beside it; the handlers' state
/// (`ref` cells written with `<-`) was written from both, and a burst of N output lines meant N passes.
type Coalescer<'a>(deliver: 'a -> unit, start: (unit -> unit) -> unit, merge: 'a -> 'a -> 'a) =
  let gate = obj ()
  let mutable state : CoalesceState<'a> = CoalesceState.Idle

  let step (transition: CoalesceState<'a> -> CoalesceState<'a> * CoalesceStep<'a>) : CoalesceStep<'a> =
    lock gate (fun () ->
      let state', next = transition state
      state <- state'
      next)

  let rec run (first: 'a) : unit =
    let mutable next = Some first
    while Option.isSome next do
      let current = next.Value
      next <- None
      let mutable delivered = false
      try
        deliver current
        delivered <- true
      finally
        match step Coalesce.finished with
        | CoalesceStep.Wait -> ()
        // A delivery that threw ends this work item, as it always did, but the value waiting behind it
        // still gets its own.
        | CoalesceStep.StartDelivery newer ->
          match delivered with
          | true -> next <- Some newer
          | false -> start (fun () -> run newer)

  /// Latest wins: a value that waits is replaced by the next one to arrive.
  new(deliver: 'a -> unit, start: (unit -> unit) -> unit) = Coalescer<'a>(deliver, start, (fun _ newer -> newer))

  member _.Post(value: 'a) : unit =
    match step (Coalesce.postWith merge value) with
    | CoalesceStep.Wait -> ()
    | CoalesceStep.StartDelivery first -> start (fun () -> run first)
