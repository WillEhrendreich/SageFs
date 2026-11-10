namespace SageFs

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// `sagefs call`: the decisions behind calling one daemon tool from a shell, a script, a hook or CI.
///
/// A client with no SageFs tools of its own (the daemon was down when it started, or its MCP config is HTTP and the
/// daemon has since restarted, which ends the session that config holds) still has to reach them. This is that
/// route. Everything here is pure: how the command line is read, how an argument is typed from the tool's own input
/// schema, which words the daemon's answer becomes, which stream they go to and the exit code. The IO edge is
/// `SageFs/McpCallCommand.fs`, which speaks to the daemon through the stdio bridge's own client code.
///
/// Identity is a member token (`Capability.CapabilityTransport`), and nothing else. A token outranks the connection
/// it arrives on, so every call that carries the same token is the same member, and a session that member made
/// can be stopped by the next call. A call without one is anonymous, which the daemon sees as a new connection each
/// time, and the output says so.
module McpCall =

  // ---------------------------------------------------------------- what a tool takes

  /// The kinds of value a tool's input schema names. A word the schema uses that is not one of these is
  /// `Unspecified`, and its value stays the string it was typed as.
  [<RequireQualifiedAccess>]
  type ParamKind =
    | Words
    | WholeNumber
    | Fraction
    | Switch
    | Items
    | Record
    | Unspecified

  module ParamKind =
    let ofSchemaType (word: string) : ParamKind =
      match word with
      | "string" -> ParamKind.Words
      | "integer" -> ParamKind.WholeNumber
      | "number" -> ParamKind.Fraction
      | "boolean" -> ParamKind.Switch
      | "array" -> ParamKind.Items
      | "object" -> ParamKind.Record
      | _ -> ParamKind.Unspecified

    let label (kind: ParamKind) : string =
      match kind with
      | ParamKind.Words -> "string"
      | ParamKind.WholeNumber -> "integer"
      | ParamKind.Fraction -> "number"
      | ParamKind.Switch -> "boolean"
      | ParamKind.Items -> "array"
      | ParamKind.Record -> "object"
      | ParamKind.Unspecified -> "any"

  /// Whether a call has to give a parameter: the schema's `required` list.
  [<RequireQualifiedAccess>]
  type ParamNeed =
    | Needed
    | Optional

  type ParamSpec =
    { Name: string
      Kind: ParamKind
      Need: ParamNeed }

  type ToolSpec =
    { Name: string
      Description: string
      Params: ParamSpec list }

  module ToolSpec =

    /// How a parameter is shown when a refusal lists what the tool takes.
    let describeParam (spec: ParamSpec) : string =
      match spec.Need with
      | ParamNeed.Needed -> sprintf "%s (%s, required)" spec.Name (ParamKind.label spec.Kind)
      | ParamNeed.Optional -> sprintf "%s (%s)" spec.Name (ParamKind.label spec.Kind)

    let describeParams (tool: ToolSpec) : string =
      match tool.Params with
      | [] -> sprintf "%s takes no parameters." tool.Name
      | specs -> sprintf "%s takes: %s." tool.Name (specs |> List.map describeParam |> String.concat ", ")

    /// What `sagefs call <tool> --help` prints: the tool's own sentence, how to call it, one line per parameter (the
    /// same words a refusal uses, `describeParam`) and where its full manual is.
    let helpText (tool: ToolSpec) : string =
      let description =
        match tool.Description.Trim() with
        | "" -> "(the daemon sends no description for this tool)"
        | text -> text
      let parameters =
        match tool.Params with
        | [] -> [ sprintf "  %s takes no parameters." tool.Name ]
        | specs -> specs |> List.map (fun spec -> "  " + describeParam spec)
      String.concat
        "\n"
        ([ sprintf "%s: %s" tool.Name description
           ""
           sprintf "usage: sagefs call %s [--<parameter> <value> ...]" tool.Name
           sprintf "       sagefs call %s --json '{\"parameter\": value}'" tool.Name
           ""
           "parameters:" ]
         @ parameters
         @ [ ""
             sprintf
               "Full manual: sagefs call discover_features --tool %s (its rules and every parameter), or docs/mcp-tools.md."
               tool.Name ])

    /// The number of tool names a refusal offers.
    let nearestCount : int = 3

    /// The fewest single-character edits that turn one word into the other.
    let editDistance (a: string) (b: string) : int =
      let last =
        a
        |> Seq.fold
          (fun (previous: int[]) letter ->
            let next = Array.zeroCreate (b.Length + 1)
            next.[0] <- previous.[0] + 1
            for column in 1 .. b.Length do
              let cost =
                match letter = b.[column - 1] with
                | true -> 0
                | false -> 1
              next.[column] <- min (min (previous.[column] + 1) (next.[column - 1] + 1)) (previous.[column - 1] + cost)
            next)
          [| 0 .. b.Length |]
      last.[b.Length]

    /// Up to `nearestCount` of the known names that read like the one asked for: it sits inside them (or they inside it),
    /// or it is a few edits away. A name nothing is close to gets nothing, since a guess would only mislead.
    let nearest (asked: string) (known: string list) : string list =
      let wanted = asked.Trim().ToLowerInvariant()
      let radius = max 3 (wanted.Length / 3)
      known
      |> List.map (fun name ->
        let lower = name.ToLowerInvariant()
        let nested = wanted <> "" && lower <> "" && (lower.Contains wanted || wanted.Contains lower)
        name,
        match nested with
        | true -> 0
        | false -> editDistance wanted lower)
      |> List.filter (fun (_, distance) -> distance <= radius)
      |> List.sortBy (fun (name, distance) -> distance, name)
      |> List.truncate nearestCount
      |> List.map fst

    /// The first line of the description, cut at `width`: the line `--list` shows beside the name.
    let oneLine (width: int) (tool: ToolSpec) : string =
      let first =
        tool.Description.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryHead
        |> Option.map (fun line -> line.Trim())
        |> Option.defaultValue ""
      match first.Length > width with
      | true -> first.Substring(0, width).TrimEnd() + "..."
      | false -> first

  /// Text parsed as JSON, or nothing when it is not. The one place in this module a parse can throw.
  let tryParseJson (text: string) : JsonDocument option =
    try
      Some(JsonDocument.Parse text)
    with :? JsonException ->
      None

  let stringProperty (name: string) (element: JsonElement) : string option =
    match element.ValueKind with
    | JsonValueKind.Object ->
      match element.TryGetProperty name with
      | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
      | _ -> None
    | _ -> None

  /// What a schema property's `type` says. A list of words (`["integer","null"]`, how an optional value is written)
  /// means its first word that is not `null`.
  let kindOfProperty (property: JsonElement) : ParamKind =
    match property.ValueKind with
    | JsonValueKind.Object ->
      match property.TryGetProperty "type" with
      | true, typeElement ->
        match typeElement.ValueKind with
        | JsonValueKind.Array ->
          typeElement.EnumerateArray()
          |> Seq.choose (fun word ->
            match word.ValueKind with
            | JsonValueKind.String -> Some(word.GetString())
            | _ -> None)
          |> Seq.tryFind (fun word -> word <> "null")
          |> Option.map ParamKind.ofSchemaType
          |> Option.defaultValue ParamKind.Unspecified
        | JsonValueKind.String -> ParamKind.ofSchemaType (typeElement.GetString())
        | _ -> ParamKind.Unspecified
      | _ -> ParamKind.Unspecified
    | _ -> ParamKind.Unspecified

  let requiredNames (schema: JsonElement) : string list =
    match schema.ValueKind with
    | JsonValueKind.Object ->
      match schema.TryGetProperty "required" with
      | true, names when names.ValueKind = JsonValueKind.Array ->
        names.EnumerateArray()
        |> Seq.choose (fun name ->
          match name.ValueKind with
          | JsonValueKind.String -> Some(name.GetString())
          | _ -> None)
        |> List.ofSeq
      | _ -> []
    | _ -> []

  let paramsOfSchema (schema: JsonElement) : ParamSpec list =
    let required = requiredNames schema
    match schema.ValueKind with
    | JsonValueKind.Object ->
      match schema.TryGetProperty "properties" with
      | true, properties when properties.ValueKind = JsonValueKind.Object ->
        properties.EnumerateObject()
        |> Seq.map (fun property ->
          { Name = property.Name
            Kind = kindOfProperty property.Value
            Need =
              match List.contains property.Name required with
              | true -> ParamNeed.Needed
              | false -> ParamNeed.Optional })
        |> List.ofSeq
      | _ -> []
    | _ -> []

  let toolOfElement (tool: JsonElement) : ToolSpec option =
    stringProperty "name" tool
    |> Option.map (fun name ->
      let schema =
        match tool.TryGetProperty "inputSchema" with
        | true, found -> found
        | false, _ -> tool
      { Name = name
        Description = stringProperty "description" tool |> Option.defaultValue ""
        Params = paramsOfSchema schema })

  /// Why a `tools/list` answer could not be read.
  [<RequireQualifiedAccess>]
  type ToolListFault =
    | NotJson
    | NotAnObject
    | NoToolsArray

  module ToolListFault =
    let describe (fault: ToolListFault) : string =
      match fault with
      | ToolListFault.NotJson -> "the tools/list answer is not JSON"
      | ToolListFault.NotAnObject -> "the tools/list answer is not an object"
      | ToolListFault.NoToolsArray -> "the tools/list answer has no tools array"

  /// The tools in a `tools/list` result (the `result` object's text, `{"tools":[...]}`).
  let parseTools (resultJson: string) : Result<ToolSpec list, ToolListFault> =
    match tryParseJson resultJson with
    | None -> Result.Error ToolListFault.NotJson
    | Some document ->
      use document = document
      match document.RootElement.ValueKind with
      | JsonValueKind.Object ->
        match document.RootElement.TryGetProperty "tools" with
        | true, tools when tools.ValueKind = JsonValueKind.Array ->
          tools.EnumerateArray() |> Seq.choose toolOfElement |> List.ofSeq |> Result.Ok
        | _ -> Result.Error ToolListFault.NoToolsArray
      | _ -> Result.Error ToolListFault.NotAnObject

  // ---------------------------------------------------------------- the command line

  /// What the command line asked for beyond the tool's own arguments.
  [<RequireQualifiedAccess>]
  type UsageRefusal =
    | NothingToDo
    | ListAndTool of tool: string
    | ListTakesNoArguments
    | OptionNeedsValue of option: string
    | OptionTakesNoValue of option: string
    | NotAPort of given: string
    | BlankToken
    /// `--token-file` named a file or stdin that held nothing.
    | BlankTokenFile of source: string
    /// `--token-file` named a file that could not be read.
    | TokenFileUnreadable of path: string * reason: string
    /// More than one of `--token` and `--token-file` was given, so which member to call as is not clear.
    | TwoTokenSources
    | FlagBeforeTool of flag: string
    | ExtraWord of word: string
    | DuplicateFlag of flag: string
    | JsonAndFlags

  let usage : string =
    String.concat
      "\n"
      [ "usage: sagefs call --list"
        "       sagefs call <tool> [--<parameter> <value> ...]"
        "       sagefs call <tool> --json '{\"parameter\": value}'"
        "       sagefs call <tool> --help"
        ""
        "  --list           list the daemon's tools: name and first line of the description"
        "  --help           with a tool's name, print what that tool does and takes (needs the daemon); alone, this text"
        "  --json <object>  the tool's arguments as one JSON object (instead of --<parameter> flags)"
        "  --token-file <path>  read the member token to call as from a file ('-' reads standard input)"
        "                   the default is SAGEFS_MEMBER_TOKEN, and with none the call is anonymous"
        "  --token <token>  the same, but the token is typed on the command line, where the process list and the shell"
        "                   history keep it. Deprecated: use SAGEFS_MEMBER_TOKEN or --token-file"
        "  --port <port>    the daemon's MCP port (default: --mcp-port, SAGEFS_MCP_PORT, then 37749)"
        ""
        "A flag takes its value from the next word or after '=' (--code=--help for a value that starts with '--')."
        "A value is typed from the tool's own input schema. A boolean flag alone means true."
        "Output is the tool's answer on stdout. A tool that answers with an error goes to stderr, exit code 1." ]

  module UsageRefusal =
    let describe (refusal: UsageRefusal) : string =
      match refusal with
      | UsageRefusal.NothingToDo -> "name a tool to call, or pass --list to see them."
      | UsageRefusal.ListAndTool tool -> sprintf "--list takes no tool, and '%s' was given with it." tool
      | UsageRefusal.ListTakesNoArguments -> "--list takes no arguments."
      | UsageRefusal.OptionNeedsValue option -> sprintf "%s needs a value after it." option
      | UsageRefusal.OptionTakesNoValue option -> sprintf "%s takes no value." option
      | UsageRefusal.NotAPort given -> sprintf "--port wants a port number from 1 to 65535, and '%s' is not one." given
      | UsageRefusal.BlankToken -> "--token is blank. Leave it out to call anonymously."
      | UsageRefusal.BlankTokenFile source ->
        sprintf "%s held no token. Leave --token-file out to call anonymously, or put a member token in it." source
      | UsageRefusal.TokenFileUnreadable(path, reason) -> sprintf "--token-file could not read '%s': %s." path reason
      | UsageRefusal.TwoTokenSources -> "give --token or --token-file, not both."
      | UsageRefusal.FlagBeforeTool flag ->
        sprintf "%s comes before the tool's name. A tool's arguments follow its name: sagefs call <tool> %s <value>." flag flag
      | UsageRefusal.ExtraWord word ->
        sprintf "'%s' is not a flag. A tool's arguments are flags (--<parameter> <value>) after its name." word
      | UsageRefusal.DuplicateFlag flag -> sprintf "%s was given twice." flag
      | UsageRefusal.JsonAndFlags -> "give the arguments as --json or as flags, not both."

  /// A `--name value` the tool will type against its schema. `Value` is `None` for a flag with nothing after it.
  type RawFlag = { Name: string; Value: string option }

  [<RequireQualifiedAccess>]
  type Payload =
    | NoArguments
    | Flags of RawFlag list
    | JsonObject of string

  [<RequireQualifiedAccess>]
  type Target =
    | ListTools
    | CallTool of tool: string
    /// `<tool> --help`: say what the tool does and takes, from the list the daemon sends. Nothing is called.
    | DescribeTool of tool: string

  /// Which daemon: the one a port flag named, or the configured one (`--mcp-port`, `SAGEFS_MCP_PORT`, then 37749).
  [<RequireQualifiedAccess>]
  type PortChoice =
    | Configured
    | Named of port: int

  /// Which member to call as: the token a file or standard input holds, the one `--token` typed (deprecated, because a
  /// token on the command line sits in the process list and the shell history), or whatever the environment holds
  /// (nothing is anonymous).
  [<RequireQualifiedAccess>]
  type TokenChoice =
    | FromEnvironment
    | Given of token: string
    | FromFile of path: string
    | FromStdin

  type CallArgs =
    { Target: Target
      Port: PortChoice
      Token: TokenChoice
      Payload: Payload }

  [<RequireQualifiedAccess>]
  type Invocation =
    | Help
    | Run of CallArgs

  /// The options that belong to the command itself and are read wherever they stand. A tool parameter with one of
  /// these names would be unreachable by flag (`--json` still reaches it), so no tool has one: a test reads the
  /// registered tools against this list.
  [<RequireQualifiedAccess>]
  type CommandOption =
    | Help
    | List
    | Json
    | Token
    | TokenFile
    | Port
    | McpPort

  module CommandOption =
    let all : CommandOption list =
      [ CommandOption.Help
        CommandOption.List
        CommandOption.Json
        CommandOption.Token
        CommandOption.TokenFile
        CommandOption.Port
        CommandOption.McpPort ]

    let name (option: CommandOption) : string =
      match option with
      | CommandOption.Help -> "help"
      | CommandOption.List -> "list"
      | CommandOption.Json -> "json"
      | CommandOption.Token -> "token"
      | CommandOption.TokenFile -> "token-file"
      | CommandOption.Port -> "port"
      | CommandOption.McpPort -> "mcp-port"

    let flag (option: CommandOption) : string = "--" + name option

    let ofName (word: string) : CommandOption option = all |> List.tryFind (fun option -> name option = word)

  /// What the command line asked of the command itself.
  [<RequireQualifiedAccess>]
  type Want =
    | Help
    | List

  module CallArgs =

    /// What has been read so far, each kind in a list with the latest first: a list that is empty says it was not given,
    /// and a repeated option is read as its last word.
    type Gathered =
      { Wants: Want list
        Tools: string list
        Jsons: string list
        Tokens: string list
        TokenFiles: string list
        Ports: int list
        Flags: RawFlag list }

    let nothingGathered : Gathered =
      { Wants = []
        Tools = []
        Jsons = []
        Tokens = []
        TokenFiles = []
        Ports = []
        Flags = [] }

    /// What `--token-file` is given to read standard input.
    [<Literal>]
    let StdinPath = "-"

    let isFlag (word: string) : bool = word.StartsWith("--", StringComparison.Ordinal)

    /// `--name=value` is the name and the value; `--name` is the name alone.
    let splitFlag (word: string) : string * string option =
      let body = word.Substring 2
      match body.IndexOf '=' with
      | -1 -> body, None
      | at -> body.Substring(0, at), Some(body.Substring(at + 1))

    /// The value of a flag: after the `=`, or the next word when it is not itself a flag.
    let valueOf (inlined: string option) (rest: string list) : string option * string list =
      match inlined, rest with
      | Some value, _ -> Some value, rest
      | None, next :: more when not (isFlag next) -> Some next, more
      | None, _ -> None, rest

    let portOf (text: string) : Result<int, UsageRefusal> =
      match Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
      | true, port when port >= 1 && port <= 65535 -> Result.Ok port
      | _ -> Result.Error(UsageRefusal.NotAPort text)

    /// A value the command's own option took.
    let withValue (gathered: Gathered) (option: CommandOption) (value: string) : Result<Gathered, UsageRefusal> =
      match option with
      | CommandOption.Json -> Result.Ok { gathered with Jsons = value :: gathered.Jsons }
      | CommandOption.Token -> Result.Ok { gathered with Tokens = value :: gathered.Tokens }
      | CommandOption.TokenFile -> Result.Ok { gathered with TokenFiles = value :: gathered.TokenFiles }
      | CommandOption.Port
      | CommandOption.McpPort -> portOf value |> Result.map (fun port -> { gathered with Ports = port :: gathered.Ports })
      | CommandOption.Help
      | CommandOption.List -> Result.Ok gathered

    let want (gathered: Gathered) (wanted: Want) : Gathered = { gathered with Wants = wanted :: gathered.Wants }

    let rec read (gathered: Gathered) (words: string list) : Result<Gathered, UsageRefusal> =
      match words with
      | [] -> Result.Ok gathered
      | "-h" :: rest -> read (want gathered Want.Help) rest
      | word :: rest when not (isFlag word) ->
        match gathered.Tools with
        | [] -> read { gathered with Tools = [ word ] } rest
        | _ :: _ -> Result.Error(UsageRefusal.ExtraWord word)
      | word :: rest ->
        let name, inlined = splitFlag word
        match CommandOption.ofName name with
        | Some CommandOption.Help -> read (want gathered Want.Help) rest
        | Some CommandOption.List ->
          match inlined with
          | Some _ -> Result.Error(UsageRefusal.OptionTakesNoValue(CommandOption.flag CommandOption.List))
          | None -> read (want gathered Want.List) rest
        | Some option ->
          match valueOf inlined rest with
          | None, _ -> Result.Error(UsageRefusal.OptionNeedsValue(CommandOption.flag option))
          | Some value, more -> withValue gathered option value |> Result.bind (fun next -> read next more)
        | None ->
          match gathered.Tools with
          | [] -> Result.Error(UsageRefusal.FlagBeforeTool word)
          | _ :: _ ->
            match gathered.Flags |> List.exists (fun flag -> flag.Name = name) with
            | true -> Result.Error(UsageRefusal.DuplicateFlag("--" + name))
            | false ->
              let value, more = valueOf inlined rest
              read { gathered with Flags = { Name = name; Value = value } :: gathered.Flags } more

    let invocationOf (gathered: Gathered) : Result<Invocation, UsageRefusal> =
      let port =
        match gathered.Ports with
        | [] -> PortChoice.Configured
        | last :: _ -> PortChoice.Named last
      let token =
        match gathered.Tokens, gathered.TokenFiles with
        | [], [] -> Result.Ok TokenChoice.FromEnvironment
        | last :: _, [] -> Result.Ok(TokenChoice.Given last)
        | [], StdinPath :: _ -> Result.Ok TokenChoice.FromStdin
        | [], path :: _ -> Result.Ok(TokenChoice.FromFile path)
        | _ :: _, _ :: _ -> Result.Error UsageRefusal.TwoTokenSources
      let run (target: Target) (payload: Payload) : Result<Invocation, UsageRefusal> =
        token
        |> Result.map (fun token -> Invocation.Run { Target = target; Port = port; Token = token; Payload = payload })
      let listing = List.contains Want.List gathered.Wants
      match List.contains Want.Help gathered.Wants, listing, gathered.Tools, gathered.Jsons, List.rev gathered.Flags with
      | true, true, _, _, _
      | true, false, [], _, _ -> Result.Ok Invocation.Help
      | true, false, tool :: _, _, _ -> run (Target.DescribeTool tool) Payload.NoArguments
      | false, true, tool :: _, _, _ -> Result.Error(UsageRefusal.ListAndTool tool)
      | false, true, [], _ :: _, _ -> Result.Error UsageRefusal.ListTakesNoArguments
      | false, true, [], [], _ -> run Target.ListTools Payload.NoArguments
      | false, false, [], _, _ -> Result.Error UsageRefusal.NothingToDo
      | false, false, _ :: _, _ :: _, _ :: _ -> Result.Error UsageRefusal.JsonAndFlags
      | false, false, tool :: _, json :: _, [] -> run (Target.CallTool tool) (Payload.JsonObject json)
      | false, false, tool :: _, [], [] -> run (Target.CallTool tool) Payload.NoArguments
      | false, false, tool :: _, [], flags -> run (Target.CallTool tool) (Payload.Flags flags)

    /// The words after `sagefs call`. Total: a mistake is a named refusal.
    let parse (words: string list) : Result<Invocation, UsageRefusal> =
      read nothingGathered words |> Result.bind invocationOf

  // ---------------------------------------------------------------- who is calling

  [<RequireQualifiedAccess>]
  type TokenSource =
    /// Typed after `--token`, so it is in the process list and the shell history.
    | Flag
    | Environment
    /// Read from the file `--token-file` named.
    | File of path: string
    /// Read from standard input (`--token-file -`).
    | Stdin

  /// What reading a token file came to: its text, or why it could not be read.
  [<RequireQualifiedAccess>]
  type TokenFileRead =
    | Text of string
    | Unreadable of reason: string

  /// How a token that is not typed on the command line is read, passed in so the choice is decided without touching a
  /// file or a terminal. The caller reads the file or standard input once and says why when it cannot.
  type TokenReads =
    { ReadFile: string -> TokenFileRead
      ReadStdin: unit -> string }

  module TokenReads =
    /// For a caller whose choice names neither: reading one is a fault it reports instead of guessing.
    let nothing : TokenReads =
      { ReadFile = fun _ -> TokenFileRead.Unreadable "this command was not given a way to read files"
        ReadStdin = fun () -> "" }

  [<RequireQualifiedAccess>]
  type Identity =
    | Anonymous
    | Member of TokenSource

  /// The headers a call carries, and what they make the caller. The token itself is in `Headers` and nowhere else:
  /// no description, refusal or listing built from this prints it.
  type Credentials =
    { Headers: (string * string) list
      Identity: Identity }

  module Credentials =

    /// No token and no operator credential: what a run that failed before it read any is described as.
    let anonymous : Credentials = { Headers = []; Identity = Identity.Anonymous }

    /// Whether the operator credential rides along (`SAGEFS_OPERATOR_TOKEN`, sent as `Authorization`).
    let hasOperator (credentials: Credentials) : bool =
      credentials.Headers |> List.exists (fun (name, _) -> name = Capability.CapabilityTransport.authorizationHeaderName)

    /// A token given with `--token` or `--token-file` wins over the environment. A blank one is refused: it was named on
    /// purpose and means nothing, and reading it as "anonymous" would hide the typo behind a call that works.
    let resolve (reads: TokenReads) (choice: TokenChoice) (getEnv: string -> string) : Result<Credentials, UsageRefusal> =
      let operatorHeader = Capability.CapabilityTransport.operatorHeaderFromEnvironment getEnv
      let withOperator (memberHeader: (string * string) option) (identity: Identity) : Credentials =
        { Headers = List.choose id [ memberHeader; operatorHeader ]
          Identity = identity }
      let memberFrom (source: TokenSource) (token: string) : Credentials =
        withOperator (Some(Capability.CapabilityTransport.headerName, token.Trim())) (Identity.Member source)
      match choice with
      | TokenChoice.Given given when String.IsNullOrWhiteSpace given -> Result.Error UsageRefusal.BlankToken
      | TokenChoice.Given given -> Result.Ok(memberFrom TokenSource.Flag given)
      | TokenChoice.FromFile path ->
        match reads.ReadFile path with
        | TokenFileRead.Unreadable reason -> Result.Error(UsageRefusal.TokenFileUnreadable(path, reason))
        | TokenFileRead.Text text when String.IsNullOrWhiteSpace text -> Result.Error(UsageRefusal.BlankTokenFile path)
        | TokenFileRead.Text text -> Result.Ok(memberFrom (TokenSource.File path) text)
      | TokenChoice.FromStdin ->
        match reads.ReadStdin() with
        | text when String.IsNullOrWhiteSpace text -> Result.Error(UsageRefusal.BlankTokenFile "standard input")
        | text -> Result.Ok(memberFrom TokenSource.Stdin text)
      | TokenChoice.FromEnvironment ->
        match Capability.CapabilityTransport.headerFromEnvironment getEnv with
        | Some header -> Result.Ok(withOperator (Some header) (Identity.Member TokenSource.Environment))
        | None -> Result.Ok(withOperator None Identity.Anonymous)

    /// What the command says on stderr before it calls, when there is something to say: a token typed with `--token` is in the
    /// process list and in the shell history for as long as either is kept. The token itself is never in the line.
    let warning (credentials: Credentials) : string option =
      match credentials.Identity with
      | Identity.Member TokenSource.Flag ->
        Some(
          sprintf
            "warning: --token puts the member token on the command line, where the process list and the shell history keep it. Set %s, or pass --token-file <path> (or --token-file - for standard input), instead."
            Capability.CapabilityTransport.bridgeEnvVar
        )
      | Identity.Member TokenSource.Environment
      | Identity.Member(TokenSource.File _)
      | Identity.Member TokenSource.Stdin
      | Identity.Anonymous -> None

    let describe (credentials: Credentials) : string =
      let who =
        match credentials.Identity with
        | Identity.Member TokenSource.Flag -> "a member token from --token"
        | Identity.Member(TokenSource.File path) -> sprintf "a member token from the file %s" path
        | Identity.Member TokenSource.Stdin -> "a member token from standard input"
        | Identity.Member TokenSource.Environment ->
          sprintf "a member token from %s" Capability.CapabilityTransport.bridgeEnvVar
        | Identity.Anonymous ->
          sprintf
            "anonymous: no --token and %s is not set, so the daemon sees a new connection on every call and cannot tie one call to the next"
            Capability.CapabilityTransport.bridgeEnvVar
      match hasOperator credentials with
      | true -> sprintf "%s, with the operator credential from %s" who Capability.CapabilityTransport.operatorEnvVar
      | false -> who

  // ---------------------------------------------------------------- typing an argument

  /// A value typed from the schema. `Raw` is JSON text for an array or an object.
  [<RequireQualifiedAccess>]
  type ArgValue =
    | Text of string
    | Whole of int64
    | Real of float
    | Flag of bool
    | Raw of string

  module ArgValue =
    /// What a person types for this value, so the round trip text -> value -> JSON can be property tested.
    let render (value: ArgValue) : string =
      match value with
      | ArgValue.Text text -> text
      | ArgValue.Whole whole -> whole.ToString(CultureInfo.InvariantCulture)
      | ArgValue.Real real -> real.ToString("R", CultureInfo.InvariantCulture)
      | ArgValue.Flag flag -> (match flag with true -> "true" | false -> "false")
      | ArgValue.Raw json -> json

  [<RequireQualifiedAccess>]
  type ArgRefusal =
    | UnknownTool of name: string * known: string list
    | UnknownParameter of flag: string * tool: ToolSpec
    | FlagNeedsValue of flag: string * kind: ParamKind
    | NotAWholeNumber of flag: string * given: string
    | NotANumber of flag: string * given: string
    | NotABoolean of flag: string * given: string
    | NotJsonOfKind of flag: string * kind: ParamKind
    | JsonArgumentsNotAnObject
    | MissingRequired of tool: ToolSpec * missing: string list

  module ArgRefusal =
    let describe (refusal: ArgRefusal) : string =
      match refusal with
      | ArgRefusal.UnknownTool(name, known) ->
        sprintf "the daemon lists no tool named '%s' to this caller (it lists tools by who is asking). Tools: %s." name (String.concat ", " known)
      | ArgRefusal.UnknownParameter(flag, tool) ->
        sprintf "'%s' is not a parameter of %s. %s" flag tool.Name (ToolSpec.describeParams tool)
      | ArgRefusal.FlagNeedsValue(flag, kind) -> sprintf "--%s is %s and needs a value after it." flag (ParamKind.label kind)
      | ArgRefusal.NotAWholeNumber(flag, given) -> sprintf "--%s wants a whole number, and '%s' is not one." flag given
      | ArgRefusal.NotANumber(flag, given) -> sprintf "--%s wants a number, and '%s' is not one." flag given
      | ArgRefusal.NotABoolean(flag, given) -> sprintf "--%s wants true or false, and '%s' is neither." flag given
      | ArgRefusal.NotJsonOfKind(flag, kind) -> sprintf "--%s wants a JSON %s." flag (ParamKind.label kind)
      | ArgRefusal.JsonArgumentsNotAnObject -> "--json wants one JSON object of arguments."
      | ArgRefusal.MissingRequired(tool, missing) ->
        sprintf "%s needs %s. %s" tool.Name (missing |> List.map (fun name -> "--" + name) |> String.concat ", ") (ToolSpec.describeParams tool)

  module Arguments =

    /// The JSON shape a kind is written as. A kind that is not written as JSON has none (`Undefined` is never a parsed shape).
    let shapeOf (kind: ParamKind) : JsonValueKind =
      match kind with
      | ParamKind.Items -> JsonValueKind.Array
      | ParamKind.Record -> JsonValueKind.Object
      | ParamKind.Words
      | ParamKind.WholeNumber
      | ParamKind.Fraction
      | ParamKind.Switch
      | ParamKind.Unspecified -> JsonValueKind.Undefined

    let jsonOfKind (kind: ParamKind) (given: string) : string option =
      match tryParseJson given with
      | None -> None
      | Some document ->
        use document = document
        match document.RootElement.ValueKind = shapeOf kind with
        | true -> Some(document.RootElement.GetRawText())
        | false -> None

    /// One flag typed against the parameter it names.
    let typeFlag (spec: ParamSpec) (flag: RawFlag) : Result<ArgValue, ArgRefusal> =
      match spec.Kind, flag.Value with
      | ParamKind.Switch, None -> Result.Ok(ArgValue.Flag true)
      | kind, None -> Result.Error(ArgRefusal.FlagNeedsValue(flag.Name, kind))
      | ParamKind.Words, Some given
      | ParamKind.Unspecified, Some given -> Result.Ok(ArgValue.Text given)
      | ParamKind.WholeNumber, Some given ->
        match Int64.TryParse(given, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, whole -> Result.Ok(ArgValue.Whole whole)
        | false, _ -> Result.Error(ArgRefusal.NotAWholeNumber(flag.Name, given))
      | ParamKind.Fraction, Some given ->
        match Double.TryParse(given, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, real when Double.IsFinite real -> Result.Ok(ArgValue.Real real)
        | _ -> Result.Error(ArgRefusal.NotANumber(flag.Name, given))
      | ParamKind.Switch, Some given ->
        match given.Trim().ToLowerInvariant() with
        | "true" -> Result.Ok(ArgValue.Flag true)
        | "false" -> Result.Ok(ArgValue.Flag false)
        | _ -> Result.Error(ArgRefusal.NotABoolean(flag.Name, given))
      | (ParamKind.Items | ParamKind.Record), Some given ->
        match jsonOfKind spec.Kind given with
        | Some json -> Result.Ok(ArgValue.Raw json)
        | None -> Result.Error(ArgRefusal.NotJsonOfKind(flag.Name, spec.Kind))

    let checkRequired (tool: ToolSpec) (given: string list) : Result<unit, ArgRefusal> =
      match tool.Params |> List.filter (fun spec -> spec.Need = ParamNeed.Needed && not (List.contains spec.Name given)) with
      | [] -> Result.Ok()
      | missing -> Result.Error(ArgRefusal.MissingRequired(tool, missing |> List.map (fun spec -> spec.Name)))

    /// Every flag typed, in the order given. A name the tool does not have is refused with the names it does.
    let ofFlags (tool: ToolSpec) (flags: RawFlag list) : Result<(string * ArgValue) list, ArgRefusal> =
      let typed =
        flags
        |> List.fold
          (fun typedSoFar flag ->
            typedSoFar
            |> Result.bind (fun values ->
              match tool.Params |> List.tryFind (fun spec -> spec.Name = flag.Name) with
              | None -> Result.Error(ArgRefusal.UnknownParameter("--" + flag.Name, tool))
              | Some spec -> typeFlag spec flag |> Result.map (fun value -> values @ [ spec.Name, value ])))
          (Result.Ok [])
      typed
      |> Result.bind (fun values -> checkRequired tool (values |> List.map fst) |> Result.map (fun () -> values))

    let writeValue (writer: Utf8JsonWriter) (value: ArgValue) : unit =
      match value with
      | ArgValue.Text text -> writer.WriteStringValue text
      | ArgValue.Whole whole -> writer.WriteNumberValue whole
      | ArgValue.Real real -> writer.WriteNumberValue real
      | ArgValue.Flag flag -> writer.WriteBooleanValue flag
      | ArgValue.Raw json -> writer.WriteRawValue json

    let writerOptions : JsonWriterOptions =
      JsonWriterOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// The arguments as the one JSON object `tools/call` carries.
    let toJson (values: (string * ArgValue) list) : string =
      use stream = new MemoryStream()
      use writer = new Utf8JsonWriter(stream, writerOptions)
      writer.WriteStartObject()
      for name, value in values do
        writer.WritePropertyName name
        writeValue writer value
      writer.WriteEndObject()
      writer.Flush()
      Encoding.UTF8.GetString(stream.ToArray())

    /// `--json`: one object whose names are all the tool's. Its values are the author's own and pass as written.
    let ofJson (tool: ToolSpec) (json: string) : Result<string, ArgRefusal> =
      match tryParseJson json with
      | None -> Result.Error ArgRefusal.JsonArgumentsNotAnObject
      | Some document ->
        use document = document
        match document.RootElement.ValueKind with
        | JsonValueKind.Object ->
          let names = document.RootElement.EnumerateObject() |> Seq.map (fun property -> property.Name) |> List.ofSeq
          match names |> List.tryFind (fun name -> not (tool.Params |> List.exists (fun spec -> spec.Name = name))) with
          | Some unknown -> Result.Error(ArgRefusal.UnknownParameter(unknown, tool))
          | None ->
            checkRequired tool names
            |> Result.map (fun () -> document.RootElement.GetRawText())
        | _ -> Result.Error ArgRefusal.JsonArgumentsNotAnObject

    /// The `arguments` object for a call: typed from the schema, or checked against it. Nothing to send is `{}`.
    let ofPayload (tool: ToolSpec) (payload: Payload) : Result<string, ArgRefusal> =
      match payload with
      | Payload.NoArguments -> checkRequired tool [] |> Result.map (fun () -> "{}")
      | Payload.Flags flags -> ofFlags tool flags |> Result.map toJson
      | Payload.JsonObject json -> ofJson tool json

    let findTool (tools: ToolSpec list) (name: string) : Result<ToolSpec, ArgRefusal> =
      match tools |> List.tryFind (fun tool -> tool.Name = name) with
      | Some tool -> Result.Ok tool
      | None -> Result.Error(ArgRefusal.UnknownTool(name, tools |> List.map (fun tool -> tool.Name)))

  // ---------------------------------------------------------------- the wire

  /// What the daemon's JSON-RPC answer was.
  type RpcFailure = { Code: int; Message: string }

  [<RequireQualifiedAccess>]
  type Answer =
    | Succeeded of resultJson: string
    | Failed of RpcFailure
    | Unanswered

  module Rpc =

    /// The ids `sagefs call` gives its three requests: one conversation, one request of each kind.
    let initializeId = 1
    let toolsListId = 2
    let toolsCallId = 3

    /// What `sagefs call` declares in `initialize`: nothing. It is also what the bridge's heal re-declares.
    let initializeParams : string =
      """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"sagefs-call","version":"1"}}"""

    let initializeRequest (id: int) : string =
      sprintf """{"jsonrpc":"2.0","id":%d,"method":"initialize","params":%s}""" id initializeParams

    let initializedNotification : string = """{"jsonrpc":"2.0","method":"notifications/initialized"}"""

    let toolsListRequest (id: int) : string = sprintf """{"jsonrpc":"2.0","id":%d,"method":"tools/list"}""" id

    /// `argumentsJson` is the object `Arguments.ofPayload` made, written into the request as it is.
    let toolsCallRequest (id: int) (tool: ToolSpec) (argumentsJson: string) : string =
      use stream = new MemoryStream()
      use writer = new Utf8JsonWriter(stream, Arguments.writerOptions)
      writer.WriteStartObject()
      writer.WriteString("jsonrpc", "2.0")
      writer.WriteNumber("id", id)
      writer.WriteString("method", "tools/call")
      writer.WritePropertyName "params"
      writer.WriteStartObject()
      writer.WriteString("name", tool.Name)
      writer.WritePropertyName "arguments"
      writer.WriteRawValue argumentsJson
      writer.WriteEndObject()
      writer.WriteEndObject()
      writer.Flush()
      Encoding.UTF8.GetString(stream.ToArray())

    let isIdOf (id: int) (message: JsonElement) : bool =
      match message.ValueKind with
      | JsonValueKind.Object ->
        match message.TryGetProperty "id" with
        | true, found when found.ValueKind = JsonValueKind.Number ->
          match found.TryGetInt32() with
          | true, number -> number = id
          | false, _ -> false
        | _ -> false
      | _ -> false

    let answerOfMessage (message: JsonElement) : Answer =
      match message.TryGetProperty "error" with
      | true, error ->
        Answer.Failed
          { Code =
              match error.TryGetProperty "code" with
              | true, code when code.ValueKind = JsonValueKind.Number ->
                (match code.TryGetInt32() with
                 | true, number -> number
                 | false, _ -> 0)
              | _ -> 0
            Message = stringProperty "message" error |> Option.defaultValue "" }
      | false, _ ->
        match message.TryGetProperty "result" with
        | true, result -> Answer.Succeeded(result.GetRawText())
        | false, _ -> Answer.Unanswered

    /// The answer to request `id` among the messages the daemon wrote: notifications and other ids are not it.
    let answerTo (id: int) (lines: string list) : Answer =
      lines
      |> List.tryPick (fun line ->
        match tryParseJson line with
        | None -> None
        | Some document ->
          use document = document
          match isIdOf id document.RootElement with
          | true -> Some(answerOfMessage document.RootElement)
          | false -> None)
      |> Option.defaultValue Answer.Unanswered

  /// A `tools/call` result: the text blocks in order, and whether the tool called it an error.
  type ToolReply = { IsError: bool; Blocks: string list }

  module ToolReply =

    let parse (resultJson: string) : ToolReply option =
      tryParseJson resultJson
      |> Option.map (fun document ->
        use document = document
        let root = document.RootElement
        let blocks =
          match root.ValueKind with
          | JsonValueKind.Object ->
            match root.TryGetProperty "content" with
            | true, content when content.ValueKind = JsonValueKind.Array ->
              content.EnumerateArray()
              |> Seq.filter (fun block -> stringProperty "type" block = Some "text")
              |> Seq.choose (stringProperty "text")
              |> List.ofSeq
            | _ -> []
          | _ -> []
        let isError =
          match root.ValueKind with
          | JsonValueKind.Object ->
            match root.TryGetProperty "isError" with
            | true, flag -> flag.ValueKind = JsonValueKind.True
            | false, _ -> false
          | _ -> false
        { IsError = isError; Blocks = blocks })

  // ---------------------------------------------------------------- what the command ends in

  /// Everything a run can end in. Each one has one stream, one text and one exit code (`Emission.ofOutcome`).
  [<RequireQualifiedAccess>]
  type Outcome =
    | Replied of ToolReply
    | ListedTools of ToolSpec list
    /// `<tool> --help`, for a tool the daemon lists.
    | ToolHelped of ToolSpec
    /// `<tool> --help`, for a name the daemon does not list to this caller, with the listed names nearest to it.
    | ToolNotListed of name: string * nearest: string list
    /// `<tool> --help` with no daemon to ask: the lines say how to start one.
    | HelpWithoutDaemon of tool: string * lines: string list
    | NotRunning of lines: string list
    | DaemonRefused of reason: string
    | DaemonGone of reason: string
    | RpcFailed of RpcFailure
    | BadAnswer of what: string
    | ArgsRefused of ArgRefusal
    | UsageRefused of UsageRefusal

  type Emission =
    { Stdout: string
      Stderr: string
      ExitCode: int }

  module Emission =

    /// How wide the description beside a name in `--list` may be.
    let listDescriptionWidth = 100

    /// What a refusal says when the caller was anonymous, because the next action depends on it: a session or a claim
    /// made by one call belongs to a connection that is gone by the next.
    let anonymousNote : string =
      sprintf
        "note: this call carried no member token, so the daemon saw a new anonymous connection. What one call makes (a session, a claim) belongs to that connection, and no later call is the same caller. Mint a token with 'sagefs member mint --role <role> --dir <checkout> --print-env %s' and pass it with --token or in %s."
        Capability.CapabilityTransport.bridgeEnvVar
        Capability.CapabilityTransport.bridgeEnvVar

    let withNote (credentials: Credentials) (text: string) : string =
      match credentials.Identity with
      | Identity.Anonymous -> text + "\n\n" + anonymousNote
      | Identity.Member _ -> text

    let failure (text: string) : Emission = { Stdout = ""; Stderr = text; ExitCode = 1 }

    let listing (credentials: Credentials) (tools: ToolSpec list) : string =
      let width = tools |> List.map (fun tool -> tool.Name.Length) |> List.fold max 0
      let lines =
        tools
        |> List.map (fun tool -> sprintf "%s  %s" (tool.Name.PadRight width) (ToolSpec.oneLine listDescriptionWidth tool))
      String.concat "\n" (sprintf "identity: %s" (Credentials.describe credentials) :: "" :: lines)

    /// The first block is the tool's answer on stdout; any later block is the daemon speaking (its event echo) and goes
    /// to stderr, so a script reading stdout reads the answer alone. An error keeps every block, in order, on stderr.
    let ofReply (credentials: Credentials) (reply: ToolReply) : Emission =
      match reply.IsError, reply.Blocks with
      | true, blocks -> failure (withNote credentials (String.concat "\n" blocks))
      | false, [] -> { Stdout = ""; Stderr = ""; ExitCode = 0 }
      | false, answer :: rest -> { Stdout = answer; Stderr = String.concat "\n" rest; ExitCode = 0 }

    let ofOutcome (credentials: Credentials) (outcome: Outcome) : Emission =
      match outcome with
      | Outcome.Replied reply -> ofReply credentials reply
      | Outcome.ListedTools tools -> { Stdout = listing credentials tools; Stderr = ""; ExitCode = 0 }
      | Outcome.ToolHelped tool -> { Stdout = ToolSpec.helpText tool; Stderr = ""; ExitCode = 0 }
      | Outcome.ToolNotListed(name, nearest) ->
        let close =
          match nearest with
          | [] -> []
          | names -> [ sprintf "Nearest tools: %s." (String.concat ", " names) ]
        failure (
          withNote
            credentials
            (String.concat
              "\n"
              ([ sprintf "sagefs call: the daemon lists no tool named '%s' to this caller (it lists tools by who is asking)." name ]
               @ close
               @ [ "'sagefs call --list' shows every tool this caller can call." ]))
        )
      | Outcome.HelpWithoutDaemon(tool, lines) ->
        { Stdout = usage
          Stderr =
            String.concat
              "\n"
              (lines
               @ [ ""
                   sprintf "What %s takes comes from the daemon, so its own help cannot be printed until one is running. The command's usage is on standard output." tool ])
          ExitCode = 1 }
      | Outcome.NotRunning lines -> failure (String.concat "\n" lines)
      | Outcome.DaemonRefused reason -> failure (withNote credentials (sprintf "sagefs call: the daemon refused the request: %s" reason))
      | Outcome.DaemonGone reason -> failure (sprintf "sagefs call: the daemon stopped answering: %s" reason)
      | Outcome.RpcFailed rpc -> failure (withNote credentials (sprintf "sagefs call: the daemon answered with an error (%d): %s" rpc.Code rpc.Message))
      | Outcome.BadAnswer what -> failure (sprintf "sagefs call: could not read the daemon's answer: %s" what)
      | Outcome.ArgsRefused(ArgRefusal.UnknownTool _ as refusal) ->
        failure (withNote credentials (sprintf "sagefs call: %s" (ArgRefusal.describe refusal)))
      | Outcome.ArgsRefused refusal -> failure (sprintf "sagefs call: %s" (ArgRefusal.describe refusal))
      | Outcome.UsageRefused refusal ->
        { Stdout = ""
          Stderr = sprintf "sagefs call: %s\n%s" (UsageRefusal.describe refusal) usage
          ExitCode = 2 }
