/// The actions clock. Every driver command appends one JSON line to OUT/timeline.ndjson with
/// the epoch-millisecond instant it started and the instant it ended, the editor, the command
/// and its arguments. A separate unit records video and does the temporal analysis; this only
/// logs the actions, so the two can be lined up by wall clock.
module LemDrive.Timeline

open System
open System.IO
open System.Text
open System.Text.Json

/// One command's span on the wall clock.
type Entry =
  { StartMs: int64
    EndMs: int64
    Editor: string
    Command: string
    Args: string list
    Outcome: string }

/// The file the driver appends to. Optional: a call with no timeline set is not logged.
[<Literal>]
let TimelineVar = "LEM_TIMELINE"

let nowMs () : int64 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

/// One entry as one JSON line, no trailing newline.
let toJson (e: Entry) : string =
  use stream = new MemoryStream()
  use w = new Utf8JsonWriter(stream)
  w.WriteStartObject()
  w.WriteNumber("startMs", e.StartMs)
  w.WriteNumber("endMs", e.EndMs)
  w.WriteString("editor", e.Editor)
  w.WriteString("command", e.Command)
  w.WriteStartArray "args"
  for a in e.Args do
    w.WriteStringValue a
  w.WriteEndArray()
  w.WriteString("outcome", e.Outcome)
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(stream.ToArray())

/// Appends one line. Several driver processes can run at once (the lemming's and the
/// harness's), so the file is opened for append with sharing and each entry goes down in a
/// single write. A failure to log never fails the command.
let append (entry: Entry) : unit =
  match Environment.GetEnvironmentVariable TimelineVar with
  | null
  | "" -> ()
  | path ->
    try
      use fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
      let bytes = Encoding.UTF8.GetBytes(toJson entry + "\n")
      fs.Write(bytes, 0, bytes.Length)
    with _ -> ()
