namespace SageFs.Simulation

open SageFs.Simulation.GuardSim

/// Named invariants over a drained `GuardSim.Trace`. Same stable-id / `Holds`-vs-`Violated` shape as the other
/// simulations.
module GuardSimInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// The first step where `bad` says the step is wrong, as a violation naming the step, the op and what was seen.
  let private firstBadStep (t: Trace) (bad: StepRecord -> string option) : Outcome =
    t.Steps
    |> List.tryPick (fun s ->
      bad s
      |> Option.map (fun why ->
        sprintf "step %d (%A): %s (seed=%d, ops=%A)" s.Index s.Op why t.Scenario.Seed t.Scenario.Ops))
    |> function
       | Some message -> Outcome.Violated message
       | None -> Outcome.Holds

  /// patched-iff-held: a method carries the patch exactly while some click that holds it is still waiting or has a
  /// thread still running. Too few and a thread runs unchecked, too many and the guards outlive the click that asked.
  let patchedIffHeld : Invariant =
    { Id = "patched-iff-held"
      Description = "A method is patched exactly while some click that reached it is still waiting or still has its thread running."
      Check = fun t ->
        firstBadStep t (fun s ->
          match s.Observed.Patched = s.Expected.Patched with
          | true -> None
          | false -> Some(sprintf "patched %A but truth says %A" s.Observed.Patched s.Expected.Patched)) }

  /// abandoned-thread-stays-guarded: after a click gives up on a thread that still runs, everything that click reached
  /// stays guarded until the thread ends. The thread may be anywhere in guarded code, and running it unchecked would let
  /// a loop or a recursion go on with nothing to stop it.
  let abandonedThreadStaysGuarded : Invariant =
    { Id = "abandoned-thread-stays-guarded"
      Description = "A click that gave up on a thread that still runs keeps everything it reached guarded until the thread ends."
      Check = fun t ->
        firstBadStep t (fun s ->
          let missing = Set.difference s.Expected.AbandonedStillRunning s.Observed.Patched
          match Set.isEmpty missing with
          | true -> None
          | false -> Some(sprintf "an abandoned thread still runs, and %A lost its guards" missing)) }

  /// pending-count-is-exact: the global stop count is the stops asked for whose threads still run, never more (a leaked
  /// stop makes every later check slower) and never fewer (a stop that was never counted never reaches its thread).
  let pendingCountIsExact : Invariant =
    { Id = "pending-count-is-exact"
      Description = "The global stop count is exactly the number of stops asked for whose threads are still running."
      Check = fun t ->
        firstBadStep t (fun s ->
          match s.Observed.Pending = s.Expected.Pending with
          | true -> None
          | false -> Some(sprintf "stop count %d but truth says %d" s.Observed.Pending s.Expected.Pending)) }

  /// stop-reaches-only-its-thread: a check on the thread of a click that was asked to stop throws, on the thread of any
  /// other click does not, and on a thread bound to no cell never does.
  let stopReachesOnlyItsThread : Invariant =
    { Id = "stop-reaches-only-its-thread"
      Description = "A check throws on the thread of a click that was asked to stop and on no other thread."
      Check = fun t ->
        firstBadStep t (fun s ->
          let wrong =
            s.Expected.StopChecks
            |> Map.toList
            |> List.filter (fun (click, wanted) -> Map.tryFind click s.Observed.StopChecks <> Some wanted)
            |> List.map fst
          match wrong, s.Observed.UnboundCheck with
          | [], Check.Quiet -> None
          | [], Check.Threw -> Some "a check on a thread bound to no cell threw"
          | clicks, _ -> Some(sprintf "checks on the threads of clicks %A did not do what truth says" clicks)) }

  /// reloaded-never-patched: a method hot reload re-pointed is never patched, whether it was held when the reload landed
  /// or a click asks for it afterwards. A patch on it would put the old code back over the reload.
  let reloadedNeverPatched : Invariant =
    { Id = "reloaded-never-patched"
      Description = "A method hot reload re-pointed carries no patch of ours, from the reload on."
      Check = fun t ->
        firstBadStep t (fun s ->
          let still = Set.intersect s.Observed.Patched s.Expected.Detoured
          match Set.isEmpty still with
          | true -> None
          | false -> Some(sprintf "re-pointed methods %A still carry a patch" still)) }

  /// everything-released-at-the-end: once every thread has ended and every click has looked, nothing is patched and no
  /// stop is pending.
  let everythingReleasedAtTheEnd : Invariant =
    { Id = "everything-released-at-the-end"
      Description = "After every thread has ended and every click has looked, no method is patched and no stop is pending."
      Check = fun t ->
        match Set.isEmpty t.Final.Patched && t.Final.Pending = 0 with
        | true -> Outcome.Holds
        | false ->
          Outcome.Violated(
            sprintf "at the end %A are still patched and %d stops are pending (seed=%d, ops=%A)"
              t.Final.Patched t.Final.Pending t.Scenario.Seed t.Scenario.Ops) }

  let all : Invariant list =
    [ patchedIffHeld
      abandonedThreadStaysGuarded
      pendingCountIsExact
      stopReachesOnlyItsThread
      reloadedNeverPatched
      everythingReleasedAtTheEnd ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
