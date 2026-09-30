namespace SageFs

open System
open System.IO
open System.Text

/// Why a per-session log path could not be built.
[<RequireQualifiedAccess>]
type WorkerLogPathError =
  | EmptySessionId
  /// The id would name something other than one file directly under the
  /// workers directory (a separator, "." or "..").
  | NotABareFileName of sessionId: string

/// Why a `SizeCappedLogWriter` could not be opened.
[<RequireQualifiedAccess>]
type LogWriterError =
  | CapTooSmall of requested: int64 * minimum: int64
  | CannotOpen of path: string * reason: string

/// Severity of a worker log line.
[<RequireQualifiedAccess>]
type WorkerLogLevel =
  | Info
  | Debug
  | Warn
  // Not `Error`: a bare Error case shadows Result.Error wherever this type's
  // module is opened (a repo test enforces it).
  | Errored

/// The four sinks `SageFs.Utils.Log` routes to.
type LogSinks =
  { Info: string -> unit
    Debug: string -> unit
    Warn: string -> unit
    Error: string -> unit }

/// A writer's life: open on a stream, or closed. A closed writer refuses to
/// write and, above all, refuses to rotate (rotating would re-create the file
/// the worker just stopped using).
type internal WriterState =
  | Open of FileStream
  | Closed

/// Appends UTF-8 lines to one file and caps its size in BYTES: when the next
/// line would pass the cap, the current file becomes `<path>.1` (replacing the
/// previous generation) and a fresh file starts. Disk use is therefore bounded
/// at two caps. No packages, no background thread: one lock, one open stream,
/// one flush per entry (worker log volume is low; losing the last lines of a
/// crashing worker would defeat the point).
///
/// It never throws out of `Append`. A log call must not be able to take down
/// the worker that made it, so a failed write is counted in `DroppedEntries`
/// instead of raised.
[<Sealed>]
type SizeCappedLogWriter private (path: string, maxBytes: int64, initial: FileStream) =
  let gate = obj ()
  let mutable state = WriterState.Open initial
  let mutable written = initial.Length
  let mutable dropped = 0L
  let encoding = UTF8Encoding(false)

  /// Smallest cap accepted. Below this a single ordinary line forces a rotation
  /// on every write.
  static member MinimumCapBytes : int64 = 64L

  static member private OpenAppend(path: string) : FileStream =
    let options =
      FileStreamOptions(
        Mode = FileMode.Append,
        Access = FileAccess.Write,
        // Others may read (tail -f, the daemon) and delete/rotate the file.
        Share = (FileShare.ReadWrite ||| FileShare.Delete))
    // Worker logs carry project paths and error text: owner-only where the OS
    // has file modes (same stance as the data dir itself).
    if not (OperatingSystem.IsWindows()) then
      options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    new FileStream(path, options)

  static member TryOpen(path: string, maxBytes: int64) : Result<SizeCappedLogWriter, LogWriterError> =
    match maxBytes >= SizeCappedLogWriter.MinimumCapBytes with
    | false -> Result.Error (LogWriterError.CapTooSmall (maxBytes, SizeCappedLogWriter.MinimumCapBytes))
    | true ->
      try
        let dir = Path.GetDirectoryName path
        match String.IsNullOrEmpty dir with
        | true -> ()
        | false ->
          match OperatingSystem.IsWindows() with
          | true -> Directory.CreateDirectory dir |> ignore
          | false ->
            Directory.CreateDirectory(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            |> ignore
        Result.Ok (new SizeCappedLogWriter(path, maxBytes, SizeCappedLogWriter.OpenAppend path))
      with ex ->
        Result.Error (LogWriterError.CannotOpen (path, ex.Message))

  /// Entries that could not be written (I/O failure, or written after Dispose).
  member _.DroppedEntries : int64 = lock gate (fun () -> dropped)

  /// Append one entry plus a newline. An entry larger than the whole cap is
  /// truncated to fit rather than allowed to defeat it.
  member _.Append(text: string) : unit =
    lock gate (fun () ->
      try
        match state with
        | WriterState.Closed -> dropped <- dropped + 1L
        | WriterState.Open stream ->
          // UTF-8 is at most 3 bytes per UTF-16 char (a surrogate pair is 2
          // chars for 4 bytes), so this many chars always fit under the cap.
          let maxChars = int ((maxBytes - 1L) / 3L)
          let fitted =
            match text.Length > maxChars with
            | true -> text.Substring(0, maxChars - 3) + "..."
            | false -> text
          let bytes = encoding.GetBytes(fitted + "\n")
          let length = int64 bytes.Length
          let target =
            match written > 0L && written + length > maxBytes with
            | true ->
              stream.Dispose()
              File.Move(path, path + ".1", true)
              let fresh = SizeCappedLogWriter.OpenAppend path
              state <- WriterState.Open fresh
              written <- 0L
              fresh
            | false -> stream
          target.Write(bytes, 0, bytes.Length)
          target.Flush()
          written <- written + length
      with _ ->
        dropped <- dropped + 1L)

  interface IDisposable with
    member _.Dispose() =
      lock gate (fun () ->
        match state with
        | WriterState.Open stream ->
          state <- WriterState.Closed
          try stream.Dispose() with _ -> ()
        | WriterState.Closed -> ())

/// Why the worker's file sink could not be installed.
[<RequireQualifiedAccess>]
type WorkerLogSetupError =
  | BadPath of WorkerLogPathError
  | CannotWrite of LogWriterError

/// The worker process's diagnostic log. A worker's stderr ends at the daemon's
/// bounded tail once the port handshake is done and the host has no
/// `ILogger` sinks, so without this every `Log.*` call after that point was
/// dropped. This gives them a real file the user can open.
module WorkerLogFile =

  /// Largest one generation of a worker log may grow. Two generations are kept
  /// (current and `.1`), so a worker's logs cost at most twice this on disk.
  [<Literal>]
  let maxBytesPerGeneration = 4194304L

  [<Literal>]
  let directoryName = "workers"

  [<Literal>]
  let fileExtension = ".log"

  /// `<dataDir>/workers/<sessionId>.log`. The session id must be one bare file
  /// name, so a hostile id cannot write outside the workers directory.
  let tryPathFor (dataDir: string) (sessionId: string) : Result<string, WorkerLogPathError> =
    match String.IsNullOrEmpty sessionId with
    | true -> Result.Error WorkerLogPathError.EmptySessionId
    | false ->
      let bare =
        sessionId <> "." && sessionId <> ".."
        && Path.GetFileName sessionId = sessionId
        && sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
      match bare with
      | true -> Result.Ok (Path.Combine(dataDir, directoryName, sessionId + fileExtension))
      | false -> Result.Error (WorkerLogPathError.NotABareFileName sessionId)

  let private tag (level: WorkerLogLevel) : string =
    match level with
    | WorkerLogLevel.Info -> "INF"
    | WorkerLogLevel.Debug -> "DBG"
    | WorkerLogLevel.Warn -> "WRN"
    | WorkerLogLevel.Errored -> "ERR"

  /// `<UTC ISO-8601 with milliseconds> [<tag>] <message>`.
  let formatLine (at: DateTimeOffset) (level: WorkerLogLevel) (message: string) : string =
    sprintf "%s [%s] %s" (at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")) (tag level) message

  /// Sinks that format each entry once and hand the same text to `write` (the
  /// file) and `echo` (stderr, which the daemon's tail captures). Neither may
  /// throw into the caller: a log line must not crash the worker.
  let sinksFor (clock: unit -> DateTimeOffset) (write: string -> unit) (echo: string -> unit) : LogSinks =
    let emit (level: WorkerLogLevel) (message: string) : unit =
      let line = formatLine (clock ()) level message
      try write line with _ -> ()
      try echo line with _ -> ()
    { Info = emit WorkerLogLevel.Info
      Debug = emit WorkerLogLevel.Debug
      Warn = emit WorkerLogLevel.Warn
      Error = emit WorkerLogLevel.Errored }

  /// The sinks `Log` currently routes to.
  let currentSinks () : LogSinks =
    { Info = SageFs.Utils.Log.logInfo
      Debug = SageFs.Utils.Log.logDebug
      Warn = SageFs.Utils.Log.logWarn
      Error = SageFs.Utils.Log.logError }

  /// Point `Log` at `sinks`.
  let restore (sinks: LogSinks) : unit =
    SageFs.Utils.Log.logInfo <- sinks.Info
    SageFs.Utils.Log.logDebug <- sinks.Debug
    SageFs.Utils.Log.logWarn <- sinks.Warn
    SageFs.Utils.Log.logError <- sinks.Error

  /// Open `<dataDir>/workers/<sessionId>.log` and route every `Log.*` call to
  /// it (and still to stderr). The caller owns the returned writer and keeps it
  /// for the life of the process. On `Error` the sinks are left as they were.
  let tryInstall (dataDir: string) (sessionId: string) : Result<SizeCappedLogWriter, WorkerLogSetupError> =
    match tryPathFor dataDir sessionId with
    | Result.Error err -> Result.Error (WorkerLogSetupError.BadPath err)
    | Result.Ok path ->
      match SizeCappedLogWriter.TryOpen(path, maxBytesPerGeneration) with
      | Result.Error err -> Result.Error (WorkerLogSetupError.CannotWrite err)
      | Result.Ok writer ->
        restore (sinksFor (fun () -> DateTimeOffset.UtcNow) writer.Append (fun line -> eprintfn "%s" line))
        Result.Ok writer
