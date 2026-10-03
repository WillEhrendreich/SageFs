module SageFs.Tests.ToolSurfaceHonestyTests

// WHY — `discover_features` is the tool an agent asks "what can I use?". It advertised six tools
// (explore_namespace, explore_type, get_completions, get_file_coverage, query_test_coverage,
// visualize_domain_model) that `tools/list` never had, and the instructions text for them still sat
// in McpTools.fs. The registered set, read by reflection, is the one source of truth: discovery is
// built from it, and every other list of tool names is compared to it here.
open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Affordances
open SageFs.Features.FeatureDiscovery
open SageFs.Server.McpTools

let private repoRoot =
  // Runtime-resolved from the assembly's own location. A walk up from the working
  // directory throws during static initialisation when the suite is launched from
  // outside the checkout, taking the whole discovery pass down with it.
  RepoPaths.requireRepoRoot ()

let private registered : RegisteredTool list = RegisteredTools.describe typeof<SageFsTools>

let private registeredNames : Set<string> = registered |> List.map (fun t -> t.Name) |> Set.ofList

let private toolMethod (name: string) : MethodInfo =
  typeof<SageFsTools>.GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly)
  |> Array.find (fun m -> m.Name = name)

let private descriptionOf (name: string) : string =
  (toolMethod name).GetCustomAttribute<System.ComponentModel.DescriptionAttribute>().Description

let private sessionStates : SessionState list =
  [ SessionState.Uninitialized; SessionState.WarmingUp; SessionState.Ready; SessionState.Evaluating; SessionState.Faulted ]

/// The tools that read one session's evals or tests, and so take the routing arguments.
let private sessionAnalysisTools =
  [ "diagnose"; "coverage_intel"; "impact_forecast"; "plan_ripple"; "preview_what_if"
    "suggest_next_cell"; "suggest_next_action"; "get_cell_dependencies"; "discover_features" ]

let private discoverEverything = FeatureDiscovery.discoverOver registered FeatureDiscovery.emptyContext

/// Names that look like tools (a verb prefix and an underscore) in some text an agent reads, that tools/list does
/// not have: a typo, or a tool that left without the text being fixed.
let private unregisteredToolNamesIn (text: string) : string list =
  let verbs = [ "get"; "list"; "run"; "set"; "create"; "switch"; "stop"; "enable"; "disable"; "explain"; "suggest"; "plan"; "preview"; "check"; "send"; "reset"; "acquire"; "release"; "export"; "manage"; "report"; "tidy"; "discover"; "decompose"; "cancel"; "request"; "reassign"; "join"; "leave" ]
  Regex(sprintf @"\b(?:%s)_[a-z_]+\b" (String.Join("|", verbs))).Matches(text)
  |> Seq.map (fun m -> m.Value)
  |> Seq.filter (fun name -> not (registeredNames.Contains name))
  |> Seq.distinct
  |> List.ofSeq

[<Tests>]
let discoveryTests =
  testList "discover_features is built from the registered tool set" [

    testCase "WHY — the registered set is read, so this suite cannot pass by testing nothing" <| fun _ ->
      (registered.Length, 50) |> Expect.isGreaterThan "many tools are registered"

    testCase "WHY — every tool discovery has a ranking rule for is registered, so it cannot advertise one that is not" <| fun _ ->
      FeatureDiscovery.rankedToolNames
      |> List.filter (fun name -> not (registeredNames.Contains name))
      |> Expect.isEmpty "a ranking rule names a tool tools/list does not have"

    testCase "WHY — discovery lists exactly the registered tools, none missing and none extra" <| fun _ ->
      discoverEverything.Suggestions
      |> List.map (fun s -> s.ToolName)
      |> Set.ofList
      |> Expect.equal "advertised = registered" registeredNames

    testCase "WHY — each registered tool is advertised once" <| fun _ ->
      discoverEverything.Suggestions
      |> List.map (fun s -> s.ToolName)
      |> List.countBy id
      |> List.filter (fun (_, n) -> n <> 1)
      |> Expect.isEmpty "no duplicates"

    testCase "WHY — the advertised count is the registered count" <| fun _ ->
      discoverEverything.TotalKnownFeatures |> Expect.equal "TotalKnownFeatures" registered.Length

    testCase "WHY — none of the retired tool names is advertised" <| fun _ ->
      let advertised = discoverEverything.Suggestions |> List.map (fun s -> s.ToolName) |> Set.ofList
      RetiredTool.toolNames
      |> List.filter advertised.Contains
      |> Expect.isEmpty "a retired tool is advertised"

    testCase "WHY — every advertised summary and example comes from the tool's own registration" <| fun _ ->
      discoverEverything.Suggestions
      |> List.iter (fun s ->
        let tool = registered |> List.find (fun t -> t.Name = s.ToolName)
        s.ShortDescription |> Expect.equal (sprintf "%s summary" s.ToolName) tool.Summary
        s.ExampleUsage |> Expect.equal (sprintf "%s example" s.ToolName) (FeatureDiscovery.exampleOf tool)
        s.ShortDescription |> Expect.isNotEmpty (sprintf "%s has a summary" s.ToolName))

    testCase "WHY — an example call names every required parameter and no optional one" <| fun _ ->
      let tool = registered |> List.find (fun t -> t.Name = "plan_ripple")
      FeatureDiscovery.exampleOf tool |> Expect.equal "only changed_cells is required" "plan_ripple(changed_cells=...)"

    testCase "WHY — a registered tool that discovery has no rule for is still advertised, ranked last" <| fun _ ->
      let unranked = registered |> List.filter (fun t -> not (List.contains t.Name FeatureDiscovery.rankedToolNames))
      unranked |> Expect.isNonEmpty "some registered tools have no ranking rule"
      let last = discoverEverything.Suggestions |> List.map (fun s -> s.Relevance) |> List.last
      last |> Expect.equal "the unranked tools sit in the last tier" FeatureRelevance.Contextual
  ]

[<Tests>]
let nameListsTests =
  testList "every other list of tool names agrees with the registered set" [

    testCase "WHY — the six tools discovery used to advertise are retired names now, so they cannot come back unnoticed" <| fun _ ->
      RetiredTool.toolNames
      |> Expect.containsAll
        "the retired vocabulary names them"
        [ "explore_namespace"; "explore_type"; "get_completions"; "get_file_coverage"; "query_test_coverage"; "visualize_domain_model" ]

    testCase "WHY — no retired name is registered" <| fun _ ->
      RetiredTool.toolNames
      |> List.filter registeredNames.Contains
      |> Expect.isEmpty "a name is both retired and registered"

    testCase "WHY — every tool an affordance state offers is registered" <| fun _ ->
      sessionStates
      |> List.collect (fun state -> availableTools state |> List.map (fun name -> state, name))
      |> List.filter (fun (_, name) -> not (registeredNames.Contains name))
      |> Expect.isEmpty "an affordance state offers a tool that is not registered"

    testCase "WHY — the state list above covers every session state, so a new state cannot skip the check" <| fun _ ->
      sessionStates.Length |> Expect.equal "every case" (FSharpType.GetUnionCases(typeof<SessionState>).Length)

    testCase "WHY — docs/mcp-tools.md lists exactly the registered tools" <| fun _ ->
      let docs = File.ReadAllText(Path.Combine(repoRoot, "docs", "mcp-tools.md"))
      let documented =
        Regex.Matches(docs, @"^\| `([a-z][a-z0-9_]*)` \|", RegexOptions.Multiline)
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> Set.ofSeq
      documented |> Expect.equal "documented = registered" registeredNames

    testCase "WHY — docs/mcp-tools.md states the registered count" <| fun _ ->
      let docs = File.ReadAllText(Path.Combine(repoRoot, "docs", "mcp-tools.md"))
      docs |> Expect.stringContains "the advertised count" (sprintf "The full advertised set is %d tools" registered.Length)

    testCase "WHY — no registered tool's description names a retired tool, so the surface never points an agent at one" <| fun _ ->
      registered
      |> List.collect (fun tool ->
        let description = descriptionOf tool.Name
        RetiredTool.toolNames |> List.filter description.Contains |> List.map (fun retired -> sprintf "%s mentions %s" tool.Name retired))
      |> Expect.isEmpty "a description mentions a retired tool"

    testCase "WHY — every tool-shaped name a registered description tells an agent to call is registered or retired on purpose" <| fun _ ->
      registered
      |> List.collect (fun tool ->
        unregisteredToolNamesIn (descriptionOf tool.Name)
        |> List.map (fun name -> sprintf "%s names %s" tool.Name name))
      |> Expect.isEmpty "a description names a tool that is not registered"

    testCase "WHY — the live-testing discovery hint does not point an agent at a tool that does not exist" <| fun _ ->
      [ Features.LiveTesting.LiveTestDiscoveryState.Disabled
        Features.LiveTesting.LiveTestDiscoveryState.Discovering
        Features.LiveTesting.LiveTestDiscoveryState.ReadyZeroTests
        Features.LiveTesting.LiveTestDiscoveryState.ReadyWithTests 3 ]
      |> List.collect (fun state ->
        unregisteredToolNamesIn (Features.LiveTesting.LiveTestDiscoveryState.hint state)
        |> List.map (fun name -> sprintf "%A hint names %s" state name))
      |> Expect.isEmpty "a hint tells an agent to call a tool tools/list does not have (live testing is switched on with switch_workflow)"

    testCase "WHY — the hint for live testing being off names the tool that does switch it on" <| fun _ ->
      Features.LiveTesting.LiveTestDiscoveryState.hint Features.LiveTesting.LiveTestDiscoveryState.Disabled
      |> Expect.stringContains "the real way in" "switch_workflow"
  ]

[<Tests>]
let runTestsVerdictTests =
  testList "run_tests lists every verdict a receipt can carry" [

    testCase "WHY — the verdicts in the run_tests description are the verdict type's own cases, so adding one cannot go unlisted" <| fun _ ->
      let verdicts =
        FSharpType.GetUnionCases(typeof<Features.RunReceipts.RunVerdict>)
        |> Array.map (fun case -> Features.RunReceipts.RunVerdict.token (FSharpValue.MakeUnion(case, [||]) :?> Features.RunReceipts.RunVerdict))
        |> Set.ofArray
      let listed =
        Regex.Match(descriptionOf "run_tests", @"has a verdict \(([^)]*)\)").Groups.[1].Value.Split(',')
        |> Array.map (fun token -> token.Trim())
        |> Set.ofArray
      listed |> Expect.equal "the description lists exactly the verdict tokens" verdicts

    testCase "WHY — run_tests says that none of the Passed-on-something verdicts is green" <| fun _ ->
      let d = descriptionOf "run_tests"
      [ "PassedOnStaleSource"; "PassedWhileRebuilding"; "PassedOnUnknownSource" ]
      |> List.iter (fun token ->
        (Regex.Matches(d, Regex.Escape token).Count, 1)
        |> Expect.isGreaterThan (sprintf "%s is explained in prose, not only listed" token))
      d |> Expect.stringContains "says they are not green" "none of them is green"
  ]

[<Tests>]
let analysisToolDescriptionTests =
  testList "the analysis tools say what they measure" [

    testCase "WHY — each session-analysis tool takes session_id and working_directory, so a caller can name the session" <| fun _ ->
      sessionAnalysisTools
      |> List.collect (fun name ->
        let parameters = (toolMethod name).GetParameters() |> Array.map (fun p -> p.Name) |> Set.ofArray
        [ "session_id"; "working_directory" ]
        |> List.filter (fun p -> not (parameters.Contains p))
        |> List.map (fun p -> sprintf "%s lacks %s" name p))
      |> Expect.isEmpty "a session-analysis tool cannot be pointed at a session"

    testCase "WHY — each tool that answers Measured or NotAvailable says so in its description" <| fun _ ->
      [ "diagnose"; "coverage_intel"; "impact_forecast"; "plan_ripple"; "preview_what_if"; "suggest_next_cell"; "suggest_next_action"; "get_cell_dependencies" ]
      |> List.filter (fun name ->
        let d = descriptionOf name
        not (d.Contains "NotAvailable" && d.Contains "Measured"))
      |> Expect.isEmpty "a description does not explain the typed answer"

    testCase "WHY — impact_forecast says it measures REPL cells and not the blast radius of a source change" <| fun _ ->
      let d = descriptionOf "impact_forecast"
      d |> Expect.stringContains "what it measures" "WHAT IT MEASURES"
      d |> Expect.stringContains "what it does not" "the blast radius of a change to your source code"
      d |> Expect.stringContains "do not gate on it" "do not gate a change on it"

    testCase "WHY — impact_forecast's description states the thresholds the code applies" <| fun _ ->
      let d = descriptionOf "impact_forecast"
      let p95Acceptable = int Features.ImpactForecast.ImpactForecast.P95AcceptableMs
      let p95Investigate = int Features.ImpactForecast.ImpactForecast.P95InvestigateMs
      d |> Expect.stringContains "Investigate latency" (sprintf "P95 is over %d ms" p95Acceptable)
      d |> Expect.stringContains "Refactor latency" (sprintf "P95 is over %d ms" p95Investigate)
      d |> Expect.stringContains "Investigate fan-out" (sprintf "more than %d cells are downstream" Features.ImpactForecast.ImpactForecast.DownstreamAcceptable)
      d |> Expect.stringContains "Refactor fan-out" (sprintf "more than %d are" Features.ImpactForecast.ImpactForecast.DownstreamInvestigate)

    testCase "WHY — coverage_intel says only live testing produces its data, and names the reasons" <| fun _ ->
      let d = descriptionOf "coverage_intel"
      d |> Expect.stringContains "needs live testing" "live testing's instrumented runs"
      [ "NeedsAWorkflow"; "NeedsLiveTesting"; "NoCoverageRecorded"; "NoTestRunYet" ]
      |> List.iter (fun reason -> d |> Expect.stringContains (sprintf "names %s" reason) reason)

    testCase "WHY — get_cell_dependencies does not claim to know which cells are stale" <| fun _ ->
      let d = descriptionOf "get_cell_dependencies"
      d |> Expect.stringContains "says staleness is not measured" "NotMeasured"
      d.Contains "IsStale" |> Expect.isFalse "no stale flag is promised"

    testCase "WHY — diagnose says it will not claim 'no issues' about a side it did not read" <| fun _ ->
      let d = descriptionOf "diagnose"
      d |> Expect.stringContains "names the unmeasured field" "Unmeasured"
      d |> Expect.stringContains "refuses to say 'no issues' when nothing was seen" "NothingObservedYet"
  ]

do
  TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant discoveryTests |> ignore
  TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant nameListsTests |> ignore
