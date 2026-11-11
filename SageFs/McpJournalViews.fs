namespace SageFs

open System.Threading.Tasks
open SageFs.McpTools
open SageFs.McpEcho

/// Two analysis tool bodies, moved out of Mcp.fs (the accretion hub whose line budget only goes down —
/// RatchetBudgets "SageFs/Mcp.fs") so each could answer with an `EchoResult`: a refusal is a typed
/// `SageFsError` the wrapper reports as isError, where the bodies used to return a sentence with no mark.
/// The registered tools still live in McpTools.fs and answer through `withEchoResult`.
module McpJournalViews =
  open Features.MessageJournal

  /// MCP Tool: split a pipeline into stages and say which are pure, effectful or unknown. Blank code has
  /// no stages and is the one input this refuses: any other text is at least one stage.
  let decomposePipeline (code: string) : Task<EchoResult> =
    task {
      match Features.EvalLens.decomposePipeline code with
      | [] -> return EchoResult.Failed McpToolRefusals.noPipelineCode
      | stages ->
        let lines =
          stages
          |> List.map (fun stage ->
            let icon =
              match Features.EvalLens.classifyStage stage.Code with
              | Features.Pure -> "●"
              | Features.Effectful -> "⚡"
              | Features.Unknown -> "?"
            sprintf "  %d. %s %s" stage.StageIndex icon (stage.Code.Trim()))
        return EchoResult.Answered (sprintf "Pipeline decomposition (%d stages):\n%s" stages.Length (String.concat "\n" lines))
    }

  let private levelOfFilter (lvl: string) : JournalLevel =
    match lvl.ToLowerInvariant() with
    | "debug" -> JournalLevel.Debug
    | "warn" | "warning" -> JournalLevel.Warn
    | "error" -> JournalLevel.Failure
    | _ -> JournalLevel.Info

  let private levelTag (level: JournalLevel) : string =
    match level with
    | JournalLevel.Debug -> "DBG"
    | JournalLevel.Info -> "INF"
    | JournalLevel.Warn -> "WRN"
    | JournalLevel.Failure -> "ERR"

  /// The journal of what the caller's session evaluated, newest last: one entry per eval, with the eval's own
  /// time and a level read from its result (`JournalLevel.ofEvalResult`), so a failed eval is an error entry.
  let journalOfHistory (history: Features.FeatureHooks.EvalHistoryEntry list) : Journal =
    history
    |> List.rev
    |> List.map (fun e ->
      { Timestamp = e.Timestamp
        Level = JournalLevel.ofEvalResult e.Result
        Source = "eval"
        Message = e.Code })
    |> fun entries -> Journal.create (max 256 history.Length) |> Journal.recordEntries entries

  /// MCP Tool: Get message journal (synthesized from eval history)
  let getMessageJournal (ctx: McpContext) (minLevel: string option) (source: string option) : Task<EchoResult> =
    task {
      match featureStateForCaller ctx with
      | None -> return EchoResult.Failed McpToolRefusals.noFeatureState
      | Some state ->
        match state.EvalHistory with
        | [] -> return EchoResult.Answered "No eval history — journal is empty."
        | history ->
          let journal = journalOfHistory history
          let filtered =
            match minLevel with
            | Some lvl -> Journal.filterByMinLevel (levelOfFilter lvl) journal
            | None -> Journal.entries journal
          let filtered =
            match source with
            | Some src when src <> "" ->
              filtered |> List.filter (fun e -> e.Source.Contains(src, System.StringComparison.OrdinalIgnoreCase))
            | _ -> filtered
          let stats = Journal.stats journal
          return
            EchoResult.Answered (
              [ sprintf "Journal: %d entries (%d info, %d warn, %d error)"
                  stats.Total stats.InfoCount stats.WarnCount stats.ErrorCount
                ""
                yield!
                  filtered
                  |> List.map (fun e ->
                    sprintf "[%s] %s | %s: %s"
                      (e.Timestamp.ToString("HH:mm:ss"))
                      (levelTag e.Level)
                      e.Source
                      (e.Message |> fun m -> match m.Length > 80 with | true -> m.[..77] + "..." | false -> m)) ]
              |> String.concat "\n")
    }
