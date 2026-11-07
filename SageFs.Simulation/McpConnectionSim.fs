namespace SageFs.Simulation

open System
open System.Collections.Generic
open System.Threading.Tasks
open SageFs

/// Deterministic simulation of MCP connections that open, work, stop reading and are abandoned, while the daemon
/// broadcasts notifications to all of them, folded through the REAL `McpFanout.broadcast`.
///
/// The model of the SDK is the one measured in the 2026-11-07 dump: each connection has a write buffer, a notification
/// is appended to it, and the buffer is cleared only when the whole write reaches the client. A connection that has stopped
/// reading fails every write, so its buffer keeps what was appended. The daemon on that day held 311 connections and 25 GB.
/// `Real` releases a connection after its first failed send (the product). `KeepsStuckConnectionTwin` is the old behaviour:
/// the failed connection is only forgotten by the registry and its next call registers it again, so its buffer keeps growing.
module McpConnectionSim =

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// A client connects and is listening.
    | Open of connection: int
    /// A client makes a tool call: a connection that is known and not released registers itself.
    | Call of connection: int
    /// The daemon broadcasts one notification of this many bytes.
    | Notify of bytes: int
    /// The client stops reading and never reads again (a bridge blocked on an unread pipe, a stopped process).
    | Abandon of connection: int

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    | KeepsStuckConnectionTwin

  type Connection =
    { Id: int
      mutable Buffered: int64
      mutable Reading: bool
      mutable Released: bool }

  type State =
    { Connections: Dictionary<int, Connection>
      Registered: HashSet<int>
      /// True when the event that produced this state was a broadcast.
      AfterNotify: bool
      mutable Step: int }

  let private initial () : State =
    { Connections = Dictionary<int, Connection>()
      Registered = HashSet<int>()
      AfterNotify = false
      Step = 0 }

  /// The most a connection's write buffer may hold: one notification that did not get through. The generator never sends more than this.
  let maxNotificationBytes = 512

  let private targetOf (behavior: Behavior) (state: State) (bytes: int) (connection: Connection) : FanoutTarget =
    { Id = string connection.Id
      Send = fun _ ->
        // The SDK appends first and clears only after the client took the bytes.
        connection.Buffered <- connection.Buffered + int64 bytes
        match connection.Reading with
        | true ->
          connection.Buffered <- 0L
          Task.CompletedTask
        | false -> Task.FromException(OperationCanceledException())
      Release = fun () ->
        state.Registered.Remove connection.Id |> ignore
        match behavior with
        | Behavior.Real ->
          connection.Released <- true
          connection.Buffered <- 0L
        | Behavior.KeepsStuckConnectionTwin -> ()
        Task.CompletedTask }

  let private apply (behavior: Behavior) (state: State) (event: SimEvent) : unit =
    state.Step <- state.Step + 1
    match event with
    | SimEvent.Open id ->
      state.Connections.[id] <- { Id = id; Buffered = 0L; Reading = true; Released = false }
      state.Registered.Add id |> ignore
    | SimEvent.Call id ->
      match state.Connections.TryGetValue id with
      | true, connection when not connection.Released -> state.Registered.Add id |> ignore
      | _ -> ()
    | SimEvent.Abandon id ->
      match state.Connections.TryGetValue id with
      | true, connection -> connection.Reading <- false
      | false, _ -> ()
    | SimEvent.Notify bytes ->
      let targets =
        state.Registered
        |> Seq.choose (fun id ->
          match state.Connections.TryGetValue id with
          | true, connection -> Some(targetOf behavior state bytes connection)
          | false, _ -> None)
        |> Seq.toArray
      McpFanout.broadcast Timeouts.mcpNotificationSend Timeouts.mcpConnectionRelease targets
      |> fun task -> task.GetAwaiter().GetResult()
      |> ignore

  /// Fold the events; the state after each one.
  let run (behavior: Behavior) (events: SimEvent list) : State list =
    let state = initial ()
    [ for event in events do
        apply behavior state event
        yield
          { Connections = Dictionary(state.Connections |> Seq.map (fun kvp -> KeyValuePair(kvp.Key, { kvp.Value with Buffered = kvp.Value.Buffered })))
            Registered = HashSet(state.Registered)
            AfterNotify = (match event with SimEvent.Notify _ -> true | _ -> false)
            Step = state.Step } ]

  /// A seeded lifecycle: `connections` clients connect, call, go quiet and are replaced, while notifications go out.
  let scenarioOf (seed: int) (steps: int) : SimEvent list =
    let rnd = Random(seed)
    let mutable nextId = 0
    let opened = ResizeArray<int>()
    [ for _ in 1 .. steps do
        match rnd.Next 10, opened.Count with
        | 0, _
        | _, 0 ->
          let id = nextId
          nextId <- nextId + 1
          opened.Add id
          yield SimEvent.Open id
        | 1, n | 2, n ->
          yield SimEvent.Abandon opened.[rnd.Next n]
        | 3, n | 4, n | 5, n ->
          yield SimEvent.Call opened.[rnd.Next n]
        | _ -> yield SimEvent.Notify(1 + rnd.Next maxNotificationBytes) ]

  /// What the daemon holds for connections that have stopped reading: the bytes in their buffers and how many are still registered.
  let stuckBytes (state: State) : int64 =
    state.Connections.Values
    |> Seq.filter (fun connection -> not connection.Reading)
    |> Seq.sumBy (fun connection -> connection.Buffered)

  let stuckRegistered (state: State) : int =
    state.Registered
    |> Seq.filter (fun id -> not state.Connections.[id].Reading)
    |> Seq.length

  /// The invariants of the product: a notification that did not get through leaves at most itself in a buffer
  /// (and only until the broadcast that failed it finishes), and a registry never keeps a connection that stopped reading
  /// past the broadcast that found out.
  let violations (states: State list) : string list =
    [ for state in states do
        let connectionsOpen = state.Connections.Count
        match stuckBytes state > int64 maxNotificationBytes * int64 connectionsOpen with
        | true -> yield sprintf "step %d: connections that stopped reading hold %d bytes" state.Step (stuckBytes state)
        | false -> ()
        for connection in state.Connections.Values do
          match connection.Buffered > int64 maxNotificationBytes with
          | true -> yield sprintf "step %d: connection %d buffers %d bytes" state.Step connection.Id connection.Buffered
          | false -> ()
        // After a broadcast nothing that stopped reading may stay registered; between broadcasts a fresh Abandon has not been found yet.
        match state.AfterNotify && stuckRegistered state > 0 with
        | true -> yield sprintf "step %d: %d connections that stopped reading are still registered after a broadcast" state.Step (stuckRegistered state)
        | false -> () ]
