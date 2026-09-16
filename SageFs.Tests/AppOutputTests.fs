module SageFs.Tests.AppOutputTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// #82: the AppOutputWriter is what makes a run_app'd console/game app's stdout
/// visible — each line the app writes must become exactly one APP_OUTPUT= record
/// on the real stdout, which the daemon then routes to the session output panel.

let private capture (write: TextWriter -> unit) =
  let sink = new StringWriter()
  use w = new AppOutput.AppOutputWriter(sink)
  write w
  sink.ToString()

[<Tests>]
let appOutputTests =
  testList "AppOutput" [

    testCase "each WriteLine becomes one APP_OUTPUT= record" <| fun _ ->
      capture (fun w ->
        w.WriteLine "tick 1"
        w.WriteLine "tick 2")
      |> Expect.equal "two lines => two records"
        (AppOutput.prefix + "tick 1\n" + AppOutput.prefix + "tick 2\n")

    testCase "partial writes buffer until their newline, then emit one record" <| fun _ ->
      capture (fun w ->
        w.Write "par"
        w.Write "tial"
        w.WriteLine " done")
      |> Expect.equal "a line assembled from several writes is one record"
        (AppOutput.prefix + "partial done\n")

    testCase "a trailing partial line (no newline) is NOT emitted" <| fun _ ->
      // A record without its newline would be a half-line; hold it back.
      capture (fun w -> w.Write "no newline yet")
      |> Expect.equal "partial line stays buffered" ""

    testCase "CR is dropped so CRLF/CR both yield one clean LF-delimited record" <| fun _ ->
      capture (fun w -> w.Write "a\r\nb\r\n")
      |> Expect.equal "CRLF normalized to one record per line"
        (AppOutput.prefix + "a\n" + AppOutput.prefix + "b\n")

    testCase "tryParse recognizes an APP_OUTPUT= record and extracts the payload" <| fun _ ->
      AppOutput.tryParse (AppOutput.prefix + "hello world")
      |> Expect.equal "payload extracted" (Some "hello world")

    testCase "tryParse ignores a non-APP_OUTPUT line (e.g. WARMUP_PROGRESS or the port line)" <| fun _ ->
      AppOutput.tryParse "WARMUP_PROGRESS=2/4 loading"
      |> Expect.equal "other prefixes are not app output" None
  ]
