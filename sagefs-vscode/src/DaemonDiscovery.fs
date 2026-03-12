module SageFs.Vscode.DaemonDiscovery

open System

[<Literal>]
let defaultMcpPort = 37749

type PortSnapshot =
  { McpPort: int
    DashboardPort: int }

type DiscoveredPorts =
  { McpPort: int option
    DashboardPort: int option }

let deriveDashboardPort (mcpPort: int) =
  mcpPort + 1

let normalizeConfiguredPorts (mcpPort: int) (_dashboardPort: int) : PortSnapshot =
  { McpPort = mcpPort
    DashboardPort = deriveDashboardPort mcpPort }

let resolveDiscoveredPorts (current: PortSnapshot) (discovered: DiscoveredPorts) : PortSnapshot =
  let mcpPort =
    discovered.McpPort
    |> Option.defaultValue current.McpPort

  let dashboardPort =
    deriveDashboardPort mcpPort

  { McpPort = mcpPort
    DashboardPort = dashboardPort }

let candidateMcpPorts (configuredMcpPort: int) (daemonJsonMcpPort: int option) =
  [ daemonJsonMcpPort
    Some configuredMcpPort
    Some defaultMcpPort ]
  |> List.choose id
  |> List.distinct

let private compactJson (json: string) =
  json.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "")

let private tryParseDigits (start: int) (text: string) =
  let mutable idx = start

  while idx < text.Length && Char.IsDigit text.[idx] do
    idx <- idx + 1

  match idx > start with
  | true ->
    let digits = text.Substring(start, idx - start)

    match Int32.TryParse digits with
    | true, value -> Some value
    | _ -> None
  | false -> None

let private tryParseIntProperty (propertyName: string) (json: string) =
  let marker = sprintf "\"%s\":" propertyName
  let idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase)

  match idx >= 0 with
  | true -> tryParseDigits (idx + marker.Length) json
  | false -> None

let private tryParseUrlPort (propertyName: string) (json: string) =
  let marker = sprintf "\"%s\":\"" propertyName
  let idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase)

  match idx >= 0 with
  | false -> None
  | true ->
    let valueStart = idx + marker.Length
    let valueEnd = json.IndexOf('"', valueStart)

    match valueEnd > valueStart with
    | false -> None
    | true ->
      let url = json.Substring(valueStart, valueEnd - valueStart)

      match Uri.TryCreate(url, UriKind.Absolute) with
      | true, uri when uri.Port > 0 -> Some uri.Port
      | _ -> None

let tryParseDaemonJsonMcpPort (json: string) =
  let compact = compactJson json

  tryParseIntProperty "mcpPort" compact
  |> Option.orElseWith (fun () -> tryParseIntProperty "port" compact)
  |> Option.orElseWith (fun () -> tryParseUrlPort "Url" compact)
  |> Option.orElseWith (fun () -> tryParseUrlPort "url" compact)

let buildDaemonStartArgs (projectOrSln: string) (mcpPort: int) =
  let flag =
    match
      projectOrSln.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
      || projectOrSln.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
    with
    | true -> "--sln"
    | false -> "--proj"

  [| flag
     projectOrSln
     "--mcp-port"
     string mcpPort |]
