module SageFs.Tests.AppOutputProtocolTests

open Expecto
open Expecto.Flip
open SageFs

/// The `APP_OUTPUT=` line protocol, now carrying WHICH stream a line came from.
///
/// WHY THIS EXISTS. An app's stderr never reached the daemon at all. `AppOutputWriter` was installed as the base
/// `Console.Out` only, so an app that wrote to `Console.Error` produced nothing the daemon could read: the worker
/// writes the tag to process stdout, and the daemon's kept-alive reader reads `proc.StandardOutput`. That is not a
/// stream being flattened somewhere downstream, it is a stream never being captured at the source — so the fix is at
/// the protocol, not in the pane.
///
/// The shape below is one `tryParse` over both prefixes, returning the stream WITH the text. Every layer above it
/// (`OnAppOutput`, `WorkerAppOutput`, the pane's `OutputStream`) then has a real value to carry instead of a fiction.
[<Tests>]
let tests =
  testList "the app output line protocol" [

    // ---- parsing -------------------------------------------------------------------------------

    testCase "WHY — an APP_OUTPUT line is stdout, and APP_ERROR is stderr, because the pane filters on it" <| fun _ ->
      match AppOutput.tryParse "APP_OUTPUT=listening on :5000" with
      | Some (AppOutput.Stream.Stdout, text) ->
        text |> Expect.equal "the payload with no prefix" "listening on :5000"
      | other -> failtestf "expected an stdout payload, got %A" other

      match AppOutput.tryParse "APP_ERROR=System.IO.IOException: broken pipe" with
      | Some (AppOutput.Stream.Stderr, text) ->
        text |> Expect.equal "and the error payload" "System.IO.IOException: broken pipe"
      | other -> failtestf "expected a stderr payload, got %A" other

    testCase "WHY — a line with neither prefix is not app output, so eval output and worker noise stay out" <| fun _ ->
      // The invariant that makes tagging safe: eval output goes through the recorder capture and never the base
      // stdout, so a line arriving here without a tag is the worker's own chatter and must be ignored.
      AppOutput.tryParse "listening on :5000" |> Expect.isNone "an untagged line"
      AppOutput.tryParse "WARMUP_PROGRESS=loaded" |> Expect.isNone "another protocol's line"
      AppOutput.tryParse "WORKER_PORT=5123" |> Expect.isNone "and the port line"
      AppOutput.tryParse "" |> Expect.isNone "an empty line"
      AppOutput.tryParse null |> Expect.isNone "and no line at all"

    testCase "WHY — a tag with an empty payload is a line with nothing in it, not a parse failure" <| fun _ ->
      // `Console.WriteLine("")` under the tag is a real record the writer produced. Returning None for it would
      // make the reader treat a blank app line as an unrelated line, which is a different thing entirely.
      match AppOutput.tryParse "APP_OUTPUT=" with
      | Some (AppOutput.Stream.Stdout, text) -> text |> Expect.equal "empty is still parsed" ""
      | other -> failtestf "an empty payload is still a record, got %A" other

    testCase "WHY — a tag must be the WHOLE start of the line, not a substring of the text" <| fun _ ->
      // An app printing the literal string `APP_ERROR=` is not an error line, and a `Contains` test would say it is.
      AppOutput.tryParse "GET /?q=APP_ERROR=oops" |> Expect.isNone "a tag mid-line is just text"
      AppOutput.tryParse "  APP_OUTPUT=indented" |> Expect.isNone "and leading space defeats the tag"

    testCase "WHY — a payload keeps every character after the tag, including a second prefix" <| fun _ ->
      match AppOutput.tryParse "APP_OUTPUT=the value APP_ERROR= inside" with
      | Some (AppOutput.Stream.Stdout, text) ->
        text |> Expect.equal "only the first tag is consumed" "the value APP_ERROR= inside"
      | other -> failtestf "expected the rest verbatim, got %A" other

    testCase "WHY — an empty payload is not confused with a missing one, because the pane drops blank lines itself" <| fun _ ->
      // AppOutputLine.ofText owns "a blank line is not a line". The protocol's job is to say which stream a record
      // came from, and it must not also decide the record is uninteresting, or the two rules live in two places.
      match AppOutput.tryParse "APP_ERROR=" with
      | Some (AppOutput.Stream.Stderr, "") -> ()
      | other -> failtestf "an empty stderr payload is still a record, got %A" other

    // ---- the stream, as a closed set -----------------------------------------------------------------------------

    testCase "WHY — a stream is one of two things, and the parse can never produce a third" <| fun _ ->
      // `Stream` is closed on purpose: the pane's ErrorsOnly filter matches on it, and a token outside the set
      // would have to be guessed at, which is how a stdout line ends up rendered as an error.
      AppOutput.Stream.all
      |> Expect.map string
      |> Expect.equal "exactly the two process streams" [ "Stdout"; "Stderr" ]

    // ---- writing, at the source where the stream was being lost ----------------------------------------------------

    testCase "WHY — a writer tags with its own prefix, so an error line is tagged as an error from the first byte" <| fun _ ->
      let target = new System.IO.StringWriter()
      let errWriter = new AppOutput.AppOutputWriter(target, AppOutput.Stream.Stderr)
      errWriter.Write("boom")
      errWriter.Flush()
      let text = target.ToString()
      text |> Expect.stringContains "the stderr tag is what the reader sees" "APP_ERROR=boom"
      AppOutput.tryParse (text.TrimEnd('\n'))
      |> Expect.equal "and it parses back as the stderr line it is" (Some(AppOutput.Stream.Stderr, "boom"))

    testCase "WHY — an stdout writer and a stderr writer produce different tags from the same text" <| fun _ ->
      let outTarget = new System.IO.StringWriter()
      let outWriter = new AppOutput.AppOutputWriter(outTarget, AppOutput.Stream.Stdout)
      outWriter.Write("same text")
      outWriter.Flush()
      outWriter.Flush()

      let errTarget = new System.IO.StringWriter()
      let errWriter = new AppOutput.AppOutputWriter(errTarget, AppOutput.Stream.Stderr)
      errWriter.Write("same text")
      errWriter.Flush()
      errWriter.Flush()

      AppOutput.tryParse (outTarget.ToString().TrimEnd('\n'))
      |> Expect.equal "stdout is tagged as output" (Some(AppOutput.Stream.Stdout, "same text"))

      AppOutput.tryParse (errTarget.ToString().TrimEnd('\n'))
      |> Expect.equal "stderr is tagged as an error" (Some(AppOutput.Stream.Stderr, "same text"))

    testCase "WHY — a partial line buffers until its newline, so a tag never splits a line in two" <| fun _ ->
      let target = new System.IO.StringWriter()
      let writer = new AppOutput.AppOutputWriter(target, AppOutput.Stream.Stdout)
      writer.Write("half")
      writer.Flush()
      target.ToString()
      |> Expect.equal "nothing is emitted before the newline" ""
      writer.Write(" a whole line")
      writer.Flush()
      writer.Flush()
      AppOutput.tryParse (target.ToString().TrimEnd('\n'))
      |> Expect.equal "and the record is whole when it lands" (Some(AppOutput.Stream.Stdout, "half a whole line"))

    testCase "WHY — CR is dropped so a record is one clean line under CRLF too" <| fun _ ->
      let target = new System.IO.StringWriter()
      let writer = new AppOutput.AppOutputWriter(target, AppOutput.Stream.Stdout)
      writer.Write("windows\r\n")
      writer.Flush()
      writer.Flush()
      AppOutput.tryParse (target.ToString().TrimEnd('\n'))
      |> Expect.equal "no stray carriage return in the payload" (Some(AppOutput.Stream.Stdout, "windows"))

    testCase "WHY — two writers on two threads cannot interleave inside one line" <| fun _ ->
      // The app writes from its own thread while evals run on the actor thread, so the buffer is under one lock.
      // Without it a half-written line from one thread and a whole line from another share a buffer and the reader
      // sees a line neither app wrote.
      let target = new System.IO.StringWriter()
      let writer = new AppOutput.AppOutputWriter(target, AppOutput.Stream.Stdout)
      let many = [ 1 .. 40 ]
      let threads =
        many
        |> List.map (fun i ->
          System.Threading.Thread(fun () ->
            writer.Write(sprintf "thread-%d" i)
            writer.Flush() :> unit))
        |> List.iter (fun t -> t.Start())
      many |> List.iter (fun _ -> ())
      System.Threading.Thread.Sleep 200
      writer.Flush()
      writer.Flush()
      let lines = target.ToString().Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
      let bad = lines |> List.filter (fun l -> AppOutput.tryParse l |> Option.isNone)
      bad |> Expect.equal "every emitted record carries the tag" []
      lines.Length |> Expect.equal "and each thread's record landed whole" 40
      threads |> ignore
  ]