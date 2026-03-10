module SageTui.Poc.Program

open System
open System.IO
open System.Net.Http
open System.Text.Json
open SageTUI

// ── Domain types ──

type OutputKind =
  | Result
  | Error
  | Info
  | System

type EvalResult = {
  Output: string
  Kind: OutputKind
  Timestamp: DateTimeOffset
}

type SessionInfo = {
  SessionId: string
  SessionState: string
  EvalCount: int
  AvgMs: float
  WorkingDir: string
}

type RegionSnapshot = {
  Id: string
  Content: string
}

// ── TEA model ──

type Model = {
  EvalResults: EvalResult list
  Regions: RegionSnapshot list
  Session: SessionInfo option
  Connected: bool
  StatusMessage: string
  ScrollOffset: int
  MaxVisible: int
}

type Msg =
  | SseDataReceived of string
  | SseError of string
  | ConnectionEstablished
  | ConnectionLost
  | ScrollUp
  | ScrollDown
  | Quit

// ── SSE JSON parsing ──

module SseParse =
  let tryParseState (json: string) : (SessionInfo * RegionSnapshot list) option =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      let session = {
        SessionId =
          match root.TryGetProperty("sessionId") with
          | true, v -> v.GetString()
          | _ -> ""
        SessionState =
          match root.TryGetProperty("sessionState") with
          | true, v -> v.GetString()
          | _ -> "unknown"
        EvalCount =
          match root.TryGetProperty("evalCount") with
          | true, v -> v.GetInt32()
          | _ -> 0
        AvgMs =
          match root.TryGetProperty("avgMs") with
          | true, v -> v.GetDouble()
          | _ -> 0.0
        WorkingDir =
          match root.TryGetProperty("activeWorkingDir") with
          | true, v -> v.GetString()
          | _ -> ""
      }
      let regions =
        match root.TryGetProperty("regions") with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
          [ for elem in arr.EnumerateArray() do
              let id =
                match elem.TryGetProperty("id") with
                | true, v -> v.GetString()
                | _ -> ""
              let content =
                match elem.TryGetProperty("content") with
                | true, v -> v.GetString()
                | _ -> ""
              yield { Id = id; Content = content } ]
        | _ -> []
      Some (session, regions)
    with _ ->
      None

// ── TEA functions ──

let init () : Model * Cmd<Msg> =
  { EvalResults = []
    Regions = []
    Session = None
    Connected = false
    StatusMessage = "Connecting to SageFs daemon..."
    ScrollOffset = 0
    MaxVisible = 20 },
  Cmd.none

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
  match msg with
  | ConnectionEstablished ->
    { model with
        Connected = true
        StatusMessage = "Connected to SageFs daemon" },
    Cmd.none

  | ConnectionLost ->
    { model with
        Connected = false
        StatusMessage = "Connection lost — reconnecting..." },
    Cmd.none

  | SseError err ->
    { model with StatusMessage = sprintf "Error: %s" err }, Cmd.none

  | SseDataReceived json ->
    match SseParse.tryParseState json with
    | Some (session, regions) ->
      let outputRegion =
        regions |> List.tryFind (fun r -> r.Id = "output")
      let newResults =
        match outputRegion with
        | Some region when region.Content.Length > 0 ->
          let lines =
            region.Content.Split('\n')
            |> Array.toList
            |> List.filter (fun l -> l.Length > 0)
          lines
          |> List.map (fun line ->
            let kind =
              match line.StartsWith("error") || line.StartsWith("Error") with
              | true -> Error
              | false ->
                match line.StartsWith("val ") || line.StartsWith("type ") with
                | true -> Result
                | false -> Info
            { Output = line; Kind = kind; Timestamp = DateTimeOffset.Now })
        | _ -> []
      { model with
          Session = Some session
          Regions = regions
          EvalResults = newResults
          StatusMessage =
            sprintf "Session: %s | Evals: %d | Avg: %.0fms"
              session.SessionState session.EvalCount session.AvgMs },
      Cmd.none
    | None ->
      model, Cmd.none

  | ScrollUp ->
    { model with ScrollOffset = max 0 (model.ScrollOffset - 3) }, Cmd.none

  | ScrollDown ->
    let maxOffset = max 0 (model.EvalResults.Length - model.MaxVisible)
    { model with ScrollOffset = min maxOffset (model.ScrollOffset + 3) }, Cmd.none

  | Quit ->
    model, Cmd.quit

// ── View ──

let private kindColor (kind: OutputKind) : Color =
  match kind with
  | Result -> Color.green
  | Error -> Color.red
  | Info -> Color.cyan
  | System -> Color.yellow

let private connectionIndicator (connected: bool) : Element =
  match connected with
  | true ->
    El.row [
      El.text "● " |> El.fg Color.green
      El.text "Connected"
    ]
  | false ->
    El.row [
      El.text "○ " |> El.fg Color.red
      El.text "Disconnected"
    ]

let private sessionBar (model: Model) : Element =
  match model.Session with
  | Some s ->
    El.row [
      El.text (sprintf " %s " s.SessionState) |> El.fg Color.cyan |> El.bold
      El.text " │ "
      El.text (sprintf "Evals: %d" s.EvalCount) |> El.fg Color.yellow
      El.text " │ "
      El.text (sprintf "Avg: %.0fms" s.AvgMs) |> El.fg Color.magenta
      El.text " │ "
      El.text (
        match s.WorkingDir.Length > 40 with
        | true -> "…" + s.WorkingDir.[(s.WorkingDir.Length - 39)..]
        | false -> s.WorkingDir
      ) |> El.dim
    ]
  | None ->
    El.text " No session" |> El.dim

let private regionList (regions: RegionSnapshot list) : Element =
  match regions with
  | [] -> El.text " No regions" |> El.dim
  | _ ->
    El.column [
      for r in regions do
        El.row [
          El.text (sprintf " [%s]" r.Id)
            |> El.fg Color.cyan
            |> El.width 20
          El.text (
            match r.Content.Length > 60 with
            | true -> r.Content.[..59] + "…"
            | false ->
              match r.Content.Length with
              | 0 -> "(empty)"
              | _ -> r.Content.Replace('\n', '↵')
          ) |> El.dim
        ]
    ]

let private outputLines (model: Model) : Element =
  match model.EvalResults with
  | [] ->
    El.column [
      El.empty
      El.text "  Waiting for eval results..." |> El.dim |> El.center
      El.text "  Submit code in SageFs to see output here" |> El.dim |> El.center
      El.empty
    ]
  | results ->
    let visible =
      results
      |> List.skip (min model.ScrollOffset results.Length)
      |> List.truncate model.MaxVisible
    El.column [
      for r in visible do
        El.row [
          El.text (r.Timestamp.ToString("HH:mm:ss"))
            |> El.fg (Color.Named(BaseColor.Black, Bright))
            |> El.width 10
          El.text r.Output
            |> El.fg (kindColor r.Kind)
        ]
    ]

let view (model: Model) : Element =
  El.column [
    // Title bar
    El.row [
      El.text " ⚡ SageFs Output " |> El.fg Color.yellow |> El.bold
      El.text " │ " |> El.dim
      connectionIndicator model.Connected
      El.text "" |> El.fill
      El.text " q:quit ↑↓:scroll " |> El.dim
    ] |> El.bg (Color.Named(BaseColor.Blue, Normal))

    // Session info
    sessionBar model
      |> El.borderedWithTitle "Session" Rounded

    // Regions overview
    regionList model.Regions
      |> El.borderedWithTitle "Regions" Rounded

    // Main output pane
    outputLines model
      |> El.fill
      |> El.borderedWithTitle "Eval Output" Rounded

    // Status bar
    El.row [
      El.text (sprintf " %s" model.StatusMessage) |> El.dim
      El.text "" |> El.fill
      match model.EvalResults.Length > model.MaxVisible with
      | true ->
        El.text (
          sprintf " [%d-%d of %d] "
            (model.ScrollOffset + 1)
            (min (model.ScrollOffset + model.MaxVisible) model.EvalResults.Length)
            model.EvalResults.Length
        ) |> El.fg Color.yellow
      | false ->
        El.text (sprintf " %d lines " model.EvalResults.Length) |> El.dim
    ] |> El.bg (Color.Named(BaseColor.Blue, Normal))
  ]

// ── Subscriptions ──

let private keyBindings =
  Keys.bind [
    Key.Char (Text.Rune 'q'), Quit
    Key.Up, ScrollUp
    Key.Down, ScrollDown
  ]

let subscribe (_model: Model) : Sub<Msg> list =
  [ keyBindings

    Sub.CustomSub("sse-listener", fun dispatch ct -> async {
      // daemon on 37749, dashboard on 37750
      let baseUrl = "http://localhost:37750"
      use handler = new HttpClientHandler()
      use client = new HttpClient(handler)
      client.Timeout <- TimeSpan.FromMinutes(30.0)

      let rec connectLoop (backoff: int) = async {
        match ct.IsCancellationRequested with
        | true -> ()
        | false ->
          try
            let! response =
              client.GetAsync(
                sprintf "%s/api/state" baseUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct)
              |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            dispatch ConnectionEstablished

            use! stream = response.Content.ReadAsStreamAsync(ct) |> Async.AwaitTask
            use reader = new StreamReader(stream)

            let rec readLoop () = async {
              match ct.IsCancellationRequested with
              | true -> ()
              | false ->
                let! line = reader.ReadLineAsync(ct).AsTask() |> Async.AwaitTask
                match line with
                | null ->
                  dispatch ConnectionLost
                  do! Async.Sleep 1000
                  return! connectLoop 1000
                | l when l.StartsWith("data: ") ->
                  let json = l.Substring(6)
                  dispatch (SseDataReceived json)
                  return! readLoop ()
                | _ ->
                  // SSE comment or event: line — skip
                  return! readLoop ()
            }
            do! readLoop ()
          with
          | :? OperationCanceledException -> ()
          | ex ->
            dispatch (SseError (sprintf "%s" ex.Message))
            dispatch ConnectionLost
            let nextBackoff = min 30000 (backoff * 2)
            do! Async.Sleep backoff
            return! connectLoop nextBackoff
      }
      do! connectLoop 1000
    })

    Sub.ResizeSub(fun (_w, h) ->
      // Could dispatch a resize msg; for now ignore
      None)
  ]

// ── Entry point ──

[<EntryPoint>]
let main _argv =
  let program : Program<Model, Msg> = {
    Init = init
    Update = update
    View = view
    Subscribe = subscribe
  }
  App.run program
  0
