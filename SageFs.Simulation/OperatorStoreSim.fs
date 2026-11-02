namespace SageFs.Simulation

open System
open SageFs
open SageFs.EdgeCredential
open SageFs.OperatorFile

/// Deterministic Simulation Testing for how `operator.token` comes to exist: up to three daemons start on one
/// data directory at seeded moments, take their steps in a seeded interleaving, and some crash between two of
/// them, all folded through the REAL `OperatorFile.next`. The world they act on is an in-memory directory, so
/// the interleaving is data and a failing seed replays exactly.
///
/// Every twin changes the WORLD or what the machine is asked, never the invariants, so each invariant has teeth:
///   - `OverwriteRaceTwin` publishes by replacing the target. Two daemons that both found it missing both
///     publish, and the first one holds a credential the file no longer has.
///   - `TornWriteTwin` writes the credential into the target itself, in two parts, with no temporary file. A
///     daemon that starts between the parts reads half a credential.
module OperatorStoreSim =

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    | OverwriteRaceTwin
    | TornWriteTwin

  [<RequireQualifiedAccess>]
  type SimEvent =
    | Start of daemon: int
    | Step of daemon: int
    | Crash of daemon: int

  type Daemon = {
    Machine: State * Effect option
    Started: bool
    Crashed: bool
    /// Every text this daemon read from the target.
    Reads: string list
  }

  type Directory = {
    Target: string option
    /// Each daemon's temporary file, by daemon.
    Temps: Map<int, string>
  }

  type SimState = {
    Step: int
    Directory: Directory
    Daemons: Map<int, Daemon>
  }

  let daemonCount = 3

  /// Each daemon draws different entropy, so a daemon holding the wrong credential shows up as a different hash.
  let entropyOf (daemon: int) : byte[] = Array.create OperatorCredential.entropyBytes (byte (daemon + 1))

  let initial : SimState =
    { Step = 0
      Directory = { Target = None; Temps = Map.empty }
      Daemons =
        [ 0 .. daemonCount - 1 ]
        |> List.map (fun d -> d, { Machine = start; Started = false; Crashed = false; Reads = [] })
        |> Map.ofList }

  let halfOf (credential: string) : string = credential.Substring(0, credential.Length / 2)

  /// What the world answers to one effect, and the directory after it.
  let act (behavior: Behavior) (daemon: int) (directory: Directory) (effect: Effect) : Answer * Directory =
    match effect with
    | Effect.ReadTarget ->
      match directory.Target with
      | None -> Answer.Observed Observation.Missing, directory
      | Some text -> Answer.Observed(Observation.Present(text, FileMode.OwnerOnly)), directory
    | Effect.WriteTemp credential ->
      match behavior with
      | Behavior.TornWriteTwin ->
        match directory.Target with
        | None -> Answer.TempWritten, { directory with Target = Some(halfOf credential); Temps = Map.add daemon credential directory.Temps }
        | Some _ -> Answer.TempWritten, { directory with Temps = Map.add daemon credential directory.Temps }
      | Behavior.Real
      | Behavior.OverwriteRaceTwin -> Answer.TempWritten, { directory with Temps = Map.add daemon credential directory.Temps }
    | Effect.ReadTempMode -> Answer.TempModeIs FileMode.OwnerOnly, directory
    | Effect.PublishExclusive ->
      let credential = Map.find daemon directory.Temps
      let withoutTemp = Map.remove daemon directory.Temps
      match behavior with
      | Behavior.OverwriteRaceTwin -> Answer.Published, { Target = Some credential; Temps = withoutTemp }
      | Behavior.TornWriteTwin ->
        match directory.Target with
        | Some existing when existing = halfOf credential -> Answer.Published, { Target = Some credential; Temps = withoutTemp }
        | Some _ -> Answer.TargetAlreadyExists, directory
        | None -> Answer.Published, { Target = Some credential; Temps = withoutTemp }
      | Behavior.Real ->
        match directory.Target with
        | None -> Answer.Published, { Target = Some credential; Temps = withoutTemp }
        | Some _ -> Answer.TargetAlreadyExists, directory
    | Effect.DiscardTemp -> Answer.Discarded, { directory with Temps = Map.remove daemon directory.Temps }

  let takeStep (behavior: Behavior) (daemon: int) (state: SimState) : SimState =
    let current = Map.find daemon state.Daemons
    match current.Started, current.Crashed, current.Machine with
    | true, false, (machineState, Some effect) ->
      let answer, directory = act behavior daemon state.Directory effect
      let reads =
        match answer with
        | Answer.Observed(Observation.Present(text, _)) -> current.Reads @ [ text ]
        | _ -> current.Reads
      let updated = { current with Machine = next (entropyOf daemon) machineState answer; Reads = reads }
      { state with Directory = directory; Daemons = Map.add daemon updated state.Daemons }
    | _ -> state

  let step (behavior: Behavior) (state: SimState) (event: SimEvent) : SimState =
    let advanced = { state with Step = state.Step + 1 }
    match event with
    | SimEvent.Start daemon ->
      let current = Map.find daemon advanced.Daemons
      { advanced with Daemons = Map.add daemon { current with Started = true } advanced.Daemons }
    | SimEvent.Crash daemon ->
      let current = Map.find daemon advanced.Daemons
      { advanced with Daemons = Map.add daemon { current with Crashed = true } advanced.Daemons }
    | SimEvent.Step daemon -> takeStep behavior daemon advanced

  let isFinished (daemon: Daemon) : bool =
    match daemon.Machine with
    | State.Finished _, _ -> true
    | _ -> false

  /// After the scenario, every daemon that started and did not crash runs to the end, round robin.
  let drain (behavior: Behavior) (state: SimState) : SimState list =
    let live (s: SimState) = s.Daemons |> Map.filter (fun _ d -> d.Started && not d.Crashed && not (isFinished d)) |> Map.keys |> List.ofSeq
    let rec go (s: SimState) (acc: SimState list) (budget: int) =
      match live s, budget with
      | [], _
      | _, 0 -> List.rev acc
      | daemons, _ ->
        let after = daemons |> List.fold (fun st d -> step behavior st (SimEvent.Step d)) s
        go after (after :: acc) (budget - 1)
    go state [] 200

  type Scenario = { Seed: int; Events: SimEvent list }

  /// Every state the scenario passes through, then the drain's.
  let trace (behavior: Behavior) (scenario: Scenario) : SimState list =
    let states = scenario.Events |> List.scan (step behavior) initial
    states @ drain behavior (List.last states)

  /// A pure function of `seed`: each daemon starts at a seeded moment, the steps interleave at random, and a
  /// crash lands between two steps in one scenario in three.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let steps = [ for _ in 1 .. 12 + rng.Next 20 -> SimEvent.Step(rng.Next daemonCount) ]
    let insertAt (event: SimEvent) (events: SimEvent list) =
      let at = rng.Next(events.Length + 1)
      List.take at events @ [ event ] @ List.skip at events
    let withStarts = [ 0 .. daemonCount - 1 ] |> List.fold (fun events d -> insertAt (SimEvent.Start d) events) steps
    // A daemon starts before it takes a step: reorder so the Start of d precedes the first Step of d.
    let startedFirst =
      [ 0 .. daemonCount - 1 ]
      |> List.fold
        (fun events d ->
          let startAt = events |> List.findIndex (fun e -> e = SimEvent.Start d)
          let firstStep = events |> List.tryFindIndex (fun e -> e = SimEvent.Step d)
          match firstStep with
          | Some stepAt when stepAt < startAt ->
            let without = events |> List.filter (fun e -> e <> SimEvent.Start d)
            List.take stepAt without @ [ SimEvent.Start d ] @ List.skip stepAt without
          | _ -> events)
        withStarts
    let events =
      match rng.Next 3 with
      | 0 -> insertAt (SimEvent.Crash(rng.Next daemonCount)) startedFirst
      | _ -> startedFirst
    { Seed = seed; Events = events }

module OperatorStoreSimInvariants =
  open OperatorStoreSim

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: SimState list -> Outcome }

  let outcomeOf (messages: string list) : Outcome =
    match messages with
    | [] -> Outcome.Holds
    | first :: _ -> Outcome.Violated first

  let wellFormed (text: string) : bool = OperatorCredential.isWellFormed (text.Trim())

  /// The target is never seen holding half a credential.
  let targetNeverTorn : Invariant =
    { Id = "TARGET-NEVER-TORN"
      Description = "at no step does the target hold anything but a whole credential"
      Check = fun states ->
        states
        |> List.choose (fun s ->
          match s.Directory.Target with
          | Some text when not (wellFormed text) -> Some(sprintf "step %d: the target holds %d characters that are not a credential" s.Step text.Length)
          | _ -> None)
        |> outcomeOf }

  /// No daemon reads anything from the target but a whole credential.
  let noDaemonReadsATornFile : Invariant =
    { Id = "NO-DAEMON-READS-A-TORN-FILE"
      Description = "every text a daemon reads from the target is a whole credential"
      Check = fun states ->
        match List.tryLast states with
        | None -> Outcome.Holds
        | Some last ->
          last.Daemons
          |> Map.toList
          |> List.collect (fun (d, daemon) -> daemon.Reads |> List.filter (wellFormed >> not) |> List.map (fun text -> sprintf "daemon %d read %d characters that are not a credential" d text.Length))
          |> outcomeOf }

  /// Every daemon that finished with a credential holds the one in the file.
  let everyHolderAgreesWithTheFile : Invariant =
    { Id = "EVERY-HOLDER-AGREES-WITH-THE-FILE"
      Description = "a daemon that finished holds the hash of the credential the file ends with"
      Check = fun states ->
        match List.tryLast states with
        | None -> Outcome.Holds
        | Some last ->
          match last.Directory.Target with
          | None -> Outcome.Holds
          | Some text ->
            let inFile = OperatorHash.ofCredential (text.Trim())
            last.Daemons
            |> Map.toList
            |> List.choose (fun (d, daemon) ->
              match daemon.Machine with
              | State.Finished(Ok held), _ when held <> inFile -> Some(sprintf "daemon %d holds a credential the file does not have" d)
              | _ -> None)
            |> outcomeOf }

  /// Every daemon that started and did not crash ends with a credential.
  let everyLiveDaemonFinishes : Invariant =
    { Id = "EVERY-LIVE-DAEMON-FINISHES-WITH-A-CREDENTIAL"
      Description = "a daemon that started and did not crash finishes Ok"
      Check = fun states ->
        match List.tryLast states with
        | None -> Outcome.Holds
        | Some last ->
          last.Daemons
          |> Map.toList
          |> List.choose (fun (d, daemon) ->
            match daemon.Started, daemon.Crashed, daemon.Machine with
            | true, false, (State.Finished(Ok _), _) -> None
            | true, false, (finished, _) -> Some(sprintf "daemon %d ended at %A" d finished)
            | _ -> None)
          |> outcomeOf }

  /// A daemon that ran to the end leaves no temporary file behind. A crashed one may.
  let noLitterFromFinishedDaemons : Invariant =
    { Id = "FINISHED-DAEMONS-LEAVE-NO-LITTER"
      Description = "a daemon that finished has no temporary file left"
      Check = fun states ->
        match List.tryLast states with
        | None -> Outcome.Holds
        | Some last ->
          last.Directory.Temps
          |> Map.toList
          |> List.choose (fun (d, _) ->
            let daemon = Map.find d last.Daemons
            match daemon.Crashed, isFinished daemon with
            | false, true -> Some(sprintf "daemon %d finished and left a temporary file" d)
            | _ -> None)
          |> outcomeOf }

  let all : Invariant list =
    [ targetNeverTorn; noDaemonReadsATornFile; everyHolderAgreesWithTheFile; everyLiveDaemonFinishes; noLitterFromFinishedDaemons ]
