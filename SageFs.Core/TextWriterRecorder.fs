namespace SageFs

open System.IO
open System.Text

/// Records session output during an eval and passes it on with CRLF endings. Output arrives on whatever thread wrote
/// it (the host's reply loop, the app's request threads) while the eval thread starts and stops the recording, and
/// `StringBuilder.ToString` racing `Append` throws `ArgumentOutOfRangeException (Parameter 'chunkLength')`. So every
/// member takes `gate`, which also covers `lastCharWasCR`.
type TextWriterRecorder(writerToRecord: TextWriter) =
  inherit TextWriter()

  let gate = obj ()
  let mutable recording: StringBuilder option = None
  // What was written while no eval was recording (a background thread's printfn, an app's log line), once watching has begun.
  // Bounded to its last `EvalResultSummary.maxBetweenCallChars` characters: a chatty app cannot grow it without limit.
  let mutable betweenCalls: StringBuilder option = None
  let mutable lastCharWasCR = false
  // Both helpers run with `gate` held.
  let record (append: StringBuilder -> unit) =
    match recording with
    | Some recorder -> append recorder
    | None ->
      betweenCalls
      |> Option.iter (fun kept ->
        append kept
        match kept.Length > Features.EvalResultSummary.maxBetweenCallChars with
        | true -> kept.Remove(0, kept.Length - Features.EvalResultSummary.maxBetweenCallChars) |> ignore
        | false -> ())
  let crlf (s: string) = s.Replace("\r\n", "\n").Replace("\n", "\r\n")

  override _.Encoding = writerToRecord.Encoding

  override _.Write(value: char) =
    lock gate (fun () ->
      record (fun recorder -> recorder.Append value |> ignore)
      match value with
      | '\n' ->
        match lastCharWasCR with
        | false -> writerToRecord.Write '\r'
        | true -> ()
        writerToRecord.Write '\n'
        lastCharWasCR <- false
      | _ ->
        lastCharWasCR <- (value = '\r')
        writerToRecord.Write value)

  override _.Write(value: string) =
    lock gate (fun () ->
      record (fun recorder -> recorder.Append value |> ignore)
      writerToRecord.Write(crlf value))

  override _.Write(bufferArr: char[], index: int, count: int) =
    lock gate (fun () ->
      record (fun recorder -> recorder.Append(bufferArr, index, count) |> ignore)
      writerToRecord.Write(crlf (new string(bufferArr, index, count))))

  member _.Enable() = () // No longer needed but kept for compatibility

  /// From now on, keep what is written while no eval is recording, for the next eval's reply (`TakeBetweenCalls`). Idempotent.
  /// Not begun at construction: warm-up writes a stream of its own that no caller asked for.
  member _.WatchBetweenCalls() =
    lock gate (fun () ->
      match betweenCalls with
      | Some _ -> ()
      | None -> betweenCalls <- Some(new StringBuilder()))

  /// What was written between evals since the last take (its last `maxBetweenCallChars` characters), and forget it.
  member _.TakeBetweenCalls() : string =
    lock gate (fun () ->
      match betweenCalls with
      | None -> ""
      | Some kept ->
        let text = kept.ToString()
        kept.Clear() |> ignore
        text)

  member _.StartRecording() =
    lock gate (fun () -> recording <- Some <| new StringBuilder())

  member _.StopRecording() =
    lock gate (fun () ->
      match recording with
      | None -> ""
      | Some recorder ->
        recording <- None
        recorder.ToString())

  override _.Flush() = lock gate (fun () -> writerToRecord.Flush())
