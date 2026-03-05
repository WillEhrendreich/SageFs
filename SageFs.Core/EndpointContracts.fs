namespace SageFs

/// Daemon endpoint contracts: machine-readable definitions of all HTTP endpoints
/// the daemon exposes, used for contract testing against editor plugins.
module EndpointContracts =

  /// HTTP method for an endpoint.
  type HttpMethod = GET | POST

  /// An endpoint definition with method, path template, and description.
  type Endpoint = {
    method: HttpMethod
    path: string
    description: string
    category: string
  }

  module Endpoint =
    let create method path category description = {
      method = method
      path = path
      description = description
      category = category
    }

  /// API contract version. Increment when endpoints are added, removed, or
  /// have breaking changes to request/response shapes. Plugins can check this
  /// against their minimum required version to detect incompatibility.
  let apiVersion = 1

  /// All daemon endpoints grouped by category.
  let coreEndpoints = [
    Endpoint.create POST "/exec" "Execution" "Execute F# code in session"
    Endpoint.create POST "/reset" "Session" "Reset FSI session (soft)"
    Endpoint.create POST "/hard-reset" "Session" "Hard reset FSI session"
    Endpoint.create POST "/cancel" "Execution" "Cancel ongoing evaluation"
    Endpoint.create POST "/load-script" "Execution" "Load an F# script file"
    Endpoint.create GET "/health" "Health" "Health check"
    Endpoint.create GET "/version" "Health" "Get daemon version"
    Endpoint.create GET "/events" "SSE" "SSE stream for state changes"
  ]

  let sessionEndpoints = [
    Endpoint.create GET "/api/sessions" "Sessions" "List all sessions"
    Endpoint.create POST "/api/sessions/create" "Sessions" "Create new session"
    Endpoint.create POST "/api/sessions/switch" "Sessions" "Switch active session"
    Endpoint.create POST "/api/sessions/stop" "Sessions" "Stop a session"
    Endpoint.create GET "/api/sessions/{sid}/export-fsx" "Sessions" "Export session as .fsx"
    Endpoint.create GET "/api/sessions/{sid}/warmup-context" "Sessions" "Get warmup context"
  ]

  let liveTestingEndpoints = [
    Endpoint.create POST "/api/live-testing/enable" "Testing" "Enable live testing"
    Endpoint.create POST "/api/live-testing/disable" "Testing" "Disable live testing"
    Endpoint.create POST "/api/live-testing/policy" "Testing" "Set testing policy"
    Endpoint.create POST "/api/live-testing/run" "Testing" "Run tests"
    Endpoint.create GET "/api/live-testing/status" "Testing" "Get testing status"
    Endpoint.create GET "/api/live-testing/file-annotations" "Testing" "Get gutter annotations"
    Endpoint.create GET "/api/live-testing/test-trace" "Testing" "Get test execution trace"
  ]

  let diagnosticEndpoints = [
    Endpoint.create GET "/diagnostics" "Diagnostics" "Get diagnostics"
    Endpoint.create GET "/api/status" "Diagnostics" "Get API status"
    Endpoint.create GET "/api/recent-events" "Diagnostics" "Get recent events"
    Endpoint.create POST "/api/cancel-eval" "Diagnostics" "Cancel current eval"
    Endpoint.create GET "/diag/threadpool" "Diagnostics" "Thread pool diagnostics"
  ]

  let hotReloadEndpoints = [
    Endpoint.create GET "/api/sessions/{sid}/hotreload" "HotReload" "Get hotreload state"
    Endpoint.create POST "/api/sessions/{sid}/hotreload/toggle" "HotReload" "Toggle file watching"
    Endpoint.create POST "/api/sessions/{sid}/hotreload/watch-all" "HotReload" "Watch all files"
    Endpoint.create POST "/api/sessions/{sid}/hotreload/unwatch-all" "HotReload" "Unwatch all files"
  ]

  let codeAnalysisEndpoints = [
    Endpoint.create POST "/api/explore" "Analysis" "Code exploration"
    Endpoint.create POST "/api/completions" "Analysis" "Code completions"
    Endpoint.create GET "/api/dependency-graph" "Analysis" "Dependency graph"
  ]

  /// All daemon endpoints.
  let all =
    coreEndpoints
    @ sessionEndpoints
    @ liveTestingEndpoints
    @ diagnosticEndpoints
    @ hotReloadEndpoints
    @ codeAnalysisEndpoints

  /// Endpoints expected by the Neovim plugin (sagefs.nvim).
  /// This is the contract surface that must not break without updating the plugin.
  let neovimContract = [
    POST, "/exec"
    GET, "/health"
    GET, "/events"
    GET, "/api/sessions"
    POST, "/api/sessions/create"
    POST, "/api/sessions/switch"
    POST, "/api/sessions/stop"
    GET, "/api/sessions/{sid}/export-fsx"
    GET, "/api/sessions/{sid}/warmup-context"
    POST, "/api/live-testing/run"
    POST, "/api/live-testing/enable"
    POST, "/api/live-testing/disable"
    POST, "/api/live-testing/policy"
    GET, "/api/status"
    POST, "/api/cancel-eval"
    GET, "/api/sessions/{sid}/hotreload"
    POST, "/api/sessions/{sid}/hotreload/toggle"
    POST, "/api/sessions/{sid}/hotreload/watch-all"
    POST, "/api/sessions/{sid}/hotreload/unwatch-all"
  ]

  /// Endpoints expected by the VS Code extension (sagefs-vscode).
  let vscodeContract = [
    POST, "/exec"
    GET, "/health"
    GET, "/events"
    POST, "/reset"
    POST, "/hard-reset"
    POST, "/load-script"
    GET, "/api/sessions"
    POST, "/api/sessions/create"
    POST, "/api/sessions/switch"
    POST, "/api/sessions/stop"
    POST, "/api/live-testing/enable"
    POST, "/api/live-testing/disable"
    POST, "/api/completions"
  ]

  /// Normalize path template for comparison (strip {sid} placeholders).
  let normalizePath (p: string) =
    System.Text.RegularExpressions.Regex.Replace(p, @"\{[^}]+\}", "{id}")

  /// Check if a contract endpoint exists in the daemon's all-endpoints list.
  let validateContract (contract: (HttpMethod * string) list) =
    contract
    |> List.map (fun (m, p) ->
      let np = normalizePath p
      let found =
        all |> List.exists (fun ep ->
          ep.method = m && normalizePath ep.path = np)
      (m, p, found))

  /// Get endpoints in the contract that are NOT in the daemon's endpoint list.
  let missingEndpoints (contract: (HttpMethod * string) list) =
    validateContract contract
    |> List.filter (fun (_, _, found) -> not found)
    |> List.map (fun (m, p, _) -> (m, p))

  /// Get daemon endpoints NOT covered by any contract.
  let uncoveredEndpoints (contracts: (HttpMethod * string) list list) =
    let allContracted =
      contracts
      |> List.concat
      |> List.map (fun (m, p) -> (m, normalizePath p))
      |> Set.ofList
    all
    |> List.filter (fun ep ->
      Set.contains (ep.method, normalizePath ep.path) allContracted |> not)
