/// The production wiring every door that writes a session's source file shares: ONE lock per file,
/// ONE journal location, ONE ports record — so `nudge_value`, the live-bindings writer and the
/// eval write-back serialize on the same file and land in the same undo history.
///
/// It lives here, before `Mcp.fs`, because the eval hook in `Mcp.fs` is what calls `persist`, and
/// `McpNudge.fs` (which compiles after `Mcp.fs`, because it needs `McpContext`) re-exports the
/// bindings it used to hold: `locks`, `defaultTweaksDir`, `productionPorts`, `ownedFilesFromJson`.
///
/// `persist` is the write-to-source half of "A REPL eval changes the running app": it runs the
/// pure decision (`EvalWriteBack.decide`), then writes through the tweak door's own crash-safe
/// path — the per-file lock, the journal record appended BEFORE the file is replaced by rename
/// (`Nudge.commit`), and the SeenHash refusal when the file moved under the eval — so `undo`
/// keeps covering these writes.
namespace SageFs

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open SageFs.Features.Tweak
open SageFs.Features.Tweak.EvalWriteBack
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.TweakLog

module EvalWriteBackWire =

  /// Where the daemon keeps nudge journals — the same journals `undo` reads, so a write-back is
  /// stepped back by the same `action=undo` as any other write to the file.
  let defaultTweaksDir () : string = Path.Combine(DaemonState.SageFsDir, "tweaks")

  /// One lock per file, shared by every call the daemon serves (see `Nudge.FileLocks`).
  let locks = FileLocks()

  /// The real disk and a real clock, with each file's journal under `tweaksDir`.
  let productionPorts (tweaksDir: string) : Ports =
    { Files = NudgeFs.steps
      Now = fun () -> DateTime.UtcNow.Ticks
      JournalPathOf = fun file -> NudgeFs.journalPathFor tweaksDir (Ownership.sessionOf file) (Ownership.pathOf file) }

  /// What a worker's `GET /hotreload` says, as the files a session owns: every project file it
  /// lists, and the ones it marks watched. `session` and `workingDirectory` are the call's own.
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

  let private client = new HttpClient(Timeout = Timeouts.workerHttpRead)

  /// A refusal in the words a caller reads: the rule it broke and what to do next, the same
  /// sentences `nudge_value` prints, so one file's writes explain themselves one way.
  let private refusalText (refusal: NudgeRefusal) : string =
    sprintf "%s %s" (NudgeRefusal.rule refusal) (NudgeRefusal.nextAction refusal)

  /// The files the session owns, asked the same way `McpNudge.ownedFilesOf` asks: the session's
  /// info for its working directory and worker port, then `GET /hotreload` on that worker. Takes
  /// the info lookup as a thunk so an eval that named no file never reaches the session at all.
  let private ownedFilesOf
    (getInfo: unit -> Task<WorkerProtocol.SessionInfo option>)
    (sessionId: string)
    : Task<Result<OwnedFiles, WriteBack>> =
    task {
      match! getInfo () with
      | None ->
        return Error(
          WriteBack.NotPersisted(sprintf "session %s is gone, so the files it owns cannot be checked — call list_sessions; nothing was written" sessionId)
        )
      | Some session ->
        match WorkerProtocol.SessionLifecycleStatus.workerPort session.Status with
        | None -> return Error(WriteBack.NotPersisted "the session has no running worker to ask which files it owns — retry once it is Ready; nothing was written")
        | Some port ->
          try
            let! answer = client.GetStringAsync(sprintf "http://127.0.0.1:%d/hotreload" port)
            match ownedFilesFromJson sessionId session.WorkingDirectory answer with
            | Ok owned -> return Ok owned
            | Error refusal -> return Error(WriteBack.NotPersisted(refusalText refusal))
          with
          | :? HttpRequestException as ex ->
            return Error(WriteBack.NotPersisted(sprintf "the worker did not answer which files the session owns (%s); nothing was written" ex.Message))
          | :? TaskCanceledException ->
            return Error(WriteBack.NotPersisted "the worker did not answer in time about the files the session owns; nothing was written"
            )
    }

  /// The write itself, WITH the file's lock held: re-read the file as the door reads it (bytes,
  /// so a BOM stays a BOM), refuse if it is no longer the bytes the decision was made against
  /// (the SeenHash refusal), settle whatever a crash left behind, journal the write, then replace
  /// the file by rename — the same order `Nudge.runWith` keeps, reusing its `settle` and `commit`
  /// verbatim, so there is never a changed file with no record that can put it back.
  let private writeLocked
    (ports: Ports)
    (path: string)
    (journal: string)
    (seenHash: string)
    (splice: Splice)
    : Result<WriteBack, NudgeRefusal> =
    sweepStaleTemp ports.Files path
    sweepStaleTemp ports.Files journal
    match ports.Files.ReadBytes path |> Result.bind (FileImage.decode path) with
    | Error fault -> Error(NudgeRefusal.FileUnreadable fault)
    | Ok image ->
      if contentHash image.Text <> seenHash then
        Ok(
          WriteBack.NotPersisted
            "the file changed between the eval and the write-back (its content hash moved) — run the eval again; nothing was written"
        )
      else
        match Journal.load ports.Files path journal |> Result.mapError NudgeRefusal.JournalFailed with
        | Error refusal -> Error refusal
        | Ok loaded ->
          match settle production ports path journal image loaded with
          | Error refusal -> Error refusal
          | Ok(log, _notes) ->
            match journalBudget log with
            | Error refusal -> Error refusal
            | Ok() ->
              let address =
                { ModulePath = splice.ModulePath @ splice.Declaration.Container
                  BindingName = splice.Declaration.Name
                  Path = [] }
              match canSave log address with
              | Error(RollbackError.BlockedByOpenConflict blocked) -> Error(NudgeRefusal.BlockedByOpenConflict blocked)
              | Error other -> Error(NudgeRefusal.HistoryRefused other)
              | Ok() ->
                match resolve image.Text address with
                | Error e -> Error(NudgeRefusal.AddressGone e)
                | Ok before ->
                  match applySplice image.Text splice with
                  | Error verdict -> Ok verdict
                  | Ok newSource ->
                    match resolve newSource address with
                    | Error e -> Error(NudgeRefusal.AddressGone e)
                    | Ok after ->
                      let planned : PlannedWrite =
                        { NewSource = newSource
                          Event = TweakLogEvent.TweakSaved(address, before.Text, after.Text, after.Hash, contentHash image.Text)
                          Receipt =
                            { File = path
                              Address = address
                              Before = before.Text
                              After = after.Text
                              HashAfter = after.Hash
                              FileHashBefore = contentHash image.Text
                              FileHashAfter = contentHash newSource
                              EventId = log.NextId } }
                      match commit production ports path journal image log planned with
                      | Ok _receipt -> Ok(WriteBack.Written(path, splice.Declaration.StartLine))
                      | Error refusal -> Error refusal

  /// Write one eval back into the file it came from, or say why it was not written. The five
  /// conditions are `EvalWriteBack.decide`'s; this adds what only the daemon knows: the file must
  /// be one the session owns (the same rule `nudge_value` enforces), and the write goes through
  /// the shared per-file lock and the crash-safe journal-before-rename path. Never throws: a
  /// write-back problem answers as `NotPersisted`, never takes the eval's own result down.
  let persist
    (getInfo: unit -> Task<WorkerProtocol.SessionInfo option>)
    (sessionId: string)
    (filePath: string option)
    (blockStartLine: int option)
    (code: string)
    : Task<WriteBack> =
    task {
      try
        match filePath with
        | None -> return WriteBack.NotPersisted noFilePathReason
        | Some file ->
          match blockStartLine with
          | None -> return WriteBack.NotPersisted noBlockStartLineReason
          | Some blockLine ->
            match! ownedFilesOf getInfo sessionId with
            | Error verdict -> return verdict
            | Ok owned ->
              match Ownership.tryOwn owned NudgeFs.steps.KindOf file with
              | Error NotOwnedWhy.NotAmongProjectFiles ->
                return WriteBack.NotPersisted(sprintf "%s is not a source file of any project this session owns, so it is not written back" file)
              | Error NotOwnedWhy.IsASymbolicLink ->
                return WriteBack.NotPersisted(
                  sprintf "%s is a symbolic link; a rename over it would replace the link, not the file it points at, so it is not written back" file
                )
              | Error NotOwnedWhy.NotARegularFile ->
                return WriteBack.NotPersisted(sprintf "%s is not a regular file (it is missing, or a directory), so it is not written back" file)
              | Ok ownedFile ->
                let path = Ownership.pathOf ownedFile
                let ports = productionPorts (defaultTweaksDir ())
                let journal = ports.JournalPathOf ownedFile
                match ports.Files.ReadBytes path |> Result.bind (FileImage.decode path) with
                | Error fault -> return WriteBack.NotPersisted(refusalText (NudgeRefusal.FileUnreadable fault))
                | Ok image ->
                  let seenHash = contentHash image.Text
                  match decide code (Some file) (Some blockLine) image.Text with
                  | Error verdict -> return verdict
                  | Ok splice ->
                    match! locks.WithLock(path, Timeouts.nudgeFileLock, fun () -> writeLocked ports path journal seenHash splice) with
                    | Error refusal -> return WriteBack.NotPersisted(refusalText refusal)
                    | Ok verdict -> return verdict
      with ex ->
        return WriteBack.NotPersisted(sprintf "write-back failed unexpectedly (%s); nothing was written" ex.Message)
    }
