namespace SageFs.Simulation

open System
open SageFs
open SageFs.WorkerProtocol
open SageFs.Simulation.ConfirmThrowawaySim

/// Seeded, dependency-free generators for throwaway-worker scenarios. Chaos is data: `fromSeed n` replays identically
/// forever.
module ConfirmThrowawayGenerators =

  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 33)
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 12) with
          | 0 | 1 | 2 | 3 -> Op.Want(rnd.Next 3, rnd.Next(1, 5))
          | 4 | 5 | 6 -> Op.StartDone(rnd.Next(0, 4))
          | 7 -> Op.StartFails(rnd.Next(0, 4))
          | 8 | 9 -> Op.End(rnd.Next 3, rnd.Next(1, 5))
          | _ -> Op.Gone(rnd.Next 3) ]
    { Seed = seed; Ops = ops }

  /// The happy path: wanted, up, the generation ends.
  let served : Scenario =
    { Seed = 400; Ops = [ Op.Want(0, 1); Op.StartDone 0; Op.End(0, 1) ] }

  /// The worker for generation 1 turns up after generation 1 ended: nobody wants it.
  let lateAfterEnd : Scenario =
    { Seed = 401; Ops = [ Op.Want(0, 1); Op.End(0, 1); Op.StartDone 0 ] }

  /// A newer confirmation takes the place of one still starting, and the older worker turns up afterwards.
  let supersededWhileStarting : Scenario =
    { Seed = 402; Ops = [ Op.Want(0, 1); Op.Want(0, 2); Op.StartDone 0; Op.StartDone 0 ] }

  /// A newer confirmation takes the place of one that is already serving.
  let supersededWhileServing : Scenario =
    { Seed = 403; Ops = [ Op.Want(0, 1); Op.StartDone 0; Op.Want(0, 2); Op.StartDone 0 ] }

  /// The session goes while its worker serves.
  let sessionGoes : Scenario =
    { Seed = 404; Ops = [ Op.Want(0, 1); Op.StartDone 0; Op.Gone 0 ] }

  /// The worker never comes up.
  let startFails : Scenario =
    { Seed = 405; Ops = [ Op.Want(0, 1); Op.StartFails 0 ] }

/// Named invariants over a drained `ConfirmThrowawaySim.Trace`.
module ConfirmThrowawayInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private replay (t: Trace) = sprintf "seed=%d, ops=%A" t.Scenario.Seed t.Scenario.Ops

  let private servingHandles (book: ThrowawayBook.Book) : Set<int> =
    book
    |> Map.toList
    |> List.choose (fun (_, entry) ->
      match entry.Phase with
      | ThrowawayBook.Phase.Serving handle -> Some handle
      | ThrowawayBook.Phase.Starting -> None)
    |> Set.ofList

  /// live-is-what-serves: after every step the processes that really exist are exactly the workers the book says
  /// serve. A worker the book forgot is a leak, and a serving worker that is not running would answer nothing.
  let liveIsWhatServes : Invariant =
    { Id = "live-is-what-serves"
      Description = "After every step the set of running throwaway workers equals the set the book says serve, so none leaks and none is lost."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          let live = s.Live |> Map.toList |> List.map fst |> Set.ofList
          let serving = servingHandles s.After
          match live = serving with
          | true -> None
          | false -> Some (sprintf "after %A the running workers were %A and the book said %A serve" s.Op (Set.toList live) (Set.toList serving)))
        |> function
           | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
           | None -> Outcome.Holds }

  /// one-per-session: the book never holds two workers for one session, and the running workers never include two for
  /// one session.
  let onePerSession : Invariant =
    { Id = "one-per-session"
      Description = "No step leaves two running throwaway workers for one session."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          let perSession = s.Live |> Map.toList |> List.countBy snd |> List.tryFind (fun (_, count) -> count > 1)
          perSession |> Option.map (fun (session, count) -> sprintf "after %A session %s had %d running throwaway workers" s.Op (SessionId.value session) count))
        |> function
           | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
           | None -> Outcome.Holds }

  /// served-is-current: a worker is only announced as serving for the generation the book now holds.
  let servedIsCurrent : Invariant =
    { Id = "served-is-current"
      Description = "A Serve effect names the generation the book holds for that session after the step."
      Check = fun t ->
        t.Steps
        |> List.tryPick (fun s ->
          s.Effects
          |> List.tryPick (function
            | ThrowawayBook.Effect.Serve (session, generation, handle) ->
              match Map.tryFind session s.After with
              | Some entry when entry.Generation = generation && entry.Phase = ThrowawayBook.Phase.Serving handle -> None
              | _ -> Some (sprintf "after %A worker %d was announced for generation %d, which the book does not hold" s.Op handle generation)
            | _ -> None))
        |> function
           | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
           | None -> Outcome.Holds }

  /// every-ask-is-answered: once every start has finished and every generation has ended, no caller is still waiting
  /// for a worker, and the book and the running workers are empty.
  let everyAskIsAnswered : Invariant =
    { Id = "every-ask-is-answered"
      Description = "After every start finishes and every generation ends, no caller waits, no worker runs, and the book is empty."
      Check = fun t ->
        match Set.isEmpty t.FinalWaiting, Map.isEmpty t.FinalLive, Map.isEmpty t.Final with
        | true, true, true -> Outcome.Holds
        | _ ->
          Outcome.Violated (
            sprintf "after the drain: waiting=%A, running=%A, book=%A (%s)" (Set.toList t.FinalWaiting) (Map.toList t.FinalLive |> List.map fst) (Map.toList t.Final |> List.map fst) (replay t)) }

  let all : Invariant list = [ liveIsWhatServes; onePerSession; servedIsCurrent; everyAskIsAnswered ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
