namespace SageFs

open System.Threading.Tasks
open SageFs.McpTools

/// The scratch-pad tool's body: a view over the session's feature state, moved out of Mcp.fs (the
/// accretion hub whose line budget only goes down — RatchetBudgets "SageFs/Mcp.fs"). The
/// registered `manage_scratch_pad` tool still lives in McpTools.fs and still answers through
/// `withEcho`; only the view moved, unchanged, so the eval write-back could land in Mcp.fs
/// without pushing it over its budget.
module McpScratchPadViews =

  /// MCP Tool: Manage scratch pad (ephemeral code snippets)
  let manageScratchPad (ctx: McpContext) (action: string) (code: string option) (snippetId: int option) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        match action.ToLowerInvariant() with
        | "list" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e -> Features.ScratchPad.addSnippet e.Code p) pad
          let snippets = Features.ScratchPad.snippets pad
          match snippets with
          | [] -> return "Scratch pad is empty."
          | _ ->
            return
              snippets
              |> List.map (fun s ->
                let status =
                  match s.Result with
                  | None -> "pending"
                  | Some (Ok v) -> sprintf "ok: %s" (v |> fun x -> match x.Length > 40 with | true -> x.[..37] + "..." | false -> x)
                  | Some (Error e) -> sprintf "err: %s" (e |> fun x -> match x.Length > 40 with | true -> x.[..37] + "..." | false -> x)
                sprintf "  [%d] %s | %s"
                  s.Id
                  (s.Code |> fun c -> match c.Length > 50 with | true -> c.[..47] + "..." | false -> c)
                  status)
              |> fun lines ->
                sprintf "Scratch pad (%d snippets):\n%s" snippets.Length (String.concat "\n" lines)
        | "export" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e ->
              let p = Features.ScratchPad.addSnippet e.Code p
              let lastId = p.NextId - 1
              Features.ScratchPad.recordResult lastId (Ok e.Result) p) pad
          return Features.ScratchPad.exportFsx pad
        | "promote" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e ->
              let p = Features.ScratchPad.addSnippet e.Code p
              let lastId = p.NextId - 1
              Features.ScratchPad.recordResult lastId (Ok e.Result) p) pad
          let successful = Features.ScratchPad.promoteSuccessful pad
          match successful with
          | [] -> return "No successful snippets to promote."
          | _ ->
            return
              sprintf "Promoted %d successful snippets:\n%s"
                successful.Length (String.concat "\n;;\n" successful)
        | _ -> return "Unknown action. Use 'list', 'export', or 'promote'."
    }
