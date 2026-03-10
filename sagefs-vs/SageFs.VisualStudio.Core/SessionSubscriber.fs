namespace SageFs.VisualStudio.Core

open System
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type SessionEvent =
  | SessionStarted of {| Config: Map<string, string>; StartedAt: DateTimeOffset |}
  | SessionWarmupCompleted of {| Duration: TimeSpan; Errors: string list |}
  | SessionWarmupProgress of {| Step: int; Total: int; Message: string |}
  | SessionReady
  | SessionFaulted of {| Error: string; StackTrace: string option |}
  | SessionReset
  | SessionHardReset of {| Rebuild: bool |}
  | DaemonSessionCreated of {| SessionId: string; Projects: string list; WorkingDir: string; CreatedAt: DateTimeOffset |}
  | DaemonSessionStopped of {| SessionId: string; StoppedAt: DateTimeOffset |}
  | DaemonSessionSwitched of {| FromId: string option; ToId: string; SwitchedAt: DateTimeOffset |}

type SessionStreamState = {
  LastEvent: SessionEvent option
  LastEventAt: DateTimeOffset
  ActiveSessionId: string option
  IsReady: bool
  IsFaulted: bool
  FaultMessage: string option
  WarmupProgress: {| Step: int; Total: int; Message: string |} option
}

module SessionStreamState =
  let empty = {
    LastEvent = None
    LastEventAt = DateTimeOffset.MinValue
    ActiveSessionId = None
    IsReady = false
    IsFaulted = false
    FaultMessage = None
    WarmupProgress = None
  }

  let update (evt: SessionEvent) (s: SessionStreamState) =
    let base' = { s with LastEvent = Some evt; LastEventAt = DateTimeOffset.UtcNow }
    match evt with
    | SessionStarted _ ->
      { base' with IsReady = false; IsFaulted = false; FaultMessage = None; WarmupProgress = None }
    | SessionWarmupCompleted _ ->
      { base' with WarmupProgress = None }
    | SessionWarmupProgress p ->
      { base' with WarmupProgress = Some p }
    | SessionReady ->
      { base' with IsReady = true; IsFaulted = false; FaultMessage = None; WarmupProgress = None }
    | SessionFaulted f ->
      { base' with IsFaulted = true; IsReady = false; FaultMessage = Some f.Error }
    | SessionReset ->
      { base' with IsReady = false; IsFaulted = false; FaultMessage = None; WarmupProgress = None }
    | SessionHardReset _ ->
      { base' with IsReady = false; IsFaulted = false; FaultMessage = None; WarmupProgress = None }
    | DaemonSessionCreated c ->
      let sessionId =
        match s.ActiveSessionId with
        | None -> Some c.SessionId
        | existing -> existing
      { base' with ActiveSessionId = sessionId }
    | DaemonSessionStopped d ->
      let sessionId =
        match s.ActiveSessionId with
        | Some id when id = d.SessionId -> None
        | existing -> existing
      { base' with ActiveSessionId = sessionId }
    | DaemonSessionSwitched sw ->
      { base' with ActiveSessionId = Some sw.ToId }

/// SSE subscriber for the /events endpoint, filtering for session-related events.
type SessionSubscriber(port: int) =
  let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
  let http = new HttpClient(handler)
  let mutable cts: CancellationTokenSource option = None
  let mutable state = SessionStreamState.empty
  let stateChanged = Event<SessionStreamState>()
  let eventReceived = Event<SessionEvent>()

  let tryStr (el: JsonElement) (prop: string) (fb: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.String then v.GetString() else fb

  let tryBool (el: JsonElement) (prop: string) (fb: bool) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.True || (el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.False)
    then v.GetBoolean() else fb

  let tryInt (el: JsonElement) (prop: string) (fb: int) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Number then v.GetInt32() else fb

  let tryStrArr (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Array then
      [ for e in v.EnumerateArray() do
          if e.ValueKind = JsonValueKind.String then yield e.GetString() ]
    else []

  let tryDateTimeOffset (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.String then
      match DateTimeOffset.TryParse(v.GetString()) with
      | true, dt -> dt
      | _ -> DateTimeOffset.UtcNow
    else DateTimeOffset.UtcNow

  let tryTimeSpan (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) then
      match v.ValueKind with
      | JsonValueKind.String ->
        match TimeSpan.TryParse(v.GetString()) with
        | true, ts -> ts
        | _ -> TimeSpan.Zero
      | JsonValueKind.Number -> TimeSpan.FromMilliseconds(v.GetDouble())
      | _ -> TimeSpan.Zero
    else TimeSpan.Zero

  let parseEvent (eventType: string) (json: string) : SessionEvent option =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      match eventType with
      | "session_started" ->
        let config =
          let mutable v = Unchecked.defaultof<JsonElement>
          if root.TryGetProperty("Config", &v) && v.ValueKind = JsonValueKind.Object then
            [ for p in v.EnumerateObject() -> p.Name, p.Value.GetString() ]
            |> Map.ofList
          else Map.empty
        let startedAt = tryDateTimeOffset root "StartedAt"
        Some (SessionStarted {| Config = config; StartedAt = startedAt |})
      | "session_warmup_completed" ->
        let duration = tryTimeSpan root "Duration"
        let errors = tryStrArr root "Errors"
        Some (SessionWarmupCompleted {| Duration = duration; Errors = errors |})
      | "session_warmup_progress" | "warmup_progress" ->
        let step = tryInt root "Step" 0
        let total = tryInt root "Total" 0
        let message = tryStr root "Message" ""
        Some (SessionWarmupProgress {| Step = step; Total = total; Message = message |})
      | "session_ready" ->
        Some SessionReady
      | "session_faulted" ->
        let error = tryStr root "Error" "Unknown error"
        let mutable stEl = Unchecked.defaultof<JsonElement>
        let stackTrace =
          if root.TryGetProperty("StackTrace", &stEl) && stEl.ValueKind = JsonValueKind.String
          then Some (stEl.GetString())
          else None
        Some (SessionFaulted {| Error = error; StackTrace = stackTrace |})
      | "session_reset" ->
        Some SessionReset
      | "session_hard_reset" ->
        let rebuild = tryBool root "Rebuild" false
        Some (SessionHardReset {| Rebuild = rebuild |})
      | "daemon_session_created" ->
        let sessionId = tryStr root "SessionId" ""
        let projects = tryStrArr root "Projects"
        let workingDir = tryStr root "WorkingDir" ""
        let createdAt = tryDateTimeOffset root "CreatedAt"
        Some (DaemonSessionCreated {| SessionId = sessionId; Projects = projects; WorkingDir = workingDir; CreatedAt = createdAt |})
      | "daemon_session_stopped" ->
        let sessionId = tryStr root "SessionId" ""
        let stoppedAt = tryDateTimeOffset root "StoppedAt"
        Some (DaemonSessionStopped {| SessionId = sessionId; StoppedAt = stoppedAt |})
      | "daemon_session_switched" ->
        let mutable fromEl = Unchecked.defaultof<JsonElement>
        let fromId =
          if root.TryGetProperty("FromId", &fromEl) && fromEl.ValueKind = JsonValueKind.String
          then Some (fromEl.GetString())
          else None
        let toId = tryStr root "ToId" ""
        let switchedAt = tryDateTimeOffset root "SwitchedAt"
        Some (DaemonSessionSwitched {| FromId = fromId; ToId = toId; SwitchedAt = switchedAt |})
      | _ -> None
    with _ -> None

  [<CLIEvent>]
  member _.StateChanged = stateChanged.Publish

  [<CLIEvent>]
  member _.EventReceived = eventReceived.Publish

  member _.State = state

  member _.Start() =
    let newCts = new CancellationTokenSource()
    cts <- Some newCts
    let url = sprintf "http://localhost:%d/events" port
    let mutable retryDelay = 1000

    let rec loop () = task {
      try
        let! resp =
          http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, newCts.Token)
        use! stream = resp.Content.ReadAsStreamAsync(newCts.Token)
        use reader = new IO.StreamReader(stream)
        retryDelay <- 1000
        let mutable currentEvent = "message"
        while not (reader.EndOfStream || newCts.Token.IsCancellationRequested) do
          let! line = reader.ReadLineAsync(newCts.Token)
          if line <> null then
            if line.StartsWith("event: ") then
              currentEvent <- line.Substring(7).Trim()
            elif line.StartsWith("data: ") then
              let json = line.Substring(6)
              match parseEvent currentEvent json with
              | Some evt ->
                state <- SessionStreamState.update evt state
                eventReceived.Trigger(evt)
                stateChanged.Trigger(state)
              | None -> ()
              currentEvent <- "message"
            elif line.Trim() = "" then
              currentEvent <- "message"
      with
      | :? OperationCanceledException -> ()
      | _ ->
        retryDelay <- min (retryDelay * 2) 30000
        do! Task.Delay(retryDelay, newCts.Token)
        if not newCts.Token.IsCancellationRequested then
          do! loop ()
    }
    Task.Run(fun () ->
      task {
        try do! loop ()
        with :? OperationCanceledException -> ()
      } :> Task) |> ignore

  member _.Stop() =
    match cts with
    | Some c -> c.Cancel(); c.Dispose(); cts <- None
    | None -> ()

  interface IDisposable with
    member this.Dispose() =
      this.Stop()
      http.Dispose()
