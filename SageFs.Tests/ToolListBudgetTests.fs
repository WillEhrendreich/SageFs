module SageFs.Tests.ToolListBudgetTests

/// The tool catalog is the largest per-session cost an agent pays (roast 2026-11-08, priority 9): `tools/list` was 111,052
/// characters for 72 tools, against 5,606 for FsiMcp 0.3.0. Nothing bounded it. These cases hold the weight of what the
/// daemon lists to a recorded budget that only goes down (`--ratchets --tighten`), so a long description or a sixth copy
/// of a parameter's sentence fails a test and not a person's context window.
///
/// The weight is counted the way docs/plans/small-default-tool-list.md counted it: each registered tool is built the way
/// `.WithTools<SageFsTools>()` builds it, given the hints the list filter gives it, serialized as the wire serializes a
/// tool (minified, no envelope), and the characters of the JSON array are summed.
/// Characters, not tokens: the table says so, and the tokens figure in the plan is these characters divided by four.

open System
open System.Reflection
open System.Text.Json
open Expecto
open Expecto.Flip
open ModelContextProtocol
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open SageFs.Server.McpTools

/// What the daemon lists, built in process.
module ListedDefinitions =
  let private flags = BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly

  let private isTool (m: MethodInfo) =
    m.GetCustomAttributes(true) |> Array.exists (fun a -> a :? McpServerToolAttribute)

  /// Every registered tool, as the SDK builds it from its method. The target is never created: a definition does not call.
  let registered () : Tool list =
    typeof<SageFsTools>.GetMethods flags
    |> Array.filter isTool
    |> Array.map (fun m -> McpServerTool.Create(m, Func<RequestContext<CallToolRequestParams>, obj>(fun _ -> null), null).ProtocolTool)
    |> List.ofArray

  /// The list a caller that is shown everything receives.
  let full () : Tool list =
    let tools = Collections.Generic.List<Tool>(registered ())
    SageFs.Server.McpServer.applyDestructiveHints tools
    List.ofSeq tools

  /// The wire's serializer with escaping relaxed, so an apostrophe or an arrow counts as the one character a model reads.
  let private readable : JsonSerializerOptions =
    let options = JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
    options.Encoder <- System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    options

  let characters (tool: Tool) : int =
    JsonSerializer.Serialize(tool, readable).Length

  /// The characters of the JSON array of these tools: the definitions, a comma between each, and the brackets.
  let weight (tools: Tool list) : int =
    match tools with
    | [] -> 2
    | _ -> (tools |> List.sumBy characters) + (List.length tools - 1) + 2

let private fullCatalogKey = "full catalog characters"

/// The live weights, by the key `RatchetBudgets.toolList` uses.
let private actual () : (string * int) list =
  [ fullCatalogKey, ListedDefinitions.weight (ListedDefinitions.full ()) ]

let private budgetTable =
  TestInfrastructure.Ratchet.table
    { Name = "tool-list weight budgets"
      SourceFile = RatchetBudgets.sourceFile
      Budgets = RatchetBudgets.toolList
      Actual = actual }

[<Tests>]
let weightBudgets =
  testList "Tool list weight (ratchet down, never raise)" [
    for (key, budget) in budgetTable.Budgets ->
      testCase (sprintf "WHY - %s stays within %d, so the list a first session pays for can only get lighter" key budget) <| fun () ->
        let live = actual () |> List.find (fun (k, _) -> k = key) |> snd
        (live <= budget)
        |> Expect.isTrue (sprintf "%s is %d, over its budget of %d. Shorten a description or a parameter sentence; never raise the budget." key live budget)
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
