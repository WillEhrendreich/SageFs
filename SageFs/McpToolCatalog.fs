module SageFs.McpToolCatalog

open System.Collections.Generic
open ModelContextProtocol.Protocol
open SageFs.Affordances

/// Give each listed tool its one sentence.
///
/// A tool's `[<Description>]` in `McpTools.fs` is its manual, and the SDK lists it as the tool's description. The list a
/// client pays for on every session carries `ToolSummaries.sentenceOf` instead, and the manual is one call away
/// (`discover_features` with `tool`). A name that is not a `ToolName` keeps the description it has, so a tool registered
/// without a sentence is never listed blank; `ToolListProfileTests` holds the registered set to the union.
///
/// It edits the tools in place, as `McpServer.applyDestructiveHints` does, and doing it twice changes nothing.
let present (tools: IList<Tool>) : unit =
  for i in 0 .. tools.Count - 1 do
    let tool = tools.[i]
    match ToolName.tryParse tool.Name with
    | Some name -> tool.Description <- ToolSummaries.sentenceOf name
    | None -> ()
