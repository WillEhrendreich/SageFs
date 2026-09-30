/// The logger the test runner gives Expecto, in place of its default.
///
/// Expecto's default (LiterateConsoleTarget) lazily replaces Console.Out and
/// Console.Error with an ANSI writer on the first line it prints. That writer takes
/// its buffer lock and the console lock in opposite orders depending on the path:
/// a stdout write holds the console's lock and then wants the buffer, while its own
/// log lines and every stderr write hold the buffer and then want the console. Two
/// threads on the two paths park forever, and with thousands of tests writing in
/// parallel it happens about one run in three under load. A stack dump of the hung
/// process showed the whole pool queued on it (`ANSIOutputWriter.T.prettyPrintInner`
/// against `ConsolePal.WriteFromConsoleStream`), and nothing in our code was at
/// fault: any test or product code that prints while Expecto logs can meet it.
///
/// Installing a plain TextWriterTarget before the run means Expecto never swaps the
/// console, so there is one writer and one lock order. The cost is colour in the
/// runner's own lines, which nobody reads in a gate log.
module SageFs.Tests.RunnerLogging

open System.IO
open Expecto.Logging

/// The level Expecto's own logger would report at for these arguments.
let levelOf (argv: string[]) : LogLevel =
  match argv |> Array.contains "--debug" with
  | true -> LogLevel.Debug
  | false -> LogLevel.Info

/// A logger for `name` that writes plain text lines to `writer`.
let loggerFor (level: LogLevel) (writer: TextWriter) (name: string[]) : Logger =
  TextWriterTarget(name, level, writer) :> Logger

/// A writer that hands each write to whatever Console.Out is at that moment. It holds
/// no lock of its own and no buffer, so a line goes through exactly one console lock
/// (the current Console.Out's). Resolving Console.Out per write, rather than capturing
/// it once, keeps the interactive log copy in Program.fs seeing the runner's own lines
/// when it swaps Console.Out after this is installed.
type ConsoleOutWriter() =
  inherit TextWriter()
  override _.Encoding = System.Console.Out.Encoding
  override _.Write(value: char) = System.Console.Out.Write value
  override _.Write(value: string) = System.Console.Out.Write value
  override _.WriteLine(value: string) = System.Console.Out.WriteLine value

/// Make `writer` the destination of every log line Expecto writes this run. Call once,
/// before Expecto starts: Expecto only installs its own logger when none is set.
let install (argv: string[]) (writer: TextWriter) : unit =
  let level = levelOf argv
  Global.initialise
    { Global.defaultConfig with
        getLogger = loggerFor level writer }
