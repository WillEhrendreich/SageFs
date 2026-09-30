/// Several tools told an agent "or pass session_id explicitly" and had no such
/// parameter, so the one handle that survives two sessions sharing a directory
/// could not be used on the tools that need it most. An onboarding trial routed
/// by working_directory, hit a wrong-directory refusal, and had nothing else to
/// try. These pin the promise to the catalog: what a tool's description offers,
/// its signature takes.
module SageFs.Tests.ToolSessionHandleTests

open System
open System.ComponentModel
open System.Reflection
open Expecto
open Expecto.Flip

/// The registered tools, by reflection, with their descriptions and parameter names.
let private catalog : (string * string * string list) list =
  typeof<SageFs.Server.McpTools.SageFsTools>.GetMethods(BindingFlags.Public ||| BindingFlags.Instance)
  |> Array.filter (fun m ->
    m.GetCustomAttributes(false)
    |> Array.exists (fun attr -> attr.GetType().Name = "McpServerToolAttribute"))
  |> Array.map (fun m ->
    let description =
      match m.GetCustomAttribute<DescriptionAttribute>() with
      | null -> ""
      | attr -> attr.Description
    m.Name, description, m.GetParameters() |> Array.map (fun p -> p.Name) |> Array.toList)
  |> Array.toList

let private parametersOf (tool: string) : string list =
  match catalog |> List.tryFind (fun (name, _, _) -> name = tool) with
  | Some (_, _, parameters) -> parameters
  | None -> failtestf "no registered tool named %s" tool

[<Tests>]
let tests =
  testList "Tools that route to a session" [
    testCase "WHY — a tool whose description says to pass session_id takes a session_id parameter, because an offer the signature cannot honour sends an agent in a circle" <| fun _ ->
      let promising =
        catalog
        |> List.filter (fun (_, description, parameters) ->
          description.Contains "session_id" && not (List.contains "session_id" parameters))
        |> List.map (fun (name, _, _) -> name)
      promising |> Expect.isEmpty "every tool that mentions session_id in its description has the parameter"

    testCase "WHY — the tools an agent uses to work in a session all accept the explicit handle, so two sessions in one directory are never a dead end" <| fun _ ->
      for tool in [ "send_fsharp_code"; "check_fsharp_code"; "get_session_status"; "reset_fsi_session"; "hard_reset_fsi_session" ] do
        parametersOf tool
        |> Expect.contains (sprintf "%s takes session_id" tool) "session_id"

    testCase "WHY — the handle is optional everywhere it is added, so a caller that only knows its directory is unaffected" <| fun _ ->
      for tool in [ "send_fsharp_code"; "get_session_status"; "reset_fsi_session"; "hard_reset_fsi_session" ] do
        let method' =
          typeof<SageFs.Server.McpTools.SageFsTools>.GetMethod(tool)
        let parameter = method'.GetParameters() |> Array.find (fun p -> p.Name = "session_id")
        parameter.IsOptional |> Expect.isTrue (sprintf "%s: session_id is optional" tool)
  ]
