/// A tool description that names another tool sends the agent there. One named
/// `run_project_tests` while no such tool existed: `acquire_test_suite_lease` said
/// "SageFs runs built-in tests through run_project_tests instead", and agents that
/// acquired the lease went looking for a tool that was never there and fell back to
/// `dotnet test`. The retired-name test only knows names someone listed; this one
/// reads the catalog itself: every tool-shaped name in a tool's own text must be a
/// tool that is actually registered.
module SageFs.Tests.ToolNameReferenceTests

open System
open System.ComponentModel
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

/// Every registered tool with its description and each parameter's description.
let private catalog : (string * string list) list =
  typeof<SageFs.Server.McpTools.SageFsTools>.GetMethods(BindingFlags.Public ||| BindingFlags.Instance)
  |> Array.filter (fun m ->
    m.GetCustomAttributes(false) |> Array.exists (fun attr -> attr.GetType().Name = "McpServerToolAttribute"))
  |> Array.map (fun m ->
    let describe (provider: ICustomAttributeProvider) =
      provider.GetCustomAttributes(typeof<DescriptionAttribute>, false)
      |> Array.map (fun attr -> (attr :?> DescriptionAttribute).Description)
      |> Array.toList
    m.Name, describe m @ (m.GetParameters() |> Array.toList |> List.collect describe))
  |> Array.toList

let private toolNames : Set<string> = catalog |> List.map fst |> Set.ofList

/// The verbs tool names start with, taken from the catalog, so a new kind of tool
/// extends what counts as tool-shaped without anyone editing this file.
let private toolVerbs : Set<string> =
  toolNames |> Set.map (fun name -> name.Split('_').[0])

let private toolShaped = Regex(@"\b[a-z]+(?:_[a-z0-9]+)+\b", RegexOptions.Compiled)

/// Tool-shaped names in `text` that are not registered tools.
let private unknownToolNames (text: string) : string list =
  [ for m in toolShaped.Matches text -> m.Value ]
  |> List.filter (fun token -> toolVerbs.Contains(token.Split('_').[0]))
  |> List.filter (fun token -> not (toolNames.Contains token))
  |> List.distinct

[<Tests>]
let tests =
  testList "Tool names mentioned in tool descriptions" [
    testCase "WHY — every tool-shaped name in a tool's own text is a registered tool, because an agent follows a name it is given" <| fun _ ->
      let dangling =
        [ for (tool, texts) in catalog do
            for text in texts do
              for name in unknownToolNames text -> sprintf "%s mentions %s" tool name ]
        |> List.distinct
      dangling |> Expect.equal "no description sends an agent to a tool that does not exist" []

    testCase "WHY — the check can fail, so a passing run means something" <| fun _ ->
      unknownToolNames "SageFs runs built-in tests through run_project_tests instead."
      |> Expect.equal "a made-up tool name is reported" [ "run_project_tests" ]
      unknownToolNames "Call get_session_status first."
      |> Expect.isEmpty "a real tool name is not"
  ]
