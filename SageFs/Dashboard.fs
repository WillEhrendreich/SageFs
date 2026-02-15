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
        :root { --bg: #0d1117; --fg: #c9d1d9; --accent: #58a6ff; --green: #3fb950; --red: #f85149; --border: #30363d; --surface: #161b22; }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: 'Cascadia Code', 'Fira Code', monospace; background: var(--bg); color: var(--fg); padding: 1rem; }
        h1 { color: var(--accent); margin-bottom: 1rem; font-size: 1.4rem; }
        .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
        .panel { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 1rem; }
        .panel h2 { color: var(--accent); font-size: 1rem; margin-bottom: 0.5rem; border-bottom: 1px solid var(--border); padding-bottom: 0.5rem; }
        .status { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; font-weight: bold; }
        .status-ready { background: var(--green); color: var(--bg); }
        .status-warming { background: #d29922; color: var(--bg); }
        .status-faulted { background: var(--red); color: white; }
        .output-line { font-size: 0.85rem; padding: 2px 0; border-bottom: 1px solid var(--border); white-space: pre-wrap; word-break: break-all; }
        .output-result { color: var(--green); }
        .output-error { color: var(--red); }
        .output-info { color: var(--accent); }
        .output-system { color: #8b949e; }
        .diag { font-size: 0.85rem; padding: 4px 0; }
        .diag-error { color: var(--red); }
        .diag-warning { color: #d29922; }
        .meta { color: #8b949e; font-size: 0.8rem; }
        .eval-input { width: 100%; background: var(--bg); color: var(--fg); border: 1px solid var(--border); border-radius: 4px; padding: 0.5rem; font-family: inherit; font-size: 0.9rem; resize: vertical; min-height: 60px; }
        .eval-btn { background: var(--accent); color: var(--bg); border: none; border-radius: 4px; padding: 0.5rem 1rem; cursor: pointer; font-family: inherit; font-weight: bold; margin-top: 0.5rem; }
        .eval-btn:hover { opacity: 0.9; }
        .full-width { grid-column: 1 / -1; }
      """ ]
    ]
    Elem.body [] [
      Elem.h1 [] [ Text.raw (sprintf "🧙 SageFs Dashboard v%s" version) ]
      Elem.div [ Attr.class' "grid" ] [
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
        Elem.div [ Attr.class' "panel full-width" ] [
          Elem.h2 [] [ Text.raw "Output" ]
          Elem.div [ Attr.id "output-panel"; Attr.style "max-height: 400px; overflow-y: auto;" ] [
            Text.raw "No output yet"
          ]
        ]
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
        Elem.div [ Attr.class' "panel" ] [
          Elem.h2 [] [ Text.raw "Evaluate" ]
          Elem.textarea
            [ Attr.class' "eval-input"
              Ds.bind "code"
              Attr.create "placeholder" "Enter F# code... (Ctrl+Enter to eval)"
              Attr.create "data-on-keydown" "if(event.ctrlKey && event.key === 'Enter') { event.preventDefault(); $$post('/dashboard/eval') }" ]
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
      ]
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
let renderOutput (lines: (string * string) list) =
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
      yield! lines |> List.map (fun (kind, text) ->
        Elem.div [ Attr.class' (sprintf "output-line %s" (lineClass kind)) ] [
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

/// Render sessions as an HTML fragment.
let renderSessions (sessions: (string * string * bool) list) =
  Elem.div [ Attr.id "sessions-panel" ] [
    if sessions.IsEmpty then
      Text.raw "No sessions"
    else
      yield! sessions |> List.map (fun (id, status, isActive) ->
        let cls = if isActive then "output-result" else "meta"
        let marker = if isActive then " ●" else ""
        Elem.div [ Attr.class' (sprintf "output-line %s" cls) ] [
          Text.raw (sprintf "%s [%s]%s" id status marker)
        ])
  ]

let private parseOutputLines (content: string) =
  let outputRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let m = outputRegex.Match(l)
    if m.Success then
      let kind =
        match m.Groups.[1].Value.ToLowerInvariant() with
        | "result" -> "Result"
        | "error" -> "Error"
        | "info" -> "Info"
        | _ -> "System"
      kind, m.Groups.[2].Value
    else
      "Result", l)
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

let private parseSessionLines (content: string) =
  content.Split('\n')
  |> Array.filter (fun (l: string) -> l.Length > 0)
  |> Array.map (fun (l: string) ->
    let isActive = l.EndsWith(" *")
    let trimmed = if isActive then l.[..l.Length - 3] else l
    let parts = trimmed.Split(" [")
    let id = if parts.Length > 0 then parts.[0] else trimmed
    let status =
      if parts.Length > 1 then parts.[1].TrimEnd(']')
      else "unknown"
    id, status, isActive)
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
  : HttpEndpoint list =
  [
    get "/dashboard" (FalcoResponse.ofHtml (renderShell version))
    get "/dashboard/stream" (createStreamHandler getSessionState getEvalStats sessionId projectCount getElmRegions stateChanged)
    post "/dashboard/eval" (createEvalHandler evalCode)
    post "/dashboard/reset" (createResetHandler resetSession)
    post "/dashboard/hard-reset" (createResetHandler hardResetSession)
  ]
