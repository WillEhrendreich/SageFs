module SageFs.Tests.WorkerLogFileTests

open System
open System.IO
open System.Text
open Expecto
open Expecto.Flip
open SageFs


/// A fresh temp directory removed when `body` returns, so every test writes real
/// files without leaving any behind.
let private withTempDir (body: string -> 'a) : 'a =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-workerlog-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  try body dir
  finally
    try Directory.Delete(dir, true) with _ -> ()

let private openWriter (path: string) (maxBytes: int64) : SizeCappedLogWriter =
  match SizeCappedLogWriter.TryOpen(path, maxBytes) with
  | Ok writer -> writer
  | Error err -> failtestf "writer should open at %s with cap %d, got %A" path maxBytes err

let private bytesOf (path: string) : int64 = FileInfo(path).Length

let private readAll (path: string) : string = File.ReadAllText path

[<Tests>]
let workerLogPathTests =
  testList "WorkerLogFile per-session path" [

    testCase "WHY — tryPathFor — the log lives under <data dir>/workers/<sessionId>.log, so it sits beside the manifest the daemon already owns" <| fun _ ->
      WorkerLogFile.tryPathFor "/data" "ab12cd34"
      |> Expect.equal "path" (Ok (Path.Combine("/data", "workers", "ab12cd34.log")))

    testCase "WHY — tryPathFor — an empty session id is refused, because it would name a hidden file '.log' shared by every session" <| fun _ ->
      WorkerLogFile.tryPathFor "/data" ""
      |> Expect.equal "empty refused" (Error WorkerLogPathError.EmptySessionId)

    testCase "WHY — tryPathFor — an id that is not a bare file name is refused, so a hostile id cannot write outside the workers directory" <| fun _ ->
      for bad in [ ".."; "."; "a/b"; "../escape" ] do
        match WorkerLogFile.tryPathFor "/data" bad with
        | Error (WorkerLogPathError.NotABareFileName _) -> ()
        | other -> failtestf "%s should be refused as not a bare file name, got %A" bad other
  ]

[<Tests>]
let workerLogFormatTests =
  testList "WorkerLogFile line format and sinks" [

    testCase "WHY — formatLine — a line is a UTC timestamp, a level tag and the message, so a log read weeks later still orders and filters" <| fun _ ->
      let at = DateTimeOffset(2026, 9, 30, 12, 34, 56, 789, TestTimeouts.sampleUtcOffset)
      WorkerLogFile.formatLine at WorkerLogLevel.Warn "Loader returned 0 projects"
      |> Expect.equal "format" "2026-09-30T10:34:56.789Z [WRN] Loader returned 0 projects"

    testCase "WHY — formatLine — every level has its own distinct tag" <| fun _ ->
      let at = DateTimeOffset.UnixEpoch
      let tags =
        [ WorkerLogLevel.Info; WorkerLogLevel.Debug; WorkerLogLevel.Warn; WorkerLogLevel.Errored ]
        |> List.map (fun level -> WorkerLogFile.formatLine at level "m")
      tags |> List.distinct |> List.length |> Expect.equal "four distinct lines" 4

    testCase "WHY — sinksFor — each Log level reaches BOTH the file writer and the stderr echo, so the daemon's tail and the file agree" <| fun _ ->
      let written = ResizeArray<string>()
      let echoed = ResizeArray<string>()
      let at = DateTimeOffset.UnixEpoch
      let sinks = WorkerLogFile.sinksFor (fun () -> at) written.Add echoed.Add
      sinks.Info "i"
      sinks.Debug "d"
      sinks.Warn "w"
      sinks.Error "e"
      written.Count |> Expect.equal "four written" 4
      (List.ofSeq written) |> Expect.equal "echo is the same text" (List.ofSeq echoed)
      written.[2] |> Expect.stringEnds "warn carries its tag and message" "[WRN] w"

    testCase "WHY — sinksFor — a writer that throws never propagates into the caller, because a log call must not crash the worker" <| fun _ ->
      let echoed = ResizeArray<string>()
      let sinks = WorkerLogFile.sinksFor (fun () -> DateTimeOffset.UnixEpoch) (fun _ -> failwith "disk full") echoed.Add
      sinks.Error "still reported"
      echoed.Count |> Expect.equal "the echo still ran" 1
  ]

[<Tests>]
let sizeCappedWriterTests =
  testList "SizeCappedLogWriter real files" [

    testCase "WHY — TryOpen — creates the workers directory, so the first worker of a fresh data dir can log" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "workers", "deadbeef.log")
        use writer = openWriter path 4096L
        writer.Append "hello"
        File.Exists path |> Expect.isTrue "log file exists"
        readAll path |> Expect.equal "one line, newline terminated" "hello\n")

    testCase "WHY — TryOpen — a cap too small to hold a useful line is a named error, not a writer that rotates on every line" <| fun _ ->
      withTempDir (fun dir ->
        match SizeCappedLogWriter.TryOpen(Path.Combine(dir, "x.log"), 3L) with
        | Error (LogWriterError.CapTooSmall (requested, minimum)) ->
          requested |> Expect.equal "requested" 3L
          minimum |> Expect.equal "minimum" SizeCappedLogWriter.MinimumCapBytes
        | other -> failtestf "expected CapTooSmall, got %A" other)

    testCase "WHY — TryOpen — a path that cannot be created is a named error carrying the path, not an exception out of worker startup" <| fun _ ->
      withTempDir (fun dir ->
        // A regular file where a directory is needed.
        let blocker = Path.Combine(dir, "blocker")
        File.WriteAllText(blocker, "x")
        match SizeCappedLogWriter.TryOpen(Path.Combine(blocker, "workers", "x.log"), 4096L) with
        | Error (LogWriterError.CannotOpen (path, _)) -> path |> Expect.stringContains "names the path" "blocker"
        | other -> failtestf "expected CannotOpen, got %A" other)

    testCase "WHY — Append — lines accumulate in order below the cap" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "a.log")
        use writer = openWriter path 4096L
        for i in 1 .. 5 do writer.Append(sprintf "line %d" i)
        readAll path |> Expect.equal "all five" "line 1\nline 2\nline 3\nline 4\nline 5\n")

    testCase "WHY — Append — at the cap the writer starts a fresh file and keeps exactly one previous generation, so disk use is bounded at two caps" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "b.log")
        let cap = 200L
        use writer = openWriter path cap
        for i in 1 .. 200 do writer.Append(sprintf "entry %03d 0123456789012345678901234567" i)
        (bytesOf path <= cap) |> Expect.isTrue "current generation within the cap"
        File.Exists(path + ".1") |> Expect.isTrue "previous generation kept"
        (bytesOf (path + ".1") <= cap) |> Expect.isTrue "previous generation within the cap"
        Directory.GetFiles(dir) |> Array.length |> Expect.equal "no third generation" 2
        readAll path |> Expect.stringContains "newest entry is in the current file" "entry 200"
        (readAll (path + ".1")).Contains "entry 001"
        |> Expect.isFalse "the oldest entries are gone")

    testCase "WHY — Append — the previous generation holds the entries just before the current file, with none skipped" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "c.log")
        use writer = openWriter path 120L
        for i in 1 .. 60 do writer.Append(sprintf "n=%03d ......................." i)
        let numbers (file: string) =
          readAll file
          |> fun text -> text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
          |> Array.map (fun l -> int (l.Substring(2, 3)))
        let prev = numbers (path + ".1")
        let cur = numbers path
        Array.append prev cur
        |> Expect.equal "consecutive run ending at the newest" [| (60 - prev.Length - cur.Length + 1) .. 60 |])

    testCase "WHY — Append — a single entry larger than the whole cap is truncated to fit, so one huge exception cannot defeat the cap" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "d.log")
        use writer = openWriter path 256L
        writer.Append(String('x', 10_000))
        (bytesOf path <= 256L) |> Expect.isTrue "within the cap")

    testCase "WHY — Append — multi-byte text is measured in bytes, not chars, so the cap holds for non-ASCII output" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "e.log")
        use writer = openWriter path 300L
        for _ in 1 .. 100 do writer.Append(String('é', 40))
        (bytesOf path <= 300L) |> Expect.isTrue "current within the cap"
        (bytesOf (path + ".1") <= 300L) |> Expect.isTrue "previous within the cap")

    testCase "WHY — TryOpen — reopening an existing file counts what is already in it, so a restarted worker cannot push the file past the cap" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "f.log")
        File.WriteAllText(path, String('a', 180))
        use writer = openWriter path 200L
        writer.Append(String('b', 50))
        File.Exists(path + ".1") |> Expect.isTrue "the old content rolled to the previous generation"
        readAll path |> Expect.equal "fresh file holds only the new entry" (String('b', 50) + "\n"))

    testCase "WHY — Append — the file can be read while the writer still has it open (tail -f, the daemon reading a fault reason)" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "g.log")
        use writer = openWriter path 4096L
        writer.Append "visible now"
        use reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        use text = new StreamReader(reader, Encoding.UTF8)
        text.ReadToEnd() |> Expect.equal "flushed per entry" "visible now\n")

    testCase "WHY — Append — after Dispose an append is counted as dropped rather than thrown, so a late log line at shutdown cannot crash the worker" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "h.log")
        let writer = openWriter path 4096L
        writer.Append "kept"
        (writer :> IDisposable).Dispose()
        writer.Append "too late"
        writer.DroppedEntries |> Expect.equal "one dropped" 1L
        readAll path |> Expect.equal "file unchanged" "kept\n")
  ]
