namespace SageFs.VisualStudio.Core

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// HTTP client for communicating with the SageFs daemon.
/// Registered as a singleton via DI in the extension entry point.
type SageFsClient() =
  let mutable mcpPort = 37749
  let mutable dashboardPort = 37750
  let http = new HttpClient()

  member _.McpPort
    with get () = mcpPort
    and set v = mcpPort <- v

  member _.DashboardPort
    with get () = dashboardPort
    and set v = dashboardPort <- v

  member private _.BaseUrl = sprintf "http://localhost:%d" mcpPort
  member private _.DashUrl = sprintf "http://localhost:%d" dashboardPort

  /// Check if the daemon is reachable.
  member this.PingAsync(ct: CancellationToken) = task {
    try
      let! resp = http.GetAsync(sprintf "%s/api/sessions" this.BaseUrl, ct)
      return resp.IsSuccessStatusCode
    with _ ->
      return false
  }

  /// Evaluate F# code via the daemon's exec endpoint.
  member this.EvalAsync(code: string, ct: CancellationToken) = task {
    try
      let json =
        sprintf """{"code":%s}""" (JsonSerializer.Serialize(code))
      use content = new StringContent(json, Encoding.UTF8, "application/json")
      let! resp = http.PostAsync(sprintf "%s/exec" this.BaseUrl, content, ct)
      let! body = resp.Content.ReadAsStringAsync(ct)
      return body
    with ex ->
      return sprintf "Error: %s" ex.Message
  }

  /// Get list of active sessions.
  member this.GetSessionsAsync(ct: CancellationToken) = task {
    try
      let! resp =
        http.GetStringAsync(sprintf "%s/api/sessions" this.BaseUrl, ct)
      return resp
    with ex ->
      return sprintf "[]"
  }

  /// Start the daemon process.
  member _.StartDaemonAsync(_ct: CancellationToken) = task {
    // For now, prompt user — daemon lifecycle is external
    return ()
  }

  /// Stop the daemon process.
  member this.StopDaemonAsync(ct: CancellationToken) = task {
    try
      let! _resp =
        http.PostAsync(
          sprintf "%s/api/shutdown" this.BaseUrl,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ ->
      return ()
  }

  /// Create a new FSI session.
  member this.CreateSessionAsync(ct: CancellationToken) = task {
    try
      let! _resp =
        http.PostAsync(
          sprintf "%s/api/sessions/create" this.BaseUrl,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ ->
      return ()
  }

  /// Switch active session.
  member this.SwitchSessionAsync(ct: CancellationToken) = task {
    // TODO: show picker with session list
    return ()
  }

  /// Reset the active session.
  member this.ResetSessionAsync(hard: bool, ct: CancellationToken) = task {
    let endpoint =
      if hard then "api/sessions/hard-reset"
      else "api/sessions/reset"
    try
      let! _resp =
        http.PostAsync(
          sprintf "%s/%s" this.BaseUrl endpoint,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ ->
      return ()
  }

  interface IDisposable with
    member _.Dispose() = http.Dispose()
