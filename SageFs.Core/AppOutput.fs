module SageFs.AppOutput

open System.IO
open System.Text

/// Shared protocol for surfacing a run_app'd app's STDOUT AND STDERR (#82, and the app-output pane).
///
/// WHY BOTH STREAMS. A running console/game app writes to `Console.Out` and `Console.Error` on its own background
/// thread, but the worker's eval-scoped `Console.SetOut` capture is torn down by the time the app writes, and the
/// daemon stops reading raw worker stdout after the port handshake — so app output was invisible. An
/// `AppOutputWriter` installed as the base `Console.Out`/`Console.Error` for the run's lifetime tags each line with
/// the prefix for ITS stream and writes it to the corresponding real process stream; the daemon's kept-alive
/// readers recognize the tags and route the line with the stream attached (mirrors the proven `WARMUP_PROGRESS=`
/// line protocol).
///
/// WHAT THIS IS NOT. This is not TUI output and has nothing to do with the deprecated TUI. It is the daemon's own
/// capture of a running app's two process streams, and the app-output pane filters on which one a line came from,
/// which is why the stream travels with the text rather than being flattened to a `string` and a colour.
///
/// THE INVARIANT THAT MAKES TAGGING SAFE. Eval output goes through the recorder capture and NEVER the base
/// stdout/stderr, so a line reaching a reader with no tag is the worker's own chatter and is dropped. Neither
/// prefix may collide with a worker's protocol line (`WARMUP_PROGRESS=`, `WORKER_PORT=`).
///
/// WHY A `module` AND NOT A `namespace`. A namespace cannot contain values, and this file has `prefix` and
/// `errorPrefix` as values beside its types. The public name is `SageFs.AppOutput` either way, so every caller
/// that says `SageFs.AppOutput.tryParse` is unaffected.

/// Which of a running app's two process streams a line came from. Closed on purpose: the pane's errors-only
/// filter matches on it, so a value outside this set would have to be guessed at.
///
/// NOT `[<RequireQualifiedAccess>]`: with it, every arm below and every match on it in the reader and the writer
/// needs `Stream.Stdout`. This DU is written a few lines from where it is declared and is never serialized, so
/// the qualified form buys nothing and only makes each arm longer to read.
type Stream =
  | Stdout
  | Stderr

module Stream =
  /// Every stream, in the order a pane lists them and the reader tries their prefixes.
  let all : Stream list = [ Stdout; Stderr ]

  /// The tag a line from this stream carries, and the real process stream it is written to.
  let prefix (stream: Stream) : string =
    match stream with
    | Stdout -> "APP_OUTPUT="
    | Stderr -> "APP_ERROR="

  /// The tag, looked up by its text. Used by the parse side and by a caller that has to say which prefix it is
  /// about to write.
  let ofPrefix (text: string) : Stream option =
    all |> List.tryFind (fun s -> prefix s = text)

/// stdout line prefix, kept as a named value because the dashboard's own tests and the worker-side writer both
/// refer to it by name. Defined FROM `Stream.prefix` rather than beside it: two places deciding the same tag
/// pairing is how a tag ends up written for one stream and parsed as the other.
let prefix = Stream.prefix Stream.Stdout

/// stderr line prefix. A second tag rather than a suffix on the first: the reader classifies by prefix, so a
/// combined `APP_OUTPUT=err:...` would have to re-split the payload and guess at an escaping scheme.
let errorPrefix = Stream.prefix Stream.Stderr

/// A thread-safe, line-buffering `TextWriter`. Each complete line written to it is emitted to `sink` as exactly
/// one `<prefix for the stream> + line + '\n'` record; partial lines (no trailing newline yet) buffer until
/// their newline. `\r` is dropped so a record is one clean line regardless of CRLF/CR. Installed as
/// `Console.Out` or `Console.Error` while an app runs; it survives the eval loop's temporary `SetOut`/restore
/// because the eval captures it as `originalOut` and restores it. The app writes from its own thread while evals
/// run on the actor thread, so every mutation is under one lock.
type AppOutputWriter(sink: TextWriter, stream: Stream) =
  inherit TextWriter()

  let gate = obj ()
  let buf = StringBuilder()
  let tag = Stream.prefix stream

  // Caller holds `gate`.
  let emitLineUnlocked () =
    let line = buf.ToString()
    buf.Clear() |> ignore
    sink.Write tag
    sink.Write line
    sink.Write '\n'
    sink.Flush()

  let writeStr (s: string) =
    match s with
    | null -> ()
    | _ ->
      lock gate (fun () ->
        for c in s do
          match c with
          | '\n' -> emitLineUnlocked ()
          | '\r' -> ()
          | _ -> buf.Append c |> ignore)

  override _.Encoding = sink.Encoding
  override _.Write(value: char) = writeStr (string value)
  override _.Write(value: string) = writeStr value
  override _.Write(buffer: char[], index: int, count: int) =
    writeStr (System.String(buffer, index, count))
  override _.Flush() = sink.Flush()

/// If `line` is one of this protocol's records, return the stream it came from and its payload. Otherwise None.
///
/// A tag must be the WHOLE start of the line: an app printing the literal text `APP_ERROR=oops` is not an error
/// line, and a substring test would say it is. Only the first tag is consumed, so a payload that itself contains a
/// prefix survives whole. An empty payload is still a record — whether a blank line is worth showing is the
/// pane's decision (`AppOutputLine.ofText`), and keeping that rule in one place is the point.
let tryParse (line: string) : (Stream * string) option =
  match isNull line with
  | true -> None
  | false ->
    [ Stream.Stdout; Stream.Stderr ]
    |> List.tryPick (fun stream ->
      let tag = Stream.prefix stream
      if line.StartsWith(tag, System.StringComparison.Ordinal) then
        Some(stream, line.Substring tag.Length)
      else
        None)
