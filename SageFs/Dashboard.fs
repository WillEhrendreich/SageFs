module SageFs.Server.Dashboard

open System
open Falco
open Falco.Markup
open Falco.Routing
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open System.Text.RegularExpressions
open SageFs
open SageFs.Affordances

module FalcoResponse = Falco.Response

/// Render the dashboard HTML shell.
/// Datastar initializes and connects to the /dashboard/stream SSE endpoint.
let private renderShell (version: string) =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ Text.raw "SageFs Dashboard" ]
      Ds.cdnScript
      Elem.style [] [ Text.raw """
        :root { --bg: #0d1117; --fg: #c9d1d9; --accent: #58a6ff; --green: #3fb950; --red: #f85149; --border: #30363d; --surface: #161b22; --yellow: #d29922; }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: 'Cascadia Code', 'Fira Code', monospace; background: var(--bg); color: var(--fg); padding: 1rem; }
        h1 { color: var(--accent); margin-bottom: 1rem; font-size: 1.4rem; }
        .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
        .panel { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 1rem; }
        .panel h2 { color: var(--accent); font-size: 1rem; margin-bottom: 0.5rem; border-bottom: 1px solid var(--border); padding-bottom: 0.5rem; display: flex; justify-content: space-between; align-items: center; }
        .status { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; font-weight: bold; }
        .status-ready { background: var(--green); color: var(--bg); }
        .status-warming { background: var(--yellow); color: var(--bg); }
        .status-faulted { background: var(--red); color: white; }
        .output-line { font-size: 0.85rem; padding: 2px 0; border-bottom: 1px solid var(--border); white-space: pre-wrap; word-break: break-all; }
        .output-result { color: var(--green); }
        .output-error { color: var(--red); }
        .output-info { color: var(--accent); }
        .output-system { color: #8b949e; }
        .diag { font-size: 0.85rem; padding: 4px 0; }
        .diag-error { color: var(--red); }
        .diag-warning { color: var(--yellow); }
        .meta { color: #8b949e; font-size: 0.8rem; }
        .eval-input { width: 100%; background: var(--bg); color: var(--fg); border: 1px solid var(--border); border-radius: 4px; padding: 0.5rem; font-family: inherit; font-size: 0.9rem; resize: vertical; min-height: 80px; tab-size: 2; }
        .eval-input:focus { outline: 1px solid var(--accent); border-color: var(--accent); }
        .eval-btn { background: var(--accent); color: var(--bg); border: none; border-radius: 4px; padding: 0.5rem 1rem; cursor: pointer; font-family: inherit; font-weight: bold; margin-top: 0.5rem; transition: opacity 0.15s; }
        .eval-btn:hover { opacity: 0.85; }
        .eval-btn:active { opacity: 0.7; }
        .session-btn { background: var(--border); color: var(--fg); border: none; border-radius: 4px; padding: 2px 8px; cursor: pointer; font-size: 0.8rem; transition: background 0.15s; }
        .session-btn:hover { background: var(--accent); color: var(--bg); }
        .session-btn-danger:hover { background: var(--red); color: white; }
        .full-width { grid-column: 1 / -1; }
        .panel-header-btn { background: none; border: 1px solid var(--border); color: var(--fg); border-radius: 4px; padding: 1px 8px; cursor: pointer; font-size: 0.75rem; font-family: inherit; }
        .panel-header-btn:hover { background: var(--border); }
        .auto-scroll { scroll-behavior: smooth; }
      """ ]
    ]
    Elem.body [] [
      Elem.h1 [] [ Text.raw (sprintf "🧙 SageFs Dashboard v%s" version) ]
      Elem.div [ Attr.class' "grid" ] [
        // Row 1: Session status + Eval stats
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Session" ]
          Elem.div [ Attr.id "session-status"; Ds.onInit (Ds.get "/dashboard/stream") ] [
            Text.raw "Connecting..."
          ]
        ]
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Eval Stats" ]
          Elem.div [ Attr.id "eval-stats" ] [
            Text.raw "Waiting for data..."
          ]
        ]
        // Row 2: Evaluate (full width)
        Elem.div [ Attr.class' "panel full-width" ] [
          Elem.h2 [] [ Text.raw "Evaluate" ]
          Elem.textarea
            [ Attr.class' "eval-input"
              Ds.bind "code"
              Attr.create "placeholder" "Enter F# code... (Ctrl+Enter to eval)"
              Attr.create "data-on-keydown" "if(event.ctrlKey && event.key === 'Enter') { event.preventDefault(); $$post('/dashboard/eval') }"
              Attr.create "spellcheck" "false" ]
            []
          Elem.div [ Attr.style "display: flex; gap: 0.5rem; margin-top: 0.5rem;" ] [
            Elem.button
              [ Attr.class' "eval-btn"
                Ds.onClick (Ds.post "/dashboard/eval") ]
              [ Text.raw "▶ Eval" ]
            Elem.button
              [ Attr.class' "eval-btn"
                Attr.style "background: var(--green);"
                Ds.onClick (Ds.post "/dashboard/reset") ]
              [ Text.raw "↻ Reset" ]
            Elem.button
              [ Attr.class' "eval-btn"
                Attr.style "background: var(--red);"
                Ds.onClick (Ds.post "/dashboard/hard-reset") ]
              [ Text.raw "⟳ Hard Reset" ]
          ]
          Elem.div [ Attr.id "eval-result" ] []
        ]
        // Row 3: Output (full width)
        Elem.div [ Attr.class' "panel full-width" ] [
          Elem.h2 [] [
            Text.raw "Output"
            Elem.button
              [ Attr.class' "panel-header-btn"
                Ds.onClick (Ds.post "/dashboard/clear-output") ]
              [ Text.raw "Clear" ]
          ]
          Elem.div
            [ Attr.id "output-panel"
              Attr.class' "auto-scroll"
              Attr.style "max-height: 400px; overflow-y: auto;" ]
            [ Text.raw "No output yet" ]
        ]
        // Row 4: Sessions + Diagnostics
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Sessions" ]
          Elem.div [ Attr.id "sessions-panel"; Attr.style "max-height: 300px; overflow-y: auto;" ] [
            Text.raw "No sessions"
          ]
        ]
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Diagnostics" ]
          Elem.div [ Attr.id "diagnostics-panel"; Attr.style "max-height: 300px; overflow-y: auto;" ] [
            Text.raw "No diagnostics"
          ]
        ]
      ]
      // Auto-scroll output panel when new content arrives
      Elem.script [] [ Text.raw """
        new MutationObserver(function(mutations) {
          var panel = document.getElementById('output-panel');
          if (panel) panel.scrollTop = panel.scrollHeight;
        }).observe(document.getElementById('output-panel') || document.body, { childList: true, subtree: true });
      """ ]
    ]
  ]

/// Render session status as an HTML fragment for Datastar morphing.
let renderSessionStatus (sessionState: string) (sessionId: string) (projectCount: int) =
  let statusClass =
    match sessionState with
    | "Ready" -> "status-ready"
    | "WarmingUp" -> "status-warming"
    | _ -> "status-faulted"
  Elem.div [ Attr.id "session-status" ] [
    Elem.span [ Attr.class' (sprintf "status %s" statusClass) ] [ Text.raw sessionState ]
    Elem.br []
    Elem.span [ Attr.class' "meta" ] [
      Text.raw (sprintf "Session: %s | Projects: %d" sessionId projectCount)
    ]
  ]

/// Render eval stats as an HTML fragment.
let renderEvalStats (evalCount: int) (avgMs: float) (minMs: float) (maxMs: float) =
  Elem.div [ Attr.id "eval-stats" ] [
    Elem.div [] [ Text.raw (sprintf "Evals: %d" evalCount) ]
    Elem.div [ Attr.class' "meta" ] [
      Text.raw (sprintf "Avg: %.0fms | Min: %.0fms | Max: %.0fms" avgMs minMs maxMs)
    ]
  ]

/// Render output lines as an HTML fragment.
let renderOutput (lines: (string option * string * string) list) =
  let lineClass kind =
    match kind with
    | "Result" -> "output-result"
    | "Error" -> "output-error"
    | "Info" -> "output-info"
    | _ -> "output-system"
  Elem.div [ Attr.id "output-panel" ] [
    if lines.IsEmpty then
      Text.raw "No output yet"
    else
      yield! lines |> List.map (fun (ts, kind, text) ->
        Elem.div [ Attr.class' (sprintf "output-line %s" (lineClass kind)) ] [
          match ts with
          | Some t ->
            Elem.span [ Attr.class' "meta"; Attr.style "margin-right: 0.5rem;" ] [
              Text.raw t
            ]
          | None -> ()
          Text.raw text
        ])
  ]

/// Render diagnostics as an HTML fragment.
let renderDiagnostics (diags: (string * string * int * int) list) =
  Elem.div [ Attr.id "diagnostics-panel" ] [
    if diags.IsEmpty then
      Text.raw "No diagnostics"
    else
      yield! diags |> List.map (fun (severity, message, line, col) ->
        let cls = if severity = "Error" then "diag-error" else "diag-warning"
        Elem.div [ Attr.class' (sprintf "diag %s" cls) ] [
          Text.raw (sprintf "[%s] L%d:%d %s" severity line col message)
        ])
  ]

type ParsedSession = {
  Id: string
  Status: string
  IsActive: bool
  ProjectsText: string
  EvalCount: int
}

let private parseSessionLines (content: string) =
  let sessionRegex = Regex(@"^(\S+)\s*\[([^\]]+)\](\s*\*)?(\s*\([^)]*\))?(\s*evals:\d+)?")
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = sessionRegex.Match(l)
    if m.Success then
      let evalsMatch = Regex.Match(m.Groups.[5].Value, @"evals:(\d+)")
      { Id = m.Groups.[1].Value
        Status = m.Groups.[2].Value
        IsActive = m.Groups.[3].Value.Contains("*")
        ProjectsText = m.Groups.[4].Value.Trim()
        EvalCount = if evalsMatch.Success then int evalsMatch.Groups.[1].Value else 0 }
    else
      { Id = l.Trim()
        Status = "unknown"
        IsActive = false
        ProjectsText = ""
        EvalCount = 0 })
  |> Array.toList

/// Render sessions as an HTML fragment with action buttons.
let renderSessions (sessions: ParsedSession list) =
  Elem.div [ Attr.id "sessions-panel" ] [
    if sessions.IsEmpty then
      Text.raw "No sessions"
    else
      yield! sessions |> List.map (fun (s: ParsedSession) ->
        let statusClass =
          match s.Status with
          | "running" -> "status-ready"
          | "starting" | "restarting" -> "status-warming"
          | _ -> "status-faulted"
        let cls = if s.IsActive then "output-result" else ""
        Elem.div
          [ Attr.class' (sprintf "session-row %s" cls)
            Attr.style "display: flex; align-items: center; justify-content: space-between; padding: 6px 0; border-bottom: 1px solid var(--border);" ]
          [
            Elem.div [ Attr.style "flex: 1;" ] [
              Elem.span [ Attr.style "font-weight: bold;" ] [ Text.raw s.Id ]
              Text.raw " "
              Elem.span [ Attr.class' (sprintf "status %s" statusClass); Attr.style "font-size: 0.75rem;" ] [
                Text.raw s.Status
              ]
              if s.IsActive then
                Elem.span [ Attr.style "color: var(--green); margin-left: 0.5rem;" ] [ Text.raw "●" ]
              if s.ProjectsText.Length > 0 then
                Elem.br []
                Elem.span [ Attr.class' "meta" ] [ Text.raw s.ProjectsText ]
              if s.EvalCount > 0 then
                Elem.span [ Attr.class' "meta"; Attr.style "margin-left: 0.5rem;" ] [
                  Text.raw (sprintf "evals: %d" s.EvalCount)
                ]
            ]
            Elem.div [ Attr.style "display: flex; gap: 4px;" ] [
              if not s.IsActive then
                Elem.button
                  [ Attr.class' "session-btn"
                    Ds.onClick (Ds.post (sprintf "/dashboard/session/switch/%s" s.Id)) ]
                  [ Text.raw "⇄" ]
              Elem.button
                [ Attr.class' "session-btn session-btn-danger"
                  Ds.onClick (Ds.post (sprintf "/dashboard/session/stop/%s" s.Id)) ]
                [ Text.raw "■" ]
            ]
          ])
  ]

let private parseOutputLines (content: string) =
  let tsKindRegex = Regex(@"^\[(\d{2}:\d{2}:\d{2})\]\s*\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  let kindOnlyRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = tsKindRegex.Match(l)
    if m.Success then
      let kind =
        match m.Groups.[2].Value.ToLowerInvariant() with
        | "result" -> "Result"
        | "error" -> "Error"
        | "info" -> "Info"
        | _ -> "System"
      Some m.Groups.[1].Value, kind, m.Groups.[3].Value
    else
      let m2 = kindOnlyRegex.Match(l)
      if m2.Success then
        let kind =
          match m2.Groups.[1].Value.ToLowerInvariant() with
          | "result" -> "Result"
          | "error" -> "Error"
          | "info" -> "Info"
          | _ -> "System"
        None, kind, m2.Groups.[2].Value
      else
        None, "Result", l)
  |> Array.toList

let private parseDiagLines (content: string) =
  let diagRegex = Regex(@"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = diagRegex.Match(l)
    if m.Success then
      let severity = if m.Groups.[1].Value = "error" then "Error" else "Warning"
      let line = int m.Groups.[2].Value
      let col = int m.Groups.[3].Value
      let message = m.Groups.[4].Value
      severity, message, line, col
    else
      let severity = if l.Contains("[error]") then "Error" else "Warning"
      severity, l, 0, 0)
  |> Array.toList



let private pushRegions
  (ctx: HttpContext)
  (regions: RenderRegion list)
  = task {
    match regions |> List.tryFind (fun r -> r.Id = "output") with
    | Some r ->
      do! Response.sseHtmlElements ctx (renderOutput (parseOutputLines r.Content))
    | None -> ()
    match regions |> List.tryFind (fun r -> r.Id = "diagnostics") with
    | Some r ->
      do! Response.sseHtmlElements ctx (renderDiagnostics (parseDiagLines r.Content))
    | None -> ()
    match regions |> List.tryFind (fun r -> r.Id = "sessions") with
    | Some r ->
      do! Response.sseHtmlElements ctx (renderSessions (parseSessionLines r.Content))
    | None -> ()
  }

/// Create the SSE stream handler that pushes Elm state to the browser.
let createStreamHandler
  (getSessionState: unit -> SessionState)
  (getEvalStats: unit -> EvalStats)
  (sessionId: string)
  (projectCount: int)
  (getElmRegions: unit -> RenderRegion list option)
  (stateChanged: IEvent<string> option)
  : HttpHandler =
  fun ctx -> task {
    do! Response.sseStartResponse ctx

    let pushState () = task {
      let state = getSessionState ()
      let stats = getEvalStats ()
      let stateStr = SessionState.label state
      let avgMs =
        if stats.EvalCount > 0
        then stats.TotalDuration.TotalMilliseconds / float stats.EvalCount
        else 0.0
      do! Response.sseHtmlElements ctx (
        renderSessionStatus stateStr sessionId projectCount)
      do! Response.sseHtmlElements ctx (
        renderEvalStats
          stats.EvalCount
          avgMs
          stats.MinDuration.TotalMilliseconds
          stats.MaxDuration.TotalMilliseconds)
      match getElmRegions () with
      | Some regions -> do! pushRegions ctx regions
      | None -> ()
    }

    // Push initial state
    do! pushState ()

    match stateChanged with
    | Some evt ->
      // Event-driven: push on every state change
      let tcs = Threading.Tasks.TaskCompletionSource()
      use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
      use _sub = evt.Subscribe(fun _ ->
        try pushState().Wait()
        with _ -> ())
      do! tcs.Task
    | None ->
      // Fallback: poll every second
      while not ctx.RequestAborted.IsCancellationRequested do
        try
          do! Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 1.0, ctx.RequestAborted)
          do! pushState ()
        with
        | :? OperationCanceledException -> ()
  }

/// Create the eval POST handler.
let createEvalHandler
  (evalCode: string -> Threading.Tasks.Task<string>)
  : HttpHandler =
  fun ctx -> task {
    let! doc = Request.getSignalsJson ctx
    let code =
      match doc.RootElement.TryGetProperty("code") with
      | true, prop -> prop.GetString()
      | _ -> ""
    if String.IsNullOrWhiteSpace code then
      do! Response.sseStartResponse ctx
      do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
    else
      let! result = evalCode code
      do! Response.sseStartResponse ctx
      do! Response.ssePatchSignal ctx (SignalPath.sp "code") ""
      // Show result inline below the eval button
      let resultHtml =
        Elem.div [ Attr.id "eval-result" ] [
          Elem.pre [ Attr.class' "output-line output-result"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
            Text.raw result
          ]
        ]
      do! Response.sseHtmlElements ctx resultHtml
  }

/// Create the reset POST handler.
let createResetHandler
  (resetSession: unit -> Threading.Tasks.Task<string>)
  : HttpHandler =
  fun ctx -> task {
    let! result = resetSession ()
    do! Response.sseStartResponse ctx
    let resultHtml =
      Elem.div [ Attr.id "eval-result" ] [
        Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
          Text.raw (sprintf "Reset: %s" result)
        ]
      ]
    do! Response.sseHtmlElements ctx resultHtml
  }

/// Create the session action handler (switch/stop).
let createSessionActionHandler
  (action: string -> Threading.Tasks.Task<string>)
  : string -> HttpHandler =
  fun sessionId ctx -> task {
    let! result = action sessionId
    do! Response.sseStartResponse ctx
    let resultHtml =
      Elem.div [ Attr.id "eval-result" ] [
        Elem.pre [ Attr.class' "output-line output-info"; Attr.style "margin-top: 0.5rem; white-space: pre-wrap;" ] [
          Text.raw result
        ]
      ]
    do! Response.sseHtmlElements ctx resultHtml
  }

/// Create clear-output handler.
let createClearOutputHandler : HttpHandler =
  fun ctx -> task {
    do! Response.sseStartResponse ctx
    let emptyOutput = Elem.div [ Attr.id "output-panel" ] [ Text.raw "No output yet" ]
    do! Response.sseHtmlElements ctx emptyOutput
  }

/// Create all dashboard routes.
let createEndpoints
  (version: string)
  (getSessionState: unit -> SessionState)
  (getEvalStats: unit -> EvalStats)
  (sessionId: string)
  (projectCount: int)
  (getElmRegions: unit -> RenderRegion list option)
  (stateChanged: IEvent<string> option)
  (evalCode: string -> Threading.Tasks.Task<string>)
  (resetSession: unit -> Threading.Tasks.Task<string>)
  (hardResetSession: unit -> Threading.Tasks.Task<string>)
  (switchSession: (string -> Threading.Tasks.Task<string>) option)
  (stopSession: (string -> Threading.Tasks.Task<string>) option)
  : HttpEndpoint list =
  [
    yield get "/dashboard" (FalcoResponse.ofHtml (renderShell version))
    yield get "/dashboard/stream" (createStreamHandler getSessionState getEvalStats sessionId projectCount getElmRegions stateChanged)
    yield post "/dashboard/eval" (createEvalHandler evalCode)
    yield post "/dashboard/reset" (createResetHandler resetSession)
    yield post "/dashboard/hard-reset" (createResetHandler hardResetSession)
    yield post "/dashboard/clear-output" createClearOutputHandler
    match switchSession with
    | Some handler ->
      yield mapPost "/dashboard/session/switch/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sid -> createSessionActionHandler handler sid)
    | None -> ()
    match stopSession with
    | Some handler ->
      yield mapPost "/dashboard/session/stop/{id}"
        (fun (r: RequestData) -> r.GetString("id", ""))
        (fun sid -> createSessionActionHandler handler sid)
    | None -> ()
  ]
