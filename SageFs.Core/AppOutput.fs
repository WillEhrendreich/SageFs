namespace SageFs

open System.IO
open System.Text

/// Shared protocol for surfacing a run_app'd app's stdout (#82). A running
/// console/game app writes to `Console.Out` on its own background thread, but
/// the worker's eval-scoped `Console.SetOut` capture is torn down by the time
/// the app writes, and the daemon stops reading raw worker stdout after the
/// port handshake — so app output was invisible. An `AppOutputWriter` installed
/// as the base `Console.Out` for the run's lifetime tags each line with
/// `prefix` and writes it to the real process stdout; the daemon's kept-alive
/// stdout reader recognizes the tag and routes the line to the session output
/// stream (mirrors the proven `WARMUP_PROGRESS=` line protocol).
module AppOutput =

  /// stdout line prefix marking one line of a running app's output. Chosen to
  /// mirror `WARMUP_PROGRESS=` so the daemon's worker-stdout reader can classify
  /// lines by prefix. Must never collide with FSI/eval output (eval output goes
  /// through the recorder capture, never the base stdout).
  [<Literal>]
  let prefix = "APP_OUTPUT="

  /// A thread-safe, line-buffering `TextWriter`. Each complete line written to
  /// it is emitted to `sink` as exactly one `prefix + line + '\n'` record;
  /// partial lines (no trailing newline yet) buffer until their newline. `\r`
  /// is dropped so a record is one clean line regardless of CRLF/CR. Installed
  /// as `Console.Out` while an app runs; it survives the eval loop's temporary
  /// `SetOut`/restore because the eval captures it as `originalOut` and restores
  /// it. The app writes from its own thread while evals run on the actor thread,
  /// so every mutation is under one lock.
  type AppOutputWriter(sink: TextWriter) =
    inherit TextWriter()

    let gate = obj ()
    let buf = StringBuilder()

    // Caller holds `gate`.
    let emitLineUnlocked () =
      let line = buf.ToString()
      buf.Clear() |> ignore
      sink.Write prefix
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

  /// If `line` is an `APP_OUTPUT=` record, return its payload; otherwise None.
  /// Used by the daemon's worker-stdout reader to route app output.
  let tryParse (line: string) : string option =
    match isNull line with
    | true -> None
    | false ->
      match line.StartsWith(prefix, System.StringComparison.Ordinal) with
      | true -> Some(line.Substring prefix.Length)
      | false -> None
