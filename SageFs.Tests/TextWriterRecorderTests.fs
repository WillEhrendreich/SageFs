module SageFs.Tests.TextWriterRecorderTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.AppState

/// The recorder sits between a session's output and the eval that reads it back. Output reaches it on whatever
/// thread produced it (the isolated host's reply loop forwards the user app's stdout, and an app writes from its
/// own request threads), while the eval thread starts and stops the recording. These tests drive exactly that
/// shape: several threads writing while another loops start/stop.

/// How many threads write at once: more than one, so two writers also race each other.
let private writerThreads = 3

/// How many start/stop pairs the eval thread makes. The unfixed recorder threw on roughly one pair in forty
/// with three writers (about 5,000 faults in 200,000 pairs); this is far past the point where it always does.
let private recordingRounds = 60_000

/// How long the eval thread stays between start and stop, in spin iterations. An eval takes time while the host's
/// output arrives; with no gap at all the writers rarely land inside the window and the race hides.
let private evalSpinIterations = 50

/// One line of host stdout, as the isolated host sends it.
let private hostLine = "some host stdout line\n"

/// What a writer thread throws, if anything, while the eval thread loops start/stop.
type private Race = { Faults: exn list; Rounds: int }

/// Run `writerThreads` writers against one eval thread making `recordingRounds` start/stop pairs.
/// Every exception from either side is kept, so a test reports what actually went wrong.
let private raceStartStopAgainstWriters () : Race =
  let recorder = new TextWriterRecorder(TextWriter.Null)
  let faults = ConcurrentBag<exn>()
  use stop = new CancellationTokenSource()
  let writers =
    [| for _ in 1 .. writerThreads ->
         Task.Factory.StartNew(
           (fun () ->
             while not stop.IsCancellationRequested do
               try
                 recorder.Write hostLine
                 recorder.Write 'x'
                 recorder.Write(hostLine.ToCharArray(), 0, hostLine.Length)
               with e -> faults.Add e),
           TaskCreationOptions.LongRunning) |]
  for _ in 1 .. recordingRounds do
    try
      recorder.StartRecording()
      Thread.SpinWait evalSpinIterations
      recorder.StopRecording() |> ignore
    with e -> faults.Add e
  stop.Cancel()
  Task.WaitAll writers
  { Faults = List.ofSeq faults; Rounds = recordingRounds }

let private describeFaults (faults: exn list) : string =
  faults
  |> List.countBy (fun e -> sprintf "%s: %s" (e.GetType().Name) e.Message)
  |> List.map (fun (what, count) -> sprintf "%d x %s" count what)
  |> String.concat "; "

[<Tests>]
let textWriterRecorderTests =
  testList "TextWriterRecorder" [

    testCase "WHY — writers on other threads never corrupt the recording an eval reads back" <| fun _ ->
      let race = raceStartStopAgainstWriters ()
      race.Faults
      |> List.length
      |> Expect.equal (sprintf "no thread should throw (%s)" (describeFaults race.Faults)) 0

    testCase "what an eval reads back is exactly what was written while it recorded" <| fun _ ->
      let recorder = new TextWriterRecorder(TextWriter.Null)
      recorder.Write "before\n"
      recorder.StartRecording()
      recorder.Write "line one\r\n"
      recorder.Write 'x'
      recorder.Write "\n"
      recorder.Write([| 'a'; 'b'; 'c'; 'd' |], 1, 2)
      recorder.StopRecording()
      |> Expect.equal "the recording keeps the bytes as written, not normalized" "line one\r\nx\nbc"

    testCase "stopping twice reads back nothing the second time" <| fun _ ->
      let recorder = new TextWriterRecorder(TextWriter.Null)
      recorder.StartRecording()
      recorder.Write "once"
      recorder.StopRecording() |> ignore
      recorder.StopRecording() |> Expect.equal "a stopped recorder holds nothing" ""

    testCase "starting a recording discards what an earlier one left" <| fun _ ->
      let recorder = new TextWriterRecorder(TextWriter.Null)
      recorder.StartRecording()
      recorder.Write "stale"
      recorder.StartRecording()
      recorder.Write "fresh"
      recorder.StopRecording() |> Expect.equal "only the second recording remains" "fresh"

    testCase "the wrapped writer still sees CRLF-normalized output, recording or not" <| fun _ ->
      let sink = new StringWriter()
      let recorder = new TextWriterRecorder(sink)
      recorder.Write "a\nb"
      recorder.StartRecording()
      recorder.Write "c\r\nd\n"
      recorder.Write '\r'
      recorder.Write '\n'
      recorder.Write 'e'
      recorder.Write '\n'
      recorder.StopRecording() |> ignore
      sink.ToString() |> Expect.equal "every newline becomes CRLF exactly once" "a\r\nbc\r\nd\r\n\r\ne\r\n"
  ]
