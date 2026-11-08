namespace SageFs

open System
open System.Text.Json
open System.Text.Json.Nodes

/// The agent clients `sagefs setup` knows how to register SageFs in.
type Client =
  | ClaudeCode
  | VsCode
  | Cursor
  | ClaudeDesktop

/// The operating systems a client keeps its config on, which is where Claude Desktop's file differs.
type Platform =
  | Windows
  | MacOs
  | Linux

/// A name that is not one of the clients, as the person typed it.
type UnknownClient = UnknownClient of given: string

module Client =
  /// Every client, in the order the setup page lists them.
  let all : Client list = [ ClaudeCode; VsCode; Cursor; ClaudeDesktop ]

  /// The word `sagefs setup --client` takes.
  let toToken (client: Client) : string =
    match client with
    | ClaudeCode -> "claude-code"
    | VsCode -> "vscode"
    | Cursor -> "cursor"
    | ClaudeDesktop -> "claude-desktop"

  /// What the page and the command call it.
  let label (client: Client) : string =
    match client with
    | ClaudeCode -> "Claude Code"
    | VsCode -> "VS Code"
    | Cursor -> "Cursor"
    | ClaudeDesktop -> "Claude Desktop"

  let parse (text: string) : Result<Client, UnknownClient> =
    match all |> List.tryFind (fun client -> String.Equals(toToken client, text, StringComparison.OrdinalIgnoreCase)) with
    | Some client -> Ok client
    | None -> Error(UnknownClient text)

  let describeUnknown (UnknownClient given: UnknownClient) : string =
    sprintf "'%s' is not a client setup knows. The choices are: %s." given (all |> List.map toToken |> String.concat ", ")

/// Where a client reads its MCP servers from and what the entry looks like. Pure: no file is read or written here.
type Snippet =
  { Client: Client
    /// The file the text goes in, said for a person (a path or a place in the client's own UI).
    File: string
    /// The top-level key the client reads its servers under.
    Key: string
    /// The whole file when it does not exist yet.
    Text: string
    /// The client's own documentation page the file, key and entry were read from.
    Source: string }

type MergeRefusal =
  /// The file is not strict JSON (comments, a trailing comma, or half a file).
  | NotJson of detail: string
  /// The file is JSON but its top level is not an object.
  | NotAnObject
  /// The file has the servers key, and it is not an object.
  | ServersNotAnObject of key: string
  /// The file already has a server called sagefs that is not the stdio bridge. `found` is that entry as written.
  | DifferentSageFsEntry of found: string

type MergeResult =
  /// The file's new text, every other key and server kept.
  | Merged of text: string
  /// The file already registers sagefs as the stdio bridge. Nothing to write.
  | AlreadyPresent
  | Refused of MergeRefusal

module ClientSetup =
  let private json (key: string) (typeLine: string) : string =
    sprintf "{\n  \"%s\": {\n    \"sagefs\": {\n%s      \"command\": \"sagefs\",\n      \"args\": [ \"mcp\" ]\n    }\n  }\n}" key typeLine

  let snippet (client: Client) : Snippet =
    match client with
    | ClaudeCode ->
      { Client = client; File = ".mcp.json in the project (or ~/.claude.json for every project)"; Key = "mcpServers"; Text = json "mcpServers" ""; Source = "https://code.claude.com/docs/en/mcp" }
    | VsCode ->
      { Client = client; File = ".vscode/mcp.json in the workspace (or `MCP: Open User Configuration` for every workspace)"; Key = "servers"; Text = json "servers" "      \"type\": \"stdio\",\n"; Source = "https://code.visualstudio.com/docs/copilot/reference/mcp-configuration" }
    | Cursor ->
      { Client = client; File = ".cursor/mcp.json in the project (or ~/.cursor/mcp.json for every project)"; Key = "mcpServers"; Text = json "mcpServers" "      \"type\": \"stdio\",\n"; Source = "https://cursor.com/docs/context/mcp" }
    | ClaudeDesktop ->
      { Client = client; File = "claude_desktop_config.json (Settings, Developer, Edit Config)"; Key = "mcpServers"; Text = json "mcpServers" ""; Source = "https://modelcontextprotocol.io/quickstart/user" }

  /// The command a client has for registering a server itself. Only Claude Code has one. User scope, so a second
  /// project does not open without tools.
  let registrationCommand (client: Client) : string list option =
    match client with
    | ClaudeCode -> Some [ "claude"; "mcp"; "add"; "--scope"; "user"; "sagefs"; "--"; "sagefs"; "mcp" ]
    | VsCode | Cursor | ClaudeDesktop -> None

  let describeRefusal (reason: MergeRefusal) : string =
    match reason with
    | NotJson detail ->
      sprintf "The file is not strict JSON (%s), so it was left alone. → Add the sagefs entry by hand from the snippet." detail
    | NotAnObject -> "The file's top level is not a JSON object, so it was left alone. → Add the sagefs entry by hand from the snippet."
    | ServersNotAnObject key ->
      sprintf "The file has a '%s' key that is not an object, so it was left alone. → Fix that key, or add the sagefs entry by hand from the snippet." key
    | DifferentSageFsEntry found ->
      sprintf "The file already has a server called sagefs that is not the stdio bridge: %s. → Replace it by hand with the snippet if you mean to." found

  let private strict = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)

  /// Indented text of a node. A writer with its own options, so no serializer is configured here (SageFs.Json is the
  /// one place that does that).
  let private indented (node: JsonNode) : string =
    use stream = new IO.MemoryStream()
    (use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
     node.WriteTo writer)
    Text.Encoding.UTF8.GetString(stream.ToArray())

  /// The text of a JSON string, or none when the node is anything else.
  let private stringOf (node: JsonNode) : string option =
    match node with
    | :? JsonValue as value ->
      match value.TryGetValue<string>() with
      | true, text -> Some text
      | false, _ -> None
    | _ -> None

  let private isStdioBridge (entry: JsonNode) : bool =
    match entry with
    | :? JsonObject as o ->
      let args =
        match o["args"] with
        | :? JsonArray as a -> a |> Seq.map stringOf |> Seq.toList
        | _ -> []
      stringOf o["command"] = Some "sagefs" && args = [ Some "mcp" ]
    | _ -> false

  /// Add the sagefs entry to a client's config file, which may hold other servers. Every other key and server is
  /// kept. A file that is not strict JSON, or that already names sagefs as something else, is refused and the
  /// caller writes nothing.
  let merge (existing: string) (snippet: Snippet) : MergeResult =
    match String.IsNullOrWhiteSpace existing with
    | true -> Merged snippet.Text
    | false ->
      let parsed =
        try Ok(JsonNode.Parse(existing, documentOptions = strict))
        with :? JsonException as ex -> Error(NotJson ex.Message)
      match parsed with
      | Error reason -> Refused reason
      | Ok null -> Refused(NotJson "the file holds null")
      | Ok(:? JsonObject as root) ->
        let add (servers: JsonObject) =
          let whole = JsonNode.Parse(snippet.Text, documentOptions = strict)
          let entry = whole[snippet.Key]["sagefs"]
          servers["sagefs"] <- entry.DeepClone()
          Merged(indented root)
        match root[snippet.Key] with
        | null ->
          let servers = JsonObject()
          root[snippet.Key] <- servers
          add servers
        | :? JsonObject as servers ->
          match servers["sagefs"] with
          | null -> add servers
          | present when isStdioBridge present -> AlreadyPresent
          | present -> Refused(DifferentSageFsEntry(present.ToJsonString()))
        | _ -> Refused(ServersNotAnObject snippet.Key)
      | Ok _ -> Refused NotAnObject

  /// The day the snippets were last compared with each client's documentation page (`Snippet.Source`).
  let checkedOn = "2026-11-07"

  /// The text the generated part of docs/client-setup.md holds, and what `sagefs setup` prints. One function, so the
  /// page and the command say the same thing.
  let page : string =
    let b = System.Text.StringBuilder()
    let line (s: string) = b.Append(s).Append('\n') |> ignore
    line "### Register SageFs: stdio, the form that keeps its tools"
    line ""
    line "Run the daemon-starting bridge as a stdio server. The client starts `sagefs mcp`, the bridge starts the daemon if it is down, and the client talks to the bridge over a pipe."
    line ""
    line "```bash"
    line "dotnet tool install --global SageFs"
    line "claude mcp add --scope user sagefs -- sagefs mcp"
    line "```"
    line ""
    line "`--scope user` registers it for every project. Without it Claude Code registers for the current project only, and a second project opens with no SageFs tools."
    line ""
    for client in Client.all do
      let s = snippet client
      line (sprintf "#### %s" (Client.label client))
      line ""
      line (sprintf "File: %s. Key: `%s`. Read from %s on %s." s.File s.Key s.Source checkedOn)
      line ""
      line "```json"
      line s.Text
      line "```"
      line ""
    line "### Why an HTTP registration loses its tools"
    line ""
    line "`claude mcp add --transport http sagefs http://localhost:37749/` works until the daemon restarts. Two things go wrong. A client that fails its first connect usually caches the failure and does not retry after the daemon comes up. And the daemon keeps the `Mcp-Session-Id` in memory, so after a restart (which `install-local` does on purpose) the client holds an id the daemon never minted and every call answers `404 Session not found`. The protocol cannot recover an id in place. Over stdio the bridge owns the handshake and heals the id itself, and the bridge keeps serving across a daemon stop and start, so the client never sees either problem."
    b.ToString().TrimEnd('\n')

  /// The two marker lines are not there exactly once each, in order.
  type MarkerRefusal = MarkersMissingOrRepeated

  let describeMarkerRefusal (MarkersMissingOrRepeated: MarkerRefusal) : string =
    "docs/client-setup.md needs exactly one GENERATED start line followed by one END GENERATED line. → Restore the two marker lines."

  let generatedStart = "<!-- GENERATED: client-setup (scripts/gen-client-setup-doc.fsx) -->"
  let generatedEnd = "<!-- END GENERATED -->"

  /// `doc` with the part between the two markers replaced by `section`. An error says what is wrong with the markers,
  /// so a missing or doubled marker fails loudly instead of rewriting the wrong thing.
  let replaceGenerated (doc: string) (section: string) : Result<string, MarkerRefusal> =
    let first (marker: string) = doc.IndexOf(marker, StringComparison.Ordinal)
    let last (marker: string) = doc.LastIndexOf(marker, StringComparison.Ordinal)
    match first generatedStart, last generatedStart, first generatedEnd, last generatedEnd with
    | s, s2, e, e2 when s >= 0 && s = s2 && e > s && e = e2 ->
      let head = doc.Substring(0, s + generatedStart.Length)
      let tail = doc.Substring e
      Ok(head + "\n" + section + "\n" + tail)
    | _ -> Error MarkersMissingOrRepeated

  /// Where a client's config lives, for the operating system and the directory `sagefs setup` runs for. Claude Code
  /// registers itself through its own command, so for it this is the project file the snippet describes.
  let configFile (platform: Platform) (home: string) (appData: string) (dir: string) (client: Client) : string =
    match client with
    | ClaudeCode -> IO.Path.Combine(dir, ".mcp.json")
    | VsCode -> IO.Path.Combine(dir, ".vscode", "mcp.json")
    | Cursor -> IO.Path.Combine(dir, ".cursor", "mcp.json")
    | ClaudeDesktop ->
      match platform with
      | Windows -> IO.Path.Combine(appData, "Claude", "claude_desktop_config.json")
      | MacOs -> IO.Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json")
      | Linux -> IO.Path.Combine(home, ".config", "Claude", "claude_desktop_config.json")
