/// How a nudge touches the disk, and nothing else. Every file operation the door
/// makes goes through `FileSteps`, a record of primitive steps, so the real
/// protocol (temp file, then rename; journal record, then fsync) is code that a
/// deterministic simulation can run against an in-memory disk that crashes
/// between any two steps. Production fills the steps with System.IO
/// (`NudgeFs.steps`); the simulation fills them with a fake that tears writes.
///
/// Two rules shape this file:
///  * The source file is only ever replaced by a RENAME, so a crash or a failed
///    write leaves the file byte-identical to what it was, never half written.
///  * The journal is append-only and every record carries a CRC, so a crash
///    mid-append leaves a torn tail that the next load detects and removes. A
///    torn tail is never read as an event.
module SageFs.Features.Tweak.NudgeIo

open System
open System.Text
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.TweakLog

/// Which step failed. A closed set, so a refusal can say "the rename failed"
/// without a string to parse.
[<RequireQualifiedAccess>]
type FileOperation =
  | Reading
  | Decoding
  | WritingTemp
  | Renaming
  | Appending
  | Discarding

/// A file step that did not work, with the path and the system's own words.
type FileFault =
  { Operation: FileOperation
    Path: string
    Reason: string }

/// A fault in a sentence: the path, and the system's own words.
let describeFault (fault: FileFault) : string = sprintf "%s: %s" fault.Path fault.Reason

/// What is at a path. Ownership refuses a symbolic link (a rename over a link
/// would replace the link, not the file it points at), and a missing journal is
/// an empty one, not an error.
[<RequireQualifiedAccess>]
type FileKind =
  | RegularFile
  | SymbolicLink
  | Missing
  | NotAFile

/// The primitive disk steps. `Append` is durable when it returns (fsync), and
/// `WriteTemp` leaves a durable temp file; `Rename` replaces atomically.
/// `Discard` removes a path and never throws: it is cleanup after a failure.
/// `MakeDirectory` makes a directory and the ones above it, and is not an error
/// when it is already there (the journal's folder, the first time).
type FileSteps =
  { ReadBytes: string -> Result<byte[], FileFault>
    KindOf: string -> FileKind
    MakeDirectory: string -> Result<unit, FileFault>
    WriteTemp: string -> byte[] -> Result<unit, FileFault>
    Rename: string -> string -> Result<unit, FileFault>
    Append: string -> byte[] -> Result<unit, FileFault>
    Discard: string -> unit }

[<RequireQualifiedAccess>]
type FileEncoding =
  | Utf8
  | Utf8WithBom

/// A source file as the edit engine sees it: the text, plus the one thing the
/// text cannot carry, whether the bytes began with a UTF-8 byte-order mark.
/// Putting the mark back on write is what keeps "the bytes outside the edited
/// range never change" true for a file that has one.
type FileImage =
  { Text: string
    Encoding: FileEncoding }

[<RequireQualifiedAccess>]
module FileImage =
  let byteOrderMark = [| 0xEFuy; 0xBBuy; 0xBFuy |]

  let hasByteOrderMark (bytes: byte[]) : bool =
    bytes.Length >= byteOrderMark.Length && bytes.[0] = byteOrderMark.[0] && bytes.[1] = byteOrderMark.[1] && bytes.[2] = byteOrderMark.[2]

  /// Strict UTF-8: bytes that are not valid UTF-8 are refused (`Decoding`), because
  /// decoding them leniently would write replacement characters back over them.
  let decode (path: string) (bytes: byte[]) : Result<FileImage, FileFault> =
    let marked = hasByteOrderMark bytes
    let body = if marked then bytes.[byteOrderMark.Length ..] else bytes
    try
      let text = UTF8Encoding(false, true).GetString body
      Ok { Text = text; Encoding = (if marked then FileEncoding.Utf8WithBom else FileEncoding.Utf8) }
    with :? DecoderFallbackException as ex ->
      Error { Operation = FileOperation.Decoding; Path = path; Reason = ex.Message }

  /// The exact bytes of an image. `encode (decode b) = b` for every valid UTF-8 `b`.
  let encode (image: FileImage) : byte[] =
    let body = UTF8Encoding(false).GetBytes image.Text
    match image.Encoding with
    | FileEncoding.Utf8WithBom -> Array.append byteOrderMark body
    | FileEncoding.Utf8 -> body

/// Replace a file so that it is either wholly the old bytes or wholly the new.
[<RequireQualifiedAccess>]
module AtomicWrite =
  /// What a temp file's name ends in. It ends in `.tmp`, which the file watcher ignores.
  let tempSuffix = ".sagefs-nudge.tmp"

  /// The temp file sits next to the target (a rename across directories is not
  /// atomic) and ends in `.tmp`, which the file watcher ignores, so only the
  /// rename is seen as the save.
  let tempPathFor (path: string) : string = path + tempSuffix

  /// Write the bytes to the temp file, then rename it over `path`. On any
  /// failure the temp file is discarded and `path` is as it was.
  let write (steps: FileSteps) (path: string) (bytes: byte[]) : Result<unit, FileFault> =
    let temp = tempPathFor path
    match steps.WriteTemp temp bytes with
    | Error fault ->
      steps.Discard temp
      Error fault
    | Ok() ->
      match steps.Rename temp path with
      | Error fault ->
        steps.Discard temp
        Error fault
      | Ok() -> Ok()

  /// TWIN: never wired into production. Writes straight to the target, which a
  /// crash mid-write leaves torn. Kept so the simulation can show that its
  /// "the file is old or new, never partial" invariant would catch it.
  let writeInPlaceTwin (steps: FileSteps) (path: string) (bytes: byte[]) : Result<unit, FileFault> =
    steps.WriteTemp path bytes

/// Why a journal could not be used.
[<RequireQualifiedAccess>]
type JournalFault =
  | Io of FileFault
  /// The header is not a fingerprint at all, so nothing in the file is trusted.
  | Unreadable of reason: string
  /// Not a journal this build can trust for this file: another byte layout
  /// (`LogGrade.Impossible`), or another fold version or target (`LogGrade.Risky`).
  /// It is never folded and never appended to. The door has no use for a caveat:
  /// a write is only safe when the history it undoes from is the file's own.
  | Untrusted of grade: LogGrade

/// What loading the journal had to do before it could be trusted.
[<RequireQualifiedAccess>]
type TailHealing =
  | Intact
  /// The last record was cut off by a crash. The file was rewritten without it.
  | TornTailRemoved

type LoadedJournal =
  { Log: EventLog
    Healing: TailHealing }

/// The per-file journal of what the door wrote: `TweakSaved` for each nudge,
/// `RolledBack` for each undo and redo. Records are the engine's own
/// (`TweakLogFormat`), so the same history the engine folds is what survives a
/// restart.
[<RequireQualifiedAccess>]
module Journal =
  /// The fingerprint a journal for `targetPath` carries. The target is the path
  /// of the source file, so a journal can never be folded against another file.
  let fingerprintFor (targetPath: string) : Fingerprint = Fingerprint.current (contentHash targetPath)

  let notARegularFile (journalPath: string) : JournalFault =
    JournalFault.Io { Operation = FileOperation.Reading; Path = journalPath; Reason = "the journal path is not a regular file" }

  /// The log an event list makes: ids keep counting past the newest one.
  let logOf (events: LoggedEvent list) : EventLog =
    { Events = events; NextId = (events |> List.fold (fun newest e -> max newest e.Id) 0) + 1 }

  /// Read the journal at `journalPath`. A missing file is an empty log. A torn
  /// tail is removed (the file is rewritten atomically without it) and reported.
  let load (steps: FileSteps) (targetPath: string) (journalPath: string) : Result<LoadedJournal, JournalFault> =
    match steps.KindOf journalPath with
    | FileKind.Missing -> Ok { Log = EventLog.empty; Healing = TailHealing.Intact }
    | FileKind.SymbolicLink
    | FileKind.NotAFile -> Error(notARegularFile journalPath)
    | FileKind.RegularFile ->
      match steps.ReadBytes journalPath with
      | Error fault -> Error(JournalFault.Io fault)
      | Ok bytes ->
        let current = fingerprintFor targetPath
        match TweakLogFormat.decodeSegment current bytes with
        | Error corrupt -> Error(JournalFault.Unreadable corrupt.CorruptionReason)
        | Ok decoded ->
          match decoded.Grade with
          | LogGrade.Fine ->
            let log = logOf decoded.Events
            match decoded.TornTail with
            | false -> Ok { Log = log; Healing = TailHealing.Intact }
            | true ->
              match AtomicWrite.write steps journalPath (TweakLogFormat.encodeSegment current decoded.Events) with
              | Ok() -> Ok { Log = log; Healing = TailHealing.TornTailRemoved }
              | Error fault -> Error(JournalFault.Io fault)
          | grade -> Error(JournalFault.Untrusted grade)

  /// The record's bytes, appended once the journal is known to be whole.
  let appendRecord (steps: FileSteps) (journalPath: string) (event: LoggedEvent) : Result<unit, JournalFault> =
    steps.Append journalPath (TweakLogFormat.encodeStream [ event ]) |> Result.mapError JournalFault.Io

  /// Append one event, durably, creating the file with its header first when it
  /// is not there. When this returns Ok the record is on disk. An existing journal
  /// is loaded first, which checks it is this file's and heals a torn tail, so a
  /// record is never appended after bytes that cannot be read.
  let append (steps: FileSteps) (targetPath: string) (journalPath: string) (event: LoggedEvent) : Result<unit, JournalFault> =
    match steps.KindOf journalPath with
    | FileKind.Missing ->
      let folderResult =
        match IO.Path.GetDirectoryName journalPath with
        | null
        | "" -> Ok()
        | folder -> steps.MakeDirectory folder
      match folderResult with
      | Error fault -> Error(JournalFault.Io fault)
      | Ok() ->
        match AtomicWrite.write steps journalPath (TweakLogFormat.encodeSegment (fingerprintFor targetPath) []) with
        | Error fault -> Error(JournalFault.Io fault)
        | Ok() -> appendRecord steps journalPath event
    | _ ->
      match load steps targetPath journalPath with
      | Error fault -> Error fault
      | Ok _ -> appendRecord steps journalPath event
