module SageFs.Tests.McpCallTests

/// `sagefs call` without a daemon: how the command line is read, how an argument is typed from the tool's own schema,
/// what a wrong name is refused with, which stream an answer goes to and the exit code. The command against a real
/// daemon (a token that keeps its identity from one call to the next) is in McpCallIntegrationTests.

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.McpCall
open SageFs.Tests.SharedGenerators

/// The shape of a real `tools/list` result: the SDK writes an optional value as a list of types.
let toolsListResult : string =
  """{"tools":[
      {"name":"run_tests","description":"Run tests in the session.\nSecond line.","inputSchema":{"type":"object","properties":{"pattern":{"type":"string"},"timeout_seconds":{"type":["integer","null"]},"verbose":{"type":"boolean"},"ratio":{"type":"number"},"ids":{"type":"array"},"filter":{"type":"object"},"loose":{}},"required":["pattern"]}},
      {"name":"get_daemon_status","description":"Daemon status.","inputSchema":{"type":"object","properties":{}}}
    ]}"""

let tools : ToolSpec list =
  match parseTools toolsListResult with
  | Result.Ok found -> found
  | Result.Error fault -> failtestf "the fixture should parse: %s" (ToolListFault.describe fault)

let runTests : ToolSpec = tools |> List.find (fun tool -> tool.Name = "run_tests")

let parsed (words: string) : Result<Invocation, UsageRefusal> =
  CallArgs.parse (words.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)

let flag (name: string) (value: string option) : RawFlag = { Name = name; Value = value }

let anonymous : Credentials =
  match Credentials.resolve TokenReads.nothing TokenChoice.FromEnvironment (fun _ -> null) with
  | Result.Ok credentials -> credentials
  | Result.Error refusal -> failtestf "anonymous should resolve: %A" refusal

let asMember : Credentials =
  match Credentials.resolve TokenReads.nothing (TokenChoice.Given "sfm_abc") (fun _ -> null) with
  | Result.Ok credentials -> credentials
  | Result.Error refusal -> failtestf "a token should resolve: %A" refusal

/// Text a person could type as an argument: no lone surrogates, no control characters.
let genText : Gen<string> =
  Gen.choose (32, 0xD7FF) |> Gen.map char |> Gen.arrayOf |> Gen.map String

let jsonOf (values: (string * ArgValue) list) : JsonDocument = JsonDocument.Parse(Arguments.toJson values)

let typedFrom (kind: ParamKind) (given: string) : Result<ArgValue, ArgRefusal> =
  Arguments.typeFlag { Name = "x"; Kind = kind; Need = ParamNeed.Optional } (flag "x" (Some given))

let genKind : Gen<ParamKind> =
  Gen.elements [ ParamKind.Words; ParamKind.WholeNumber; ParamKind.Fraction; ParamKind.Switch ]

/// A value a person could mean for a parameter of this kind.
let genValueOfKind (kind: ParamKind) : Gen<ArgValue> =
  match kind with
  | ParamKind.Words -> genText |> Gen.map ArgValue.Text
  | ParamKind.WholeNumber -> Gen.choose (Int32.MinValue, Int32.MaxValue) |> Gen.map (int64 >> ArgValue.Whole)
  | ParamKind.Fraction -> Gen.choose (-100000, 100000) |> Gen.map (fun n -> ArgValue.Real(float n / 8.0))
  | ParamKind.Switch -> Gen.elements [ true; false ] |> Gen.map ArgValue.Flag
  | ParamKind.Items
  | ParamKind.Record
  | ParamKind.Unspecified -> Gen.constant (ArgValue.Text "x")

/// A schema of parameters p0..pN with a value for each, as a person would type them.
let genSchemaWithValues : Gen<(ParamSpec * ArgValue) list> =
  gen {
    let! kinds = Gen.listOf genKind
    let! values = kinds |> List.map genValueOfKind |> Gen.sequenceToList
    return
      List.zip kinds values
      |> List.mapi (fun index (kind, value) -> { Name = sprintf "p%d" index; Kind = kind; Need = ParamNeed.Needed }, value)
  }

let sameJson (expected: ArgValue) (actual: JsonElement) : bool =
  match expected with
  | ArgValue.Text text -> actual.ValueKind = JsonValueKind.String && actual.GetString() = text
  | ArgValue.Whole whole -> actual.ValueKind = JsonValueKind.Number && actual.GetInt64() = whole
  | ArgValue.Real real -> actual.ValueKind = JsonValueKind.Number && actual.GetDouble() = real
  | ArgValue.Flag flag -> actual.ValueKind = (match flag with true -> JsonValueKind.True | false -> JsonValueKind.False)
  | ArgValue.Raw _ -> false

let genReply : Gen<ToolReply> =
  gen {
    let! isError = Gen.elements [ true; false ]
    let! blocks = Gen.listOf genText
    return { IsError = isError; Blocks = blocks }
  }

[<Tests>]
let tests =
  testList "McpCall" [

    testList "the command line" [
      test "WHY - a tool and its flags come back in order, with the command's own options read from anywhere" {
        parsed "run_tests --pattern Foo --verbose --token sfm_t --port 38001"
        |> Expect.equal
          "tool, flags in order, and the options pulled out"
          (Result.Ok(
            Invocation.Run
              { Target = Target.CallTool "run_tests"
                Port = PortChoice.Named 38001
                Token = TokenChoice.Given "sfm_t"
                Payload = Payload.Flags [ flag "pattern" (Some "Foo"); flag "verbose" None ] }
          ))
        parsed "--mcp-port 38002 --list"
        |> Expect.equal "--mcp-port is the same as --port" (Result.Ok(Invocation.Run { Target = Target.ListTools; Port = PortChoice.Named 38002; Token = TokenChoice.FromEnvironment; Payload = Payload.NoArguments }))
      }

      test "WHY - --name=value carries a value that starts with dashes or holds an equals sign" {
        parsed "eval --code=--help --other=a=b"
        |> Expect.equal
          "the first '=' splits"
          (Result.Ok(
            Invocation.Run
              { Target = Target.CallTool "eval"
                Port = PortChoice.Configured
                Token = TokenChoice.FromEnvironment
                Payload = Payload.Flags [ flag "code" (Some "--help"); flag "other" (Some "a=b") ] }
          ))
      }

      test "WHY - --json is the whole payload, and help is asked for either way" {
        parsed "stop_session --json {\"session_id\":\"abc\"}"
        |> Expect.equal
          "json"
          (Result.Ok(Invocation.Run { Target = Target.CallTool "stop_session"; Port = PortChoice.Configured; Token = TokenChoice.FromEnvironment; Payload = Payload.JsonObject "{\"session_id\":\"abc\"}" }))
        parsed "--help" |> Expect.equal "long" (Result.Ok Invocation.Help)
        parsed "-h" |> Expect.equal "short" (Result.Ok Invocation.Help)
      }

      test "WHY - each mistake is its own named refusal, and every refusal says something" {
        [ "", UsageRefusal.NothingToDo
          "--port 38001", UsageRefusal.NothingToDo
          "--list stop_session", UsageRefusal.ListAndTool "stop_session"
          "--list --json {}", UsageRefusal.ListTakesNoArguments
          "--list=yes", UsageRefusal.OptionTakesNoValue "--list"
          "--pattern x run_tests", UsageRefusal.FlagBeforeTool "--pattern"
          "run_tests stray", UsageRefusal.ExtraWord "stray"
          "run_tests --pattern a --pattern b", UsageRefusal.DuplicateFlag "--pattern"
          "run_tests --json {} --pattern a", UsageRefusal.JsonAndFlags
          "run_tests --port", UsageRefusal.OptionNeedsValue "--port"
          "run_tests --token", UsageRefusal.OptionNeedsValue "--token"
          "run_tests --port soon", UsageRefusal.NotAPort "soon"
          "run_tests --port 0", UsageRefusal.NotAPort "0"
          "run_tests --port 70000", UsageRefusal.NotAPort "70000" ]
        |> List.iter (fun (words, expected) ->
          parsed words |> Expect.equal (sprintf "'%s'" words) (Result.Error expected)
          String.IsNullOrWhiteSpace(UsageRefusal.describe expected) |> Expect.isFalse "and it says something")
      }
    ]

    testList "the tool's schema" [
      test "WHY - the parameters, their kinds and which are required come from the tool's own inputSchema" {
        runTests.Params
        |> List.map (fun spec -> spec.Name, spec.Kind, spec.Need)
        |> Expect.equal
          "every property, an optional type read through its null"
          [ "pattern", ParamKind.Words, ParamNeed.Needed
            "timeout_seconds", ParamKind.WholeNumber, ParamNeed.Optional
            "verbose", ParamKind.Switch, ParamNeed.Optional
            "ratio", ParamKind.Fraction, ParamNeed.Optional
            "ids", ParamKind.Items, ParamNeed.Optional
            "filter", ParamKind.Record, ParamNeed.Optional
            "loose", ParamKind.Unspecified, ParamNeed.Optional ]
        (tools |> List.find (fun tool -> tool.Name = "get_daemon_status")).Params |> Expect.isEmpty "a tool with no properties takes none"
      }

      test "WHY - a tools/list answer that is not one is a reason, not an exception" {
        parseTools "not json" |> Expect.isError "text"
        parseTools "[]" |> Expect.isError "an array"
        parseTools "{}" |> Expect.isError "no tools"
      }
    ]

    testList "typing an argument" [
      test "WHY - a string stays a string, a number a number and a bool a bool, even when the text looks like another" {
        typedFrom ParamKind.Words "42" |> Expect.equal "the string 42" (Result.Ok(ArgValue.Text "42"))
        typedFrom ParamKind.Words "true" |> Expect.equal "the string true" (Result.Ok(ArgValue.Text "true"))
        typedFrom ParamKind.Unspecified "7" |> Expect.equal "no schema type is a string" (Result.Ok(ArgValue.Text "7"))
        typedFrom ParamKind.WholeNumber "42" |> Expect.equal "42" (Result.Ok(ArgValue.Whole 42L))
        typedFrom ParamKind.WholeNumber "-7" |> Expect.equal "-7" (Result.Ok(ArgValue.Whole -7L))
        typedFrom ParamKind.Fraction "2.5" |> Expect.equal "2.5" (Result.Ok(ArgValue.Real 2.5))
        typedFrom ParamKind.Switch "FALSE" |> Expect.equal "case does not matter" (Result.Ok(ArgValue.Flag false))
        Arguments.typeFlag { Name = "v"; Kind = ParamKind.Switch; Need = ParamNeed.Optional } (flag "v" None)
        |> Expect.equal "a bool flag alone is true" (Result.Ok(ArgValue.Flag true))
      }

      test "WHY - a value of the wrong kind is refused with the kind it wanted" {
        typedFrom ParamKind.WholeNumber "1.5" |> Expect.equal "a fraction is not whole" (Result.Error(ArgRefusal.NotAWholeNumber("x", "1.5")))
        typedFrom ParamKind.WholeNumber "ten" |> Expect.equal "words" (Result.Error(ArgRefusal.NotAWholeNumber("x", "ten")))
        typedFrom ParamKind.Fraction "NaN" |> Expect.equal "not finite" (Result.Error(ArgRefusal.NotANumber("x", "NaN")))
        typedFrom ParamKind.Switch "yes" |> Expect.equal "not a bool" (Result.Error(ArgRefusal.NotABoolean("x", "yes")))
        typedFrom ParamKind.Items "{}" |> Expect.equal "an object is not an array" (Result.Error(ArgRefusal.NotJsonOfKind("x", ParamKind.Items)))
        typedFrom ParamKind.Record "[1]" |> Expect.equal "an array is not an object" (Result.Error(ArgRefusal.NotJsonOfKind("x", ParamKind.Record)))
        Arguments.typeFlag { Name = "n"; Kind = ParamKind.WholeNumber; Need = ParamNeed.Optional } (flag "n" None)
        |> Expect.equal "a number needs its value" (Result.Error(ArgRefusal.FlagNeedsValue("n", ParamKind.WholeNumber)))
      }

      test "WHY - an array or an object is passed through as the JSON it was" {
        typedFrom ParamKind.Items "[1, \"a\"]" |> Expect.equal "array" (Result.Ok(ArgValue.Raw "[1, \"a\"]"))
        let call = Arguments.ofFlags runTests [ flag "pattern" (Some "Foo"); flag "ids" (Some "[1,2]"); flag "timeout_seconds" (Some "30") ]
        match call with
        | Result.Ok values ->
          use document = jsonOf values
          document.RootElement.GetProperty("ids").GetArrayLength() |> Expect.equal "two ids" 2
          document.RootElement.GetProperty("timeout_seconds").GetInt32() |> Expect.equal "a number on the wire" 30
          document.RootElement.GetProperty("pattern").GetString() |> Expect.equal "a string on the wire" "Foo"
        | Result.Error refusal -> failtestf "should type: %A" refusal
      }

      testPropertyWithConfig propConfig "WHY - any schema-valid argument survives text, parse, typing and JSON: the daemon receives the value that was meant" (
        Prop.forAll (Arb.fromGen genSchemaWithValues) (fun pairs ->
          let tool = { Name = "t"; Description = ""; Params = pairs |> List.map fst }
          let words =
            "t" :: (pairs |> List.map (fun (spec, value) -> sprintf "--%s=%s" spec.Name (ArgValue.render value)))
          match CallArgs.parse words with
          | Result.Ok(Invocation.Run run) ->
            match Arguments.ofPayload tool run.Payload with
            | Result.Ok json ->
              use document = JsonDocument.Parse json
              pairs |> List.forall (fun (spec, value) -> sameJson value (document.RootElement.GetProperty spec.Name))
            | Result.Error _ -> false
          | _ -> false)
      )

      testPropertyWithConfig propConfig "WHY - a string argument is exactly the text typed, whatever it holds" (
        Prop.forAll (Arb.fromGen genText) (fun text ->
          match typedFrom ParamKind.Words text with
          | Result.Ok value ->
            use document = jsonOf [ "x", value ]
            document.RootElement.GetProperty("x").GetString() = text
          | Result.Error _ -> false)
      )
    ]

    testList "a name the tool does not have" [
      test "WHY - testPattern is refused with the tool's real parameter names, which is what the slip needed" {
        match Arguments.ofFlags runTests [ flag "testPattern" (Some "Foo") ] with
        | Result.Error(ArgRefusal.UnknownParameter(name, tool) as refusal) ->
          name |> Expect.equal "the name given" "--testPattern"
          tool.Name |> Expect.equal "of this tool" "run_tests"
          let text = ArgRefusal.describe refusal
          text |> Expect.stringContains "says what was wrong" "'--testPattern' is not a parameter of run_tests"
          text |> Expect.stringContains "and gives the real name" "pattern (string, required)"
        | other -> failtestf "should be refused as an unknown parameter: %A" other
      }

      testPropertyWithConfig propConfig "WHY - any name outside the schema is refused, and the refusal names every real parameter" (
        Prop.forAll (Arb.fromGen genText) (fun name ->
          let realNames = runTests.Params |> List.map (fun spec -> spec.Name)
          match List.contains name realNames with
          | true -> true
          | false ->
            match Arguments.ofFlags runTests [ flag name (Some "x") ] with
            | Result.Error(ArgRefusal.UnknownParameter _ as refusal) ->
              let text = ArgRefusal.describe refusal
              realNames |> List.forall (fun real -> text.Contains real)
            | _ -> false)
      )

      test "WHY - a missing required parameter is named before the daemon is asked" {
        match Arguments.ofFlags runTests [ flag "verbose" None ] with
        | Result.Error(ArgRefusal.MissingRequired(_, missing)) -> missing |> Expect.equal "pattern" [ "pattern" ]
        | other -> failtestf "should name the missing parameter: %A" other
        Arguments.ofPayload runTests Payload.NoArguments |> Expect.isError "no arguments is also missing it"
      }

      test "WHY - --json is held to the same names, and must be one object" {
        Arguments.ofJson runTests "{\"pattern\":\"Foo\",\"timeout_seconds\":30}" |> Expect.isOk "real names pass as written"
        match Arguments.ofJson runTests "{\"pattern\":\"Foo\",\"testPattern\":\"x\"}" with
        | Result.Error(ArgRefusal.UnknownParameter(name, _)) -> name |> Expect.equal "the unknown key" "testPattern"
        | other -> failtestf "should refuse the key: %A" other
        Arguments.ofJson runTests "[1]" |> Expect.equal "an array" (Result.Error ArgRefusal.JsonArgumentsNotAnObject)
        Arguments.ofJson runTests "nope" |> Expect.equal "not json" (Result.Error ArgRefusal.JsonArgumentsNotAnObject)
      }

      test "WHY - a tool the daemon does not list is refused with the ones it does" {
        match Arguments.findTool tools "run_test" with
        | Result.Error(ArgRefusal.UnknownTool(_, known) as refusal) ->
          known |> Expect.equal "the listed names" [ "run_tests"; "get_daemon_status" ]
          ArgRefusal.describe refusal |> Expect.stringContains "names them" "run_tests, get_daemon_status"
        | other -> failtestf "should be refused: %A" other
      }
    ]

    testList "who is calling" [
      test "WHY - a token from the flag wins over the environment, and neither is anonymous" {
        let env name = match name with "SAGEFS_MEMBER_TOKEN" -> " sfm_env " | _ -> null
        match Credentials.resolve TokenReads.nothing TokenChoice.FromEnvironment env, Credentials.resolve TokenReads.nothing (TokenChoice.Given " sfm_flag ") env, Credentials.resolve TokenReads.nothing TokenChoice.FromEnvironment (fun _ -> null) with
        | Result.Ok fromEnv, Result.Ok fromFlag, Result.Ok none ->
          fromEnv.Identity |> Expect.equal "from the environment" (Identity.Member TokenSource.Environment)
          fromEnv.Headers |> Expect.equal "trimmed, in the member header" [ Capability.CapabilityTransport.headerName, "sfm_env" ]
          fromFlag.Identity |> Expect.equal "from the flag" (Identity.Member TokenSource.Flag)
          fromFlag.Headers |> Expect.equal "the flag's token" [ Capability.CapabilityTransport.headerName, "sfm_flag" ]
          none.Identity |> Expect.equal "none" Identity.Anonymous
          none.Headers |> Expect.isEmpty "and sends no header"
        | other -> failtestf "should resolve: %A" other
      }

      test "WHY - a blank --token is refused, never read as anonymous" {
        Credentials.resolve TokenReads.nothing (TokenChoice.Given "  ") (fun _ -> null) |> Expect.equal "blank" (Result.Error UsageRefusal.BlankToken)
      }

      test "WHY - the operator credential rides as Authorization beside the member token" {
        let env name =
          match name with
          | "SAGEFS_MEMBER_TOKEN" -> "sfm_m"
          | "SAGEFS_OPERATOR_TOKEN" -> "sfo_op"
          | _ -> null
        match Credentials.resolve TokenReads.nothing TokenChoice.FromEnvironment env with
        | Result.Ok credentials ->
          Credentials.hasOperator credentials |> Expect.isTrue "present"
          credentials.Headers |> List.map fst |> Expect.equal "both headers" [ Capability.CapabilityTransport.headerName; "Authorization" ]
          Credentials.describe credentials |> Expect.stringContains "and said so" "operator credential"
        | Result.Error refusal -> failtestf "should resolve: %A" refusal
      }

      testPropertyWithConfig propConfig "WHY - no description of who is calling ever holds the token" (
        Prop.forAll (Arb.fromGen genText) (fun text ->
          let token = "sfm_" + text.Trim() + "Z"
          match Credentials.resolve TokenReads.nothing (TokenChoice.Given token) (fun _ -> null) with
          | Result.Ok credentials -> not ((Credentials.describe credentials).Contains token) && (credentials.Headers |> List.exists (fun (_, value) -> value = token.Trim()))
          | Result.Error _ -> false)
      )
    ]

    testList "the daemon's answers" [
      test "WHY - the answer to a request is the message with its id, among the notifications the daemon also wrote" {
        let lines =
          [ """{"method":"notifications/message","params":{}}"""
            """{"jsonrpc":"2.0","id":9,"result":{"other":true}}"""
            """{"jsonrpc":"2.0","id":2,"result":{"tools":[]}}""" ]
        Rpc.answerTo 2 lines |> Expect.equal "id 2" (Answer.Succeeded """{"tools":[]}""")
        Rpc.answerTo 3 lines |> Expect.equal "nobody answered 3" Answer.Unanswered
        Rpc.answerTo 4 [ """{"jsonrpc":"2.0","id":4,"error":{"code":-32602,"message":"bad"}}""" ]
        |> Expect.equal "an error" (Answer.Failed { Code = -32602; Message = "bad" })
        Rpc.answerTo 1 [ "not json" ] |> Expect.equal "garbage is no answer" Answer.Unanswered
      }

      test "WHY - tools/call carries the tool's name and the arguments object as written" {
        use document = JsonDocument.Parse(Rpc.toolsCallRequest 7 runTests """{"pattern":"Foo"}""")
        let root = document.RootElement
        root.GetProperty("id").GetInt32() |> Expect.equal "the id" 7
        root.GetProperty("method").GetString() |> Expect.equal "the method" "tools/call"
        root.GetProperty("params").GetProperty("name").GetString() |> Expect.equal "the tool" "run_tests"
        root.GetProperty("params").GetProperty("arguments").GetProperty("pattern").GetString() |> Expect.equal "the argument" "Foo"
      }

      test "WHY - a tools/call result is its text blocks in order and whether the tool called it an error" {
        ToolReply.parse """{"content":[{"type":"text","text":"answer"},{"type":"image","data":"x"},{"type":"text","text":"echo"}],"isError":true}"""
        |> Expect.equal "blocks and flag" (Some { IsError = true; Blocks = [ "answer"; "echo" ] })
        ToolReply.parse """{"content":[{"type":"text","text":"fine"}]}""" |> Expect.equal "no flag is no error" (Some { IsError = false; Blocks = [ "fine" ] })
        ToolReply.parse "nope" |> Expect.isNone "not json"
      }
    ]

    testList "what is printed, where, and the exit code" [
      testPropertyWithConfig propConfig "WHY - a tool error is on stderr with every block, nothing on stdout, exit 1" (
        Prop.forAll (Arb.fromGen genReply) (fun reply ->
          let reply = { reply with IsError = true }
          let emission = Emission.ofReply asMember reply
          emission.ExitCode = 1
          && emission.Stdout = ""
          && reply.Blocks |> List.forall (fun block -> emission.Stderr.Contains block))
      )

      testPropertyWithConfig propConfig "WHY - a good answer is its first block on stdout, exit 0, and the daemon's later blocks stay off stdout" (
        Prop.forAll (Arb.fromGen genReply) (fun reply ->
          let reply = { reply with IsError = false }
          let emission = Emission.ofReply anonymous reply
          emission.ExitCode = 0
          && emission.Stdout = (reply.Blocks |> List.tryHead |> Option.defaultValue "")
          && not (emission.Stderr.Contains "no member token"))
      )

      test "WHY - an anonymous refusal says it was anonymous and what to do, and a member's refusal does not" {
        let refusal = { IsError = true; Blocks = [ "stop_session: owned by another bound MCP connection" ] }
        let anonymousText = (Emission.ofReply anonymous refusal).Stderr
        anonymousText |> Expect.stringContains "keeps the full text" "owned by another bound MCP connection"
        anonymousText |> Expect.stringContains "says no token was carried" "carried no member token"
        anonymousText |> Expect.stringContains "and the next action" "sagefs member mint"
        (Emission.ofReply asMember refusal).Stderr |> Expect.equal "the member's refusal is the tool's text alone" "stop_session: owned by another bound MCP connection"
      }

      test "WHY - every way a run can end has its exit code: 0 answered, 2 usage, 1 anything the caller must act on" {
        let exitOf outcome = (Emission.ofOutcome anonymous outcome).ExitCode
        exitOf (Outcome.Replied { IsError = false; Blocks = [ "ok" ] }) |> Expect.equal "answered" 0
        exitOf (Outcome.ListedTools tools) |> Expect.equal "listed" 0
        exitOf (Outcome.Replied { IsError = true; Blocks = [ "no" ] }) |> Expect.equal "tool error" 1
        exitOf (Outcome.NotRunning [ "No daemon running" ]) |> Expect.equal "no daemon" 1
        exitOf (Outcome.DaemonRefused "403") |> Expect.equal "refused" 1
        exitOf (Outcome.DaemonGone "reset") |> Expect.equal "gone" 1
        exitOf (Outcome.RpcFailed { Code = -1; Message = "m" }) |> Expect.equal "rpc error" 1
        exitOf (Outcome.BadAnswer "x") |> Expect.equal "unreadable" 1
        exitOf (Outcome.ArgsRefused(ArgRefusal.JsonArgumentsNotAnObject)) |> Expect.equal "bad arguments" 1
        exitOf (Outcome.UsageRefused UsageRefusal.NothingToDo) |> Expect.equal "usage" 2
      }

      test "WHY - an argument refusal goes to stderr naming the real parameters, and sends nothing to stdout" {
        match Arguments.ofFlags runTests [ flag "testPattern" (Some "x") ] with
        | Result.Error refusal ->
          let emission = Emission.ofOutcome anonymous (Outcome.ArgsRefused refusal)
          emission.Stdout |> Expect.equal "stdout is empty" ""
          emission.ExitCode |> Expect.equal "fails" 1
          emission.Stderr |> Expect.stringContains "names the tool's parameters" "pattern (string, required)"
        | Result.Ok _ -> failtest "testPattern is not a parameter of run_tests"
      }

      test "WHY - --list says who is calling, then one line per tool: the name and the first line of its description" {
        let text = (Emission.ofOutcome anonymous (Outcome.ListedTools tools)).Stdout
        let lines = text.Split '\n'
        lines.[0] |> Expect.stringStarts "the identity comes first" "identity: anonymous"
        text |> Expect.stringContains "a tool and the first line" "run_tests          Run tests in the session."
        text.Contains "Second line" |> Expect.isFalse "only the first line"
        (Emission.ofOutcome asMember (Outcome.ListedTools tools)).Stdout |> Expect.stringContains "a member says so" "a member token from --token"
      }

      test "WHY - a long description is cut so the list stays one line a tool" {
        let long = { Name = "t"; Description = String('x', 300); Params = [] }
        (ToolSpec.oneLine Emission.listDescriptionWidth long).Length |> Expect.equal "cut, with the dots" (Emission.listDescriptionWidth + 3)
      }

      test "WHY - a missing daemon is the answer sagefs status gives, and a running one has nothing to say" {
        let notRunning = DaemonPresence.unreachableLines 37749 DaemonPresence.NotRunning
        notRunning |> List.head |> Expect.equal "the headline" "No daemon running"
        notRunning |> String.concat "\n" |> Expect.stringContains "and the durable fix" "claude mcp add sagefs -- sagefs mcp"
        DaemonPresence.unreachableLines 37749 (DaemonPresence.Wedged 123) |> String.concat "\n" |> Expect.stringContains "a wedged one names the stop" "sagefs stop --mcp-port 37749"
        (Emission.ofOutcome anonymous (Outcome.NotRunning notRunning)).Stderr |> Expect.stringContains "call prints it" "No daemon running"
      }
    ]
  ]
