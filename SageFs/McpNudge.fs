namespace SageFs

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open SageFs.McpTools
open SageFs.Features.Tweak
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs.McpSessionRouting

/// The MCP side of `nudge_value`: which files the call's session owns, how a
/// result is told to the caller, and the production wiring of the nudge door
/// (the real disk, the shared per-file locks, the journals under the daemon's
/// data directory). The decisions are `SageFs.Features.Tweak.Nudge`; this adds
/// only what the daemon alone knows.
module McpNudge =

  let text (value: string) : JsonNode = JsonValue.Create value :> JsonNode
  let number (value: int) : JsonNode = JsonValue.Create value :> JsonNode

  let objectOf (fields: (string * JsonNode) list) : JsonNode =
    let o = JsonObject()
    for name, value in fields do
      o[name] <- value
    o :> JsonNode

  let arrayOf (items: JsonNode list) : JsonNode =
    let a = JsonArray()
    for item in items do
      a.Add item
    a :> JsonNode

  let notesJson (notes: RunNote list) : JsonNode = arrayOf (notes |> List.map (fun note -> text (RunNote.token note)))

  let receiptFields (receipt: Receipt) : (string * JsonNode) list =
    [ "file", text receipt.File
      "address", text (NudgeAddress.format receipt.Address)
      "before", text receipt.Before
      "after", text receipt.After
      "hashAfter", text receipt.HashAfter
      "fileHashBefore", text receipt.FileHashBefore
      "fileHashAfter", text receipt.FileHashAfter
      "eventId", number receipt.EventId ]

  /// A literal's value as the JSON type its kind is: a number, a boolean, or text. A real that is not a number
  /// (a literal that overflows a double) has no JSON number, so it is null here and `text` is where to read it.
  let valueJson (value: LiteralValue) : JsonNode =
    match value with
    | LiteralValue.Bool b -> JsonValue.Create b :> JsonNode
    | LiteralValue.Integer i -> JsonValue.Create i :> JsonNode
    | LiteralValue.Real r when Double.IsFinite r -> JsonValue.Create r :> JsonNode
    | LiteralValue.Real _ -> null
    | LiteralValue.Char c -> text (string c)
    | LiteralValue.Text s
    | LiteralValue.Case s -> text s

  let spanFields (span: SourceSpan) : (string * JsonNode) list =
    [ "line", number span.Line
      "column", number span.Column
      "endLine", number span.EndLine
      "endColumn", number span.EndColumn ]

  let itemJson (item: InspectedItem) : JsonNode =
    let kind =
      match item.Kind with
      | ItemKind.Knob value ->
        [ "kind", text "Knob"
          "valueKind", text (LiteralKindName.toToken (LiteralKindName.ofValue value))
          "value", valueJson value ]
      | ItemKind.Formula -> [ "kind", text "Formula" ]
    objectOf (
      [ "address", text (NudgeAddress.format item.Address); "text", text item.Text; "hash", text item.Hash ]
      @ spanFields item.Span
      @ kind
    )

  let inspectionFields (inspection: Inspection) : (string * JsonNode) list =
    let listing =
      match inspection.Listing with
      | Listing.Complete -> [ "listing", text "Complete" ]
      | Listing.Truncated(shown, total) -> [ "listing", text "Truncated"; "shown", number shown; "total", number total ]
    [ "file", text inspection.File
      "fileHash", text inspection.FileHash
      "journaled", number inspection.Journaled
      "undoSteps", number inspection.UndoSteps
      "redoSteps", number inspection.RedoSteps ]
    @ listing
    @ [ "items", arrayOf (inspection.Items |> List.map itemJson) ]

  let outcomeFields (outcome: NudgeOutcome) : (string * JsonNode) list =
    match outcome with
    | NudgeOutcome.Inspected inspection -> inspectionFields inspection
    | NudgeOutcome.Written receipt
    | NudgeOutcome.Undone receipt
    | NudgeOutcome.Redone receipt -> receiptFields receipt
    | NudgeOutcome.Unchanged(address, shown) -> [ "address", text (NudgeAddress.format address); "text", text shown ]

  /// The reply for a run: a JSON object with an `outcome` token, the facts of the
  /// outcome, and `notes`. A refusal is `outcome: "Refused"` with the case token,
  /// the rule it broke and the next action.
  let render (result: Result<Ran, NudgeRefusal>) : string =
    match result with
    | Ok ran ->
      objectOf ([ "outcome", text (NudgeOutcome.token ran.Outcome) ] @ outcomeFields ran.Outcome @ [ "notes", notesJson ran.Notes ])
      |> fun reply -> reply.ToJsonString()
    | Error refusal ->
      objectOf
        [ "outcome", text "Refused"
          "refusal", text (NudgeRefusal.token refusal)
          "rule", text (NudgeRefusal.rule refusal)
          "nextAction", text (NudgeRefusal.nextAction refusal) ]
      |> fun reply -> reply.ToJsonString()

  /// Parse `raw` against the files `owned`, run it, and render the reply. No session
  /// lookup: the caller has already said which files the session owns.
  let nudgeWith (owned: OwnedFiles) (ports: Ports) (locks: FileLocks) (raw: RawNudge) : Task<string> =
    task {
      match parse owned ports.Files.KindOf raw with
      | Error refusal -> return render (Error refusal)
      | Ok request ->
        let! result = execute ports locks Timeouts.nudgeFileLock request
        return render result
    }

  /// What a worker's `GET /hotreload` says, as the files a session owns: every project file it lists,
  /// and the ones it marks watched. `session` and `workingDirectory` are the call's own.
  let ownedFilesFromJson (session: string) (workingDirectory: string) (hotReloadJson: string) : Result<OwnedFiles, NudgeRefusal> =
    let unknown reason = Error(NudgeRefusal.ProjectFilesUnknown reason)
    let entryOf (file: JsonElement) : (string * bool) option =
      match file.ValueKind with
      | JsonValueKind.Object ->
        match file.TryGetProperty "path", file.TryGetProperty "watched" with
        | (true, path), (true, watched) when path.ValueKind = JsonValueKind.String ->
          Some(Path.GetFullPath(path.GetString(), workingDirectory), watched.ValueKind = JsonValueKind.True)
        | _ -> None
      | _ -> None
    let ownedOf (files: JsonElement) : Result<OwnedFiles, NudgeRefusal> =
      let entries = [ for file in files.EnumerateArray() -> entryOf file ]
      match entries |> List.forall Option.isSome with
      | false -> unknown "the worker's file list has an entry without a path and a watched flag"
      | true ->
        let known = entries |> List.choose id
        Ok
          { Session = session
            WorkingDirectory = workingDirectory
            Paths = known |> List.map fst |> Set.ofList
            Watched = known |> List.filter snd |> List.map fst |> Set.ofList }
    try
      use doc = JsonDocument.Parse hotReloadJson
      let root = doc.RootElement
      match root.ValueKind with
      | JsonValueKind.Object ->
        match root.TryGetProperty "files" with
        | true, files when files.ValueKind = JsonValueKind.Array -> ownedOf files
        | _ -> unknown "the worker's answer has no list of files"
      | _ -> unknown "the worker's answer is not an object"
    with :? JsonException as ex -> unknown ex.Message

  let client = new HttpClient(Timeout = Timeouts.workerHttpRead)

  /// The files the session a call resolves to owns: its projects' files, and which of
  /// them hot reload is watching. A `sessionId` wins over the directory, as in every other
  /// session tool, so two sessions in one directory can be told apart.
  let ownedFilesOf (ctx: McpContext) (sessionId: string option) (workingDirectory: string option) : Task<Result<OwnedFiles, NudgeRefusal>> =
    task {
      let! resolution = resolveSessionId ctx "mcp" sessionId workingDirectory
      match resolution with
      | Routable sessionId ->
        match WorkerProtocol.SessionId.validate sessionId with
        | Error _ -> return Error(NudgeRefusal.NoSessionToAct(sprintf "'%s' is not a session id I know." sessionId))
        | Ok sid ->
          let! info = ctx.SessionOps.GetSessionInfo sid
          match info with
          | None -> return Error(NudgeRefusal.NoSessionToAct(sprintf "Session %s is gone." sessionId))
          | Some session ->
            match WorkerProtocol.SessionLifecycleStatus.workerPort session.Status with
            | None -> return Error(NudgeRefusal.ProjectFilesUnknown "the session has no running worker to ask")
            | Some port ->
              try
                let! answer = client.GetStringAsync(sprintf "http://127.0.0.1:%d/hotreload" port)
                return ownedFilesFromJson sessionId session.WorkingDirectory answer
              with
              | :? HttpRequestException as ex -> return Error(NudgeRefusal.ProjectFilesUnknown ex.Message)
              | :? TaskCanceledException -> return Error(NudgeRefusal.ProjectFilesUnknown "the worker did not answer in time")
      | other -> return Error(NudgeRefusal.NoSessionToAct(formatSessionResolution other))
    }

  /// Where the daemon keeps nudge journals.
  let defaultTweaksDir () : string = Path.Combine(DaemonState.SageFsDir, "tweaks")

  /// One lock per file, shared by every call the daemon serves.
  let locks = FileLocks()

  /// The real disk and a real clock, with each file's journal under `tweaksDir`.
  let productionPorts (tweaksDir: string) : Ports =
    { Files = NudgeFs.steps
      Now = fun () -> DateTime.UtcNow.Ticks
      JournalPathOf = fun file -> NudgeFs.journalPathFor tweaksDir (Ownership.sessionOf file) (Ownership.pathOf file) }

  /// `nudge_value` with journals under `tweaksDir`: resolve the session, then `nudgeWith`
  /// the real disk and the daemon's shared locks.
  let nudgeValueIn (tweaksDir: string) (ctx: McpContext) (sessionId: string option) (workingDirectory: string) (raw: RawNudge) : Task<string> =
    task {
      let wd = if String.IsNullOrWhiteSpace workingDirectory then None else Some workingDirectory
      match! ownedFilesOf ctx sessionId wd with
      | Error refusal -> return render (Error refusal)
      | Ok owned -> return! nudgeWith owned (productionPorts tweaksDir) locks raw
    }

  /// `nudge_value`: `nudgeValueIn` the daemon's own tweaks directory.
  let nudgeValue (ctx: McpContext) (sessionId: string option) (workingDirectory: string) (raw: RawNudge) : Task<string> =
    nudgeValueIn (defaultTweaksDir ()) ctx sessionId workingDirectory raw
