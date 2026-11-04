namespace SageFs.Simulation

open System

/// Deterministic Simulation Testing for how an editor keeps a file's annotations current.
///
/// A connection used to be sent every file's annotations on connect. It is now sent the LIST of files that have
/// annotations (`annotations_available`), asks for the ones it has open, and is pushed a file only when what the
/// file says changed. So a client that connected after a file's last push has that file only by asking, and the
/// rule that makes it work is the client's: ask on connect for what is open, and ask when a file opens.
///
/// Chaos is data (a seeded list of changes, connects, drops, opens and closes), the subject is the client rule,
/// and each twin breaks exactly one half of it so the invariant is shown to have teeth.
module AnnotationsSyncSim =

  [<RequireQualifiedAccess>]
  type ClientBehavior =
    | Real
    /// Asks on connect, but not when a file opens afterwards.
    | NoFetchOnOpenTwin
    /// Asks when a file opens, but not on connect.
    | NoFetchOnConnectTwin

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// The daemon's view of a file changed: a new version, pushed to every connected client.
    | Change of file: string
    | Connect of client: int
    | Disconnect of client: int
    /// The client shows a file in an editor.
    | Open of client: int * file: string
    | Close of client: int * file: string

  type Scenario = { Seed: int; Events: SimEvent list }

  type Client =
    { Connected: bool
      /// Files in editors. Survives a drop: the buffers stay open.
      Open: Set<string>
      /// The version of each file the client holds.
      View: Map<string, int>
      /// The files the daemon last listed to this client.
      Listed: Set<string> }

  type State =
    { Step: int
      /// The version of each file the daemon holds. A file never changed is absent: it has no annotations.
      Versions: Map<string, int>
      Clients: Map<int, Client> }

  let files = [| "A.fs"; "B.fs"; "C.fs" |]
  let clientCount = 2

  let initial : State =
    { Step = 0
      Versions = Map.empty
      Clients =
        [ for i in 0 .. clientCount - 1 ->
            i, { Connected = false; Open = Set.empty; View = Map.empty; Listed = Set.empty } ]
        |> Map.ofList }

  let private updateClient (s: State) (id: int) (f: Client -> Client) : State =
    { s with Clients = Map.add id (f s.Clients.[id]) s.Clients }

  /// Ask the daemon for a file: the client now shows the version the daemon holds.
  let private fetch (s: State) (client: Client) (file: string) : Client =
    match Map.tryFind file s.Versions with
    | Some version -> { client with View = Map.add file version client.View }
    | None -> client

  let step (behavior: ClientBehavior) (s: State) (ev: SimEvent) : State =
    let s = { s with Step = s.Step + 1 }
    match ev with
    | SimEvent.Change file ->
      let version = (Map.tryFind file s.Versions |> Option.defaultValue 0) + 1
      let s' = { s with Versions = Map.add file version s.Versions }
      // Pushed to every connected client, which keeps whatever it is pushed.
      { s' with
          Clients =
            s'.Clients
            |> Map.map (fun _ c ->
              match c.Connected with
              | true -> { c with View = Map.add file version c.View }
              | false -> c) }
    | SimEvent.Connect id ->
      updateClient s id (fun client ->
        // A (re)connect clears what the client held, as both editors do, and is told what the daemon has.
        let listed = s.Versions |> Map.keys |> Set.ofSeq
        let connected = { client with Connected = true; View = Map.empty; Listed = listed }
        match behavior with
        | ClientBehavior.NoFetchOnConnectTwin -> connected
        | ClientBehavior.Real
        | ClientBehavior.NoFetchOnOpenTwin ->
          Set.intersect connected.Open listed |> Set.fold (fetch s) connected)
    | SimEvent.Disconnect id -> updateClient s id (fun client -> { client with Connected = false })
    | SimEvent.Open (id, file) ->
      updateClient s id (fun client ->
        let opened = { client with Open = Set.add file client.Open }
        match behavior, client.Connected, Set.contains file client.Listed with
        | ClientBehavior.NoFetchOnOpenTwin, _, _ -> opened
        | _, true, true -> fetch s opened file
        | _, _, _ -> opened)
    | SimEvent.Close (id, file) -> updateClient s id (fun client -> { client with Open = Set.remove file client.Open })

  let trace (behavior: ClientBehavior) (scenario: Scenario) : State list =
    scenario.Events |> List.scan (step behavior) initial

  /// A pure function of `seed`. Both clients connect and open every file at the end, so a scenario finishes
  /// with every client having asked for what it can.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let pick () = files.[rng.Next files.Length]
    let n = 15 + rng.Next 35
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 12 with
          | 0 | 1 | 2 | 3 -> SimEvent.Change (pick ())
          | 4 | 5 -> SimEvent.Connect (rng.Next clientCount)
          | 6 -> SimEvent.Disconnect (rng.Next clientCount)
          | 7 | 8 | 9 -> SimEvent.Open (rng.Next clientCount, pick ())
          | _ -> SimEvent.Close (rng.Next clientCount, pick ()) ]
    let settle =
      [ for client in 0 .. clientCount - 1 do
          yield SimEvent.Connect client
          for file in files -> SimEvent.Open (client, file) ]
    { Seed = seed; Events = events @ settle }

module AnnotationsSyncInvariants =
  open AnnotationsSyncSim

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: State list -> Outcome }

  /// open-files-are-current: at EVERY step, a connected client's open file that has annotations shows the version
  /// the daemon holds. A client that was connected the whole time satisfies it by the pushes; one that came later
  /// satisfies it only by asking.
  let openFilesAreCurrent : Invariant =
    { Id = "open-files-are-current"
      Description = "A connected client shows, for every open file with annotations, the version the daemon holds."
      Check = fun states ->
        states
        |> List.tryPick (fun s ->
          s.Clients
          |> Map.toList
          |> List.tryPick (fun (id, client) ->
            match client.Connected with
            | false -> None
            | true ->
              client.Open
              |> Set.toList
              |> List.tryPick (fun file ->
                match Map.tryFind file s.Versions with
                | None -> None
                | Some version ->
                  match Map.tryFind file client.View with
                  | Some shown when shown = version -> None
                  | held ->
                    Some (sprintf "step %d: client %d shows %A of %s, the daemon holds version %d" s.Step id held file version))))
        |> function
           | Some message -> Outcome.Violated message
           | None -> Outcome.Holds }

  let all : Invariant list = [ openFilesAreCurrent ]

  /// Every invariant that fails, by id.
  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun invariant ->
      match invariant.Check states with
      | Outcome.Violated message -> Some (invariant.Id, message)
      | Outcome.Holds -> None)
