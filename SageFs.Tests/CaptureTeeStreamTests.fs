/// The test runner keeps a plain copy of its console output for a human to
/// re-read without re-running the suite. It used to do that by replacing
/// `Console.Out` with a writer that forwarded to the ORIGINAL console writer, and
/// that hung a release-gate run: on Unix a console write takes `lock (Console.Out)`,
/// so a thread going through the replacement held one writer's lock and wanted
/// the other's while a thread on the original path held the other and wanted the
/// first. Nothing may nest one console writer inside another. The copy is now made
/// one level down, on the byte stream, so there is a single writer and nothing
/// to nest. These pin that the copy is faithful.
module SageFs.Tests.CaptureTeeStreamTests

open System
open System.IO
open System.Text
open Expecto
open Expecto.Flip
open SageFs.Tests.TestInfrastructure

[<Tests>]
let tests =
  testList "CaptureTeeStream" [
    testCase "WHY — everything written reaches the real stream unchanged, and the same text is kept for the log" <| fun _ ->
      use inner = new MemoryStream()
      let capture = StringBuilder()
      let encoding = UTF8Encoding(false)
      use tee = new CaptureTeeStream(inner, capture, encoding)
      let bytes = encoding.GetBytes "hello, world\n"
      tee.Write(bytes, 0, bytes.Length)
      inner.ToArray() |> Expect.equal "the real stream got exactly the bytes" bytes
      capture.ToString() |> Expect.equal "the copy is the text" "hello, world\n"

    testCase "WHY — a multi-byte character split across two writes is kept whole, not turned into replacement characters" <| fun _ ->
      use inner = new MemoryStream()
      let capture = StringBuilder()
      let encoding = UTF8Encoding(false)
      use tee = new CaptureTeeStream(inner, capture, encoding)
      let bytes = encoding.GetBytes "✓ ok ⚠"
      // Split inside the three-byte '✓'.
      tee.Write(bytes, 0, 2)
      tee.Write(bytes, 2, bytes.Length - 2)
      capture.ToString() |> Expect.equal "the character survives the split" "✓ ok ⚠"
      inner.ToArray() |> Expect.equal "and the real stream still got every byte" bytes

    testCase "WHY — the span overload StreamWriter uses is copied too, or most console output would be missing from the log" <| fun _ ->
      use inner = new MemoryStream()
      let capture = StringBuilder()
      let encoding = UTF8Encoding(false)
      use tee = new CaptureTeeStream(inner, capture, encoding)
      tee.Write(ReadOnlySpan<byte>(encoding.GetBytes "via span"))
      capture.ToString() |> Expect.equal "the copy has it" "via span"
      inner.ToArray() |> Expect.equal "and so does the real stream" (encoding.GetBytes "via span")

    testCase "WHY — a StreamWriter over the tee (how Console.Out is built) fills both, with no other writer in between" <| fun _ ->
      use inner = new MemoryStream()
      let capture = StringBuilder()
      let encoding = UTF8Encoding(false)
      let tee = new CaptureTeeStream(inner, capture, encoding)
      let writer = new StreamWriter(tee, encoding, AutoFlush = true)
      writer.WriteLine "one"
      writer.Write "two"
      writer.Flush()
      capture.ToString() |> Expect.equal "the copy" (sprintf "one%stwo" Environment.NewLine)
      encoding.GetString(inner.ToArray()) |> Expect.equal "the real stream" (sprintf "one%stwo" Environment.NewLine)
  ]
