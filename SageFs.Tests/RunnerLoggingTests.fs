/// Expecto's default logger makes the runner replace Console.Out and Console.Error
/// with its own ANSI writer, and that writer takes its buffer lock and the console
/// lock in opposite orders on the two paths (stdout: console then buffer; its own
/// log lines and stderr: buffer then console). A gate run under load hung on exactly
/// that, twice, with every thread parked in a console write. The runner now hands
/// Expecto a plain text logger before it starts, so nothing swaps the console.
/// These pin what that logger does with the messages it is given.
module SageFs.Tests.RunnerLoggingTests

open System.IO
open Expecto
open Expecto.Flip
open Expecto.Logging

let private escape = string (char 27)

/// Log one message at `level` through the runner's logger for a writer, and say what landed.
let private written (level: LogLevel) (messageLevel: LogLevel) (text: string) = async {
  use writer = new StringWriter()
  let logger = RunnerLogging.loggerFor level writer [| "runner-logging-test" |]
  do! logger.logWithAck messageLevel (Message.eventX text)
  return writer.ToString()
}

[<Tests>]
let tests =
  testList "The runner's logger" [
    testCase "WHY — --debug asks for debug lines and nothing else does, because Expecto's own reading of the flag is what we are standing in for" <| fun _ ->
      RunnerLogging.levelOf [| "--summary"; "--debug" |]
      |> Expect.equal "--debug means Debug" LogLevel.Debug
      RunnerLogging.levelOf [| "--summary" |]
      |> Expect.equal "no flag means Info, Expecto's default" LogLevel.Info
      RunnerLogging.levelOf [||]
      |> Expect.equal "no args means Info" LogLevel.Info

    testAsync "WHY — a message at or above the level reaches the writer it was given, as plain text with no colour codes" {
      let! text = written LogLevel.Info LogLevel.Info "3 tests passed"
      text |> Expect.stringContains "the message text arrives" "3 tests passed"
      text.Contains escape |> Expect.isFalse "no ANSI escape, so a redirected log reads cleanly"
    }

    testAsync "WHY — a message below the level is dropped, so the runner is no noisier than Expecto's default" {
      let! text = written LogLevel.Info LogLevel.Debug "every test starting"
      text |> Expect.equal "nothing was written" ""
    }
  ]
