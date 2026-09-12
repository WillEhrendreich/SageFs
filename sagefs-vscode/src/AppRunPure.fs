module SageFs.Vscode.AppRunPure

// WHY — this is the pure request-shaping + status-bar-rendering logic for
// the run-app/stop-app HTTP contract the daemon exposes for every editor
// client (POST /api/sessions/{sid}/run-app and .../stop-app — see
// SageFs/McpServer.fs's mapSessionRoutes). It has NO Fable dependency so
// the contract test runs under plain `dotnet fsi` in <2s, mirroring
// CoverageViewPure.fs. The Fable-aware SageFsClient.fs wraps this with its
// JsInterop JSON field helpers to build the real HTTP request/response.

/// Mirrors AppRun.AppStateView (SageFs.Core/AppRun.fs) field-for-field —
/// the JSON shape the daemon serializes on a 200 from run-app/stop-app.
type AppStateView = {
  State: string
  Message: string
  Urls: string list
  EntryPoint: string
  RunId: string
}

let private escapeJsonString (s: string) =
  s.Replace("\\", "\\\\").Replace("\"", "\\\"")

/// The optional JSON body run-app accepts: `{ "project": "<name>" }` when a
/// project was named, `{}` for the default target — matching the daemon's
/// readOptionalProjectName, which also treats a blank/whitespace name as
/// "no project" (SageFs/McpServer.fs).
let requestBodyForRunApp (project: string option) : string =
  match project |> Option.map (fun p -> p.Trim()) with
  | Some p when p <> "" -> sprintf "{\"project\":\"%s\"}" (escapeJsonString p)
  | _ -> "{}"

/// Status-bar text for the session's app state, or None to hide the item
/// entirely — NotRunning means there is nothing to show the user.
let statusBarText (view: AppStateView) : string option =
  match view.State with
  | "Running" ->
    match view.Urls with
    | url :: _ -> Some (sprintf "▶ Running %s" url)
    | [] -> Some "▶ Running"
  | "Starting" -> Some "⏳ Starting"
  | "CouldNotStart" | "BuildFailed" -> Some (sprintf "⚠ %s" view.Message)
  | "RestartRequired" -> Some "↻ Restart required"
  | "NotRunning" -> None
  // Exited/Crashed/LostTrack and any future case: still show the daemon's
  // own reason rather than silently hiding a failure the user needs to see.
  | _ -> Some (sprintf "⚠ %s" view.Message)
