namespace SageFs.Simulation

open SageFs.Features.ReloadOutcome
open SageFs.Features.PatchConfirmation
open SageFs.Simulation.PatchConfirmationSim

/// Named invariants over a drained `PatchConfirmationSim.Trace`. Same
/// stable-id / `Holds`-vs-`Violated` shape as the other simulations.
module PatchConfirmationInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private saveOf (t: Trace) (index: int) : SaveRecord = t.Final.Saves |> List.find (fun s -> s.Index = index)

  /// The functions of a save that were still this save's responsibility when
  /// its watch ended: the ones a newer save had not replaced.
  let private liveDecls (record: SaveRecord) (superseded: Set<int64>) (entered: Set<int64>) : int list =
    record.Decls
    |> List.filter (fun d ->
      match Map.tryFind d record.Probes with
      | Some probe -> Set.contains probe entered || not (Set.contains probe superseded)
      | None -> true)

  let private ran (record: SaveRecord) (entered: Set<int64>) (decl: int) : bool =
    match Map.tryFind decl record.Probes with
    | Some probe -> Set.contains probe entered
    | None -> false

  /// patched-means-seen-running: an outcome that claims a patch is live is only
  /// ever produced when every function it still answers for was really entered.
  /// This is the bug: a save that claimed Patched while the caller kept
  /// running an inlined copy of the old body.
  let patchedMeansSeenRunning : Invariant =
    { Id = "patched-means-seen-running"
      Description = "A Patched (or kept-and-patched) outcome is produced only when every function the save still answers for was really entered."
      Check = fun t ->
        t.Final.Settlements
        |> List.tryPick (fun s ->
          match s.Step with
          | WatchStep.Settled(ReloadOutcome.Patched(n, _))
          | WatchStep.Settled(ReloadOutcome.KeptLiveState(n, _, _, _)) when n > 0 ->
            let record = saveOf t s.Save
            let live = liveDecls record s.SupersededAtSettle s.EnteredAtSettle
            let silent = live |> List.filter (fun d -> not (ran record s.EnteredAtSettle d))
            match silent with
            | [] -> None
            | _ ->
              Some(
                sprintf
                  "save #%d was reported Patched but functions %A never ran (seed=%d, ops=%A)"
                  s.Save silent t.Scenario.Seed t.Scenario.Ops)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// never-entered-names-the-silent: NeverEntered names exactly the functions
  /// that did not run, and counts exactly the ones that did.
  let neverEnteredNamesTheSilent : Invariant =
    { Id = "never-entered-names-the-silent"
      Description = "NeverEntered names exactly the functions that never ran and counts exactly those that did, none replaced by a newer save."
      Check = fun t ->
        t.Final.Settlements
        |> List.tryPick (fun s ->
          match s.Step with
          | WatchStep.Settled(ReloadOutcome.NeverEntered(first, rest, entered, _, _)) ->
            let record = saveOf t s.Save
            let live = liveDecls record s.SupersededAtSettle s.EnteredAtSettle
            let expectedSilent = live |> List.filter (fun d -> not (ran record s.EnteredAtSettle d)) |> List.map (sprintf "Sim.f%d") |> List.sort
            let expectedEntered = live |> List.filter (ran record s.EnteredAtSettle) |> List.length
            let named = first :: rest |> List.sort
            match named = expectedSilent && entered = expectedEntered with
            | true -> None
            | false ->
              Some(
                sprintf
                  "save #%d: NeverEntered named %A with %d seen, truth says silent %A with %d seen (seed=%d, ops=%A)"
                  s.Save named entered expectedSilent expectedEntered t.Scenario.Seed t.Scenario.Ops)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// every-watch-resolves: once the bounds have fired, no save is left
  /// pending. A pending patch that never resolves is a claim nobody can act on.
  let everyWatchResolves : Invariant =
    { Id = "every-watch-resolves"
      Description = "After every bound has fired, each save's watch has ended in exactly one settlement (or none, when it was only ever replaced)."
      Check = fun t ->
        let stillWaiting =
          t.Final.Watches
          |> Map.toList
          |> List.choose (fun (save, w) ->
            match w with
            | WatchState.Waiting _ -> Some save
            | WatchState.Done -> None)
        let doubled =
          t.Final.Settlements
          |> List.countBy _.Save
          |> List.filter (fun (_, n) -> n > 1)
          |> List.map fst
        match stillWaiting, doubled with
        | [], [] -> Outcome.Holds
        | waiting, _ :: _ ->
          Outcome.Violated(sprintf "saves %A settled more than once (waiting=%A, seed=%d, ops=%A)" doubled waiting t.Scenario.Seed t.Scenario.Ops)
        | waiting, [] ->
          Outcome.Violated(sprintf "saves %A are still pending after every bound fired (seed=%d, ops=%A)" waiting t.Scenario.Seed t.Scenario.Ops) }

  /// replaced-is-not-blamed: a function a newer save replaced before it ever
  /// ran is never reported as never entered by the older save.
  let replacedIsNotBlamed : Invariant =
    { Id = "replaced-is-not-blamed"
      Description = "A function replaced by a newer save before it ran is not reported NeverEntered by the older save."
      Check = fun t ->
        t.Final.Settlements
        |> List.tryPick (fun s ->
          match s.Step with
          | WatchStep.Settled(ReloadOutcome.NeverEntered(first, rest, _, _, _)) ->
            let record = saveOf t s.Save
            let replaced =
              record.Decls
              |> List.filter (fun d ->
                match Map.tryFind d record.Probes with
                | Some probe -> Set.contains probe s.SupersededAtSettle && not (Set.contains probe s.EnteredAtSettle)
                | None -> false)
              |> List.map (sprintf "Sim.f%d")
            match first :: rest |> List.filter (fun n -> List.contains n replaced) with
            | [] -> None
            | blamed -> Some(sprintf "save #%d blamed %A for functions a newer save replaced (seed=%d, ops=%A)" s.Save blamed t.Scenario.Seed t.Scenario.Ops)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  let all : Invariant list =
    [ patchedMeansSeenRunning; neverEnteredNamesTheSilent; everyWatchResolves; replacedIsNotBlamed ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
