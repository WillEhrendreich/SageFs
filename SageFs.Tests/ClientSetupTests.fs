module SageFs.Tests.ClientSetupTests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Nodes
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs

let private parse (text: string) : JsonNode = JsonNode.Parse text

let private str (node: JsonNode) : string = node.GetValue<string>()

/// The node at a path of keys, one lookup at a time.
let private at (keys: string list) (node: JsonNode) : JsonNode = keys |> List.fold (fun (n: JsonNode) key -> n[key]) node

/// A config file that already holds `names` as servers under `key`, and one more key beside them.
let private configHolding (key: string) (names: string list) : string =
  let servers = JsonObject()
  for name in names do
    servers[name] <- JsonObject([ KeyValuePair<string, JsonNode>("command", JsonValue.Create "other"); KeyValuePair<string, JsonNode>("args", JsonArray(JsonValue.Create "serve")) ])
  let root = JsonObject()
  root["theme"] <- JsonValue.Create "dark"
  root[key] <- servers
  root.ToJsonString()

let private genOtherNames : Gen<string list> =
  Gen.elements [ 'a' .. 'z' ]
  |> Gen.listOfLength 5
  |> Gen.map (fun letters -> "srv-" + System.String(List.toArray letters))
  |> Gen.listOf
  |> Gen.map List.distinct

[<Tests>]
let clientSetupTests =
  testList "ClientSetup" [

    testCase "WHY - the clients are a closed set, each with one token that parses back to it" <| fun _ ->
      Client.all |> List.map Client.toToken |> List.distinct |> List.length |> Expect.equal "tokens are distinct" (List.length Client.all)
      for client in Client.all do
        Client.parse (Client.toToken client) |> Expect.equal "round trip" (Ok client)
      Client.parse "emacs" |> Expect.isError "an unknown client is refused"

    testCase "WHY - every client's snippet is valid JSON, names the sagefs command and its mcp argument, and uses the key that client reads" <| fun _ ->
      for client in Client.all do
        let snippet = ClientSetup.snippet client
        let root = parse snippet.Text
        let entry = root[snippet.Key]["sagefs"]
        str (entry["command"]) |> Expect.equal (sprintf "%A command" client) "sagefs"
        entry["args"].AsArray() |> Seq.map str |> Seq.toList |> Expect.equal (sprintf "%A args" client) [ "mcp" ]
      (ClientSetup.snippet VsCode).Key |> Expect.equal "VS Code reads servers" "servers"
      let vsCodeRoot = parse (ClientSetup.snippet VsCode).Text
      str (at [ "servers"; "sagefs"; "type" ] vsCodeRoot) |> Expect.equal "VS Code names the transport" "stdio"
      for client in [ ClaudeCode; Cursor; ClaudeDesktop ] do
        (ClientSetup.snippet client).Key |> Expect.equal (sprintf "%A reads mcpServers" client) "mcpServers"

    testCase "WHY - every snippet names the client's own documentation page it was checked against, and the page prints it with the date" <| fun _ ->
      for client in Client.all do
        let snippet = ClientSetup.snippet client
        snippet.Source |> Expect.stringStarts (sprintf "%A source is a documentation URL" client) "https://"
        ClientSetup.page |> Expect.stringContains (sprintf "%A source is on the page" client) snippet.Source
      ClientSetup.page |> Expect.stringContains "the date is on the page" ClientSetup.checkedOn

    testCase "WHY - only Claude Code registers through its own command, and it registers for the user, not one project" <| fun _ ->
      ClientSetup.registrationCommand ClaudeCode
      |> Expect.equal "user scope, stdio bridge" (Some [ "claude"; "mcp"; "add"; "--scope"; "user"; "sagefs"; "--"; "sagefs"; "mcp" ])
      for client in [ VsCode; Cursor; ClaudeDesktop ] do
        ClientSetup.registrationCommand client |> Expect.isNone (sprintf "%A has no command" client)

    testProperty "WHY - merging into a config that already holds other servers keeps every one of them and every other key"
      (Prop.forAll (Arb.fromGen (Gen.zip (Gen.elements Client.all) genOtherNames)) (fun (client, others) ->
        let snippet = ClientSetup.snippet client
        match ClientSetup.merge (configHolding snippet.Key others) snippet with
        | Merged text ->
          let root = parse text
          let servers = root[snippet.Key].AsObject()
          (others |> List.forall (fun name -> servers.ContainsKey name && str (servers[name]["command"]) = "other"))
          && servers.ContainsKey "sagefs"
          && str (root["theme"]) = "dark"
          && servers.Count = List.length others + 1
        | _ -> false))

    testCase "WHY - an empty file or one with no servers key gets the whole snippet" <| fun _ ->
      for client in Client.all do
        let snippet = ClientSetup.snippet client
        for existing in [ ""; "   "; "{}" ] do
          match ClientSetup.merge existing snippet with
          | Merged text ->
            let root = parse text
            str (at [ snippet.Key; "sagefs"; "command" ] root) |> Expect.equal "added" "sagefs"
          | other -> failtestf "%A: expected Merged for %A, got %A" client existing other

    testCase "WHY - a config that is not JSON is refused and nothing is written" <| fun _ ->
      let snippet = ClientSetup.snippet VsCode
      for broken in [ "{ \"servers\": "; "// a comment\n{}"; "not json"; "[1,2]"; "{ \"servers\": 3 }" ] do
        match ClientSetup.merge broken snippet with
        | Refused reason -> ClientSetup.describeRefusal reason |> Expect.stringContains "says the next step" "→"
        | other -> failtestf "expected Refused for %s, got %A" broken other

    testCase "WHY - merging twice changes nothing" <| fun _ ->
      for client in Client.all do
        let snippet = ClientSetup.snippet client
        match ClientSetup.merge "{}" snippet with
        | Merged once -> ClientSetup.merge once snippet |> Expect.equal (sprintf "%A second merge" client) AlreadyPresent
        | other -> failtestf "expected Merged, got %A" other

    testCase "WHY - a sagefs entry that points somewhere else is refused and named, not overwritten" <| fun _ ->
      let snippet = ClientSetup.snippet Cursor
      let existing = """{ "mcpServers": { "sagefs": { "command": "/opt/old/sagefs", "args": ["mcp"] } } }"""
      match ClientSetup.merge existing snippet with
      | Refused(DifferentSageFsEntry found) ->
        found |> Expect.stringContains "names what is there" "/opt/old/sagefs"
      | other -> failtestf "expected DifferentSageFsEntry, got %A" other

    testCase "WHY - the setup page opens with the stdio registration and explains why an HTTP registration loses its tools" <| fun _ ->
      let page = ClientSetup.page
      page |> Expect.stringContains "leads with the stdio command" "claude mcp add --scope user sagefs -- sagefs mcp"
      let stdio = page.IndexOf "sagefs mcp"
      let http = page.IndexOf "--transport http"
      (stdio >= 0 && http > stdio) |> Expect.isTrue "stdio comes first, HTTP after it"
      page |> Expect.stringContains "names the session id cause" "Mcp-Session-Id"
      for client in Client.all do
        page |> Expect.stringContains (sprintf "%A has a snippet" client) (ClientSetup.snippet client).Text

    testCase "WHY - replacing the generated part needs both markers exactly once, and says so when they are not" <| fun _ ->
      let doc = String.concat "\n" [ "intro"; ClientSetup.generatedStart; "old"; ClientSetup.generatedEnd; "outro" ]
      ClientSetup.replaceGenerated doc "new" |> Expect.equal "swapped" (Ok(String.concat "\n" [ "intro"; ClientSetup.generatedStart; "new"; ClientSetup.generatedEnd; "outro" ]))
      ClientSetup.replaceGenerated "no markers" "new" |> Expect.equal "missing" (Error ClientSetup.MarkersMissingOrRepeated)
      ClientSetup.replaceGenerated (doc + "\n" + ClientSetup.generatedEnd) "new" |> Expect.equal "repeated" (Error ClientSetup.MarkersMissingOrRepeated)

    testCase "WHY - each client's config file is where that client reads it, for the directory and operating system" <| fun _ ->
      ClientSetup.configFile Linux "/h" "" "/p" VsCode |> Expect.equal "vscode" (Path.Combine("/p", ".vscode", "mcp.json"))
      ClientSetup.configFile Linux "/h" "" "/p" Cursor |> Expect.equal "cursor" (Path.Combine("/p", ".cursor", "mcp.json"))
      ClientSetup.configFile Linux "/h" "" "/p" ClaudeCode |> Expect.equal "claude code project file" (Path.Combine("/p", ".mcp.json"))
      ClientSetup.configFile Windows "/h" "/ad" "/p" ClaudeDesktop |> Expect.equal "windows" (Path.Combine("/ad", "Claude", "claude_desktop_config.json"))
      ClientSetup.configFile MacOs "/h" "" "/p" ClaudeDesktop |> Expect.equal "macos" (Path.Combine("/h", "Library", "Application Support", "Claude", "claude_desktop_config.json"))
      ClientSetup.configFile Linux "/h" "" "/p" ClaudeDesktop |> Expect.equal "linux" (Path.Combine("/h", ".config", "Claude", "claude_desktop_config.json"))
  ]

let private setupArgs (target: SetupCommand.Target) (where: SetupCommand.Where) (mode: SetupCommand.Mode) (skill: SetupCommand.SkillStep) : SetupCommand.Args =
  { Target = target; Where = where; Mode = mode; Skill = skill }

[<Tests>]
let setupCommandTests =
  let withTempDir (body: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-setup-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    try body dir
    finally (try Directory.Delete(dir, true) with _ -> ())

  testList "SetupCommand" [

    testCase "WHY - the arguments name a client, a directory and whether to write, and a mistake says what to type" <| fun _ ->
      SetupCommand.parseArgs [] |> Expect.equal "defaults" (Ok(setupArgs SetupCommand.FoundClients SetupCommand.CurrentDirectory SetupCommand.Preview SetupCommand.InstallSkill))
      SetupCommand.parseArgs [ "--client"; "vscode"; "--dir"; "/x"; "--write"; "--skip-skill" ]
      |> Expect.equal "all of them" (Ok(setupArgs (SetupCommand.OneClient VsCode) (SetupCommand.InDirectory "/x") SetupCommand.Write SetupCommand.LeaveSkill))
      SetupCommand.parseArgs [ "--client" ] |> Expect.equal "missing value" (Error(SetupCommand.MissingValue "--client"))
      SetupCommand.parseArgs [ "--client"; "emacs" ] |> Expect.equal "unknown client" (Error(SetupCommand.UnknownClientName(UnknownClient "emacs")))
      SetupCommand.parseArgs [ "--bogus" ] |> Expect.equal "unknown flag" (Error(SetupCommand.UnknownFlag "--bogus"))

    testCase "WHY - without --write nothing is written anywhere" <| fun _ ->
      withTempDir (fun dir ->
        SetupCommand.run (setupArgs (SetupCommand.OneClient VsCode) (SetupCommand.InDirectory dir) SetupCommand.Preview SetupCommand.LeaveSkill) |> Expect.equal "exit" 0
        Directory.GetFileSystemEntries dir |> Expect.isEmpty "the directory is untouched")

    testCase "WHY - setup --client vscode --write puts a registration in .vscode/mcp.json that keeps what was there, and a second run changes nothing" <| fun _ ->
      withTempDir (fun dir ->
        let file = Path.Combine(dir, ".vscode", "mcp.json")
        Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
        File.WriteAllText(file, """{ "servers": { "other": { "command": "x" } } }""")
        let args = setupArgs (SetupCommand.OneClient VsCode) (SetupCommand.InDirectory dir) SetupCommand.Write SetupCommand.LeaveSkill
        SetupCommand.run args |> Expect.equal "first run" 0
        let root = parse (File.ReadAllText file)
        let servers = root["servers"].AsObject()
        servers.ContainsKey "other" |> Expect.isTrue "the other server is still there"
        str (servers["sagefs"]["command"]) |> Expect.equal "sagefs is registered" "sagefs"
        let once = File.ReadAllBytes file
        SetupCommand.run args |> Expect.equal "second run" 0
        File.ReadAllBytes file |> Expect.equal "byte-identical" once)

    testCase "WHY - a config the merge refuses is left byte-identical and the command exits 1" <| fun _ ->
      withTempDir (fun dir ->
        let file = Path.Combine(dir, ".cursor", "mcp.json")
        Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
        File.WriteAllText(file, "// my servers\n{ \"mcpServers\": {} }")
        let before = File.ReadAllBytes file
        SetupCommand.run (setupArgs (SetupCommand.OneClient Cursor) (SetupCommand.InDirectory dir) SetupCommand.Write SetupCommand.LeaveSkill) |> Expect.equal "exit" 1
        File.ReadAllBytes file |> Expect.equal "untouched" before)
  ]
