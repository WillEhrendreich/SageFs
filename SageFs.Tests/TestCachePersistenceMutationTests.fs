/// ## TestCachePersistence Mutation Tests
///
/// Proves the test suite catches mutations in the .sagetc v1/v2 binary
/// format's Outcome enum boundary and the writer/reader round-trip — the
/// roast's own example of a format whose bounds checks must be uniform
/// (never trust a declared count/stride without checking it against the
/// section's actual payload). Deliberately stays within TestCacheTypes /
/// TestCacheWriter / TestCacheReader — NOT TestCacheMapping, which pulls in
/// SageFs.Features.LiveTesting (LiveTestingTypes.fs), a module another agent
/// is actively changing right now. Each case asserts EXACT equality against
/// the correct value so a mutant that returns any other wrong value is
/// killed too.
module TestCachePersistenceMutationTests

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.TestCacheTypes

let testCachePersistenceMutationTests = testList "TestCachePersistence mutations" [

  // ── Outcome.tryParse: the b <= 7 boundary ───────────────────────────────

  testCase "WHY — outcome_tryParse_accepts_every_value_0_through_7" <| fun () ->
    [ 0uy .. 7uy ]
    |> List.map Outcome.tryParse
    |> Expect.equal "every byte 0-7 must parse to Ok, in enum order Pass,Fail,Skip,Error,NotRun,AssertionFailed,ExceptionThrown,TimedOut"
      [ Ok Outcome.Pass; Ok Outcome.Fail; Ok Outcome.Skip; Ok Outcome.Error; Ok Outcome.NotRun
        Ok Outcome.AssertionFailed; Ok Outcome.ExceptionThrown; Ok Outcome.TimedOut ]

  testCase "WHY — outcome_tryParse_rejects_8_the_first_value_past_the_boundary — `<=` not `<`" <| fun () ->
    match Outcome.tryParse 8uy with
    | Error msg -> msg |> Expect.stringContains "the error must name the rejected byte value" "8"
    | Ok v -> failwithf "byte 8 has no Outcome case and must Error, not parse as %A" v

  testCase "WHY — outcome_tryParse_rejects_255 — an arbitrary out-of-range byte must fail closed, never crash or default silently" <| fun () ->
    match Outcome.tryParse 255uy with
    | Error _ -> ()
    | Ok v -> failwithf "byte 255 must Error, not parse as %A" v

  // ── Writer/Reader round-trip ─────────────────────────────────────────────

  let sampleData : StcData = {
    CoverageEntries =
      [ { TestId = "test.a"; BitmapWordCount = 2u; BitmapWords = [| 0x1122334455667788UL; 0xAABBCCDDEEFF0011UL |] }
        { TestId = "test.b"; BitmapWordCount = 0u; BitmapWords = [||] } ]
    ResultEntries =
      [ { TestId = "test.a"; Outcome = Outcome.Pass; DurationMs = 12u; Message = None }
        { TestId = "test.b"; Outcome = Outcome.AssertionFailed; DurationMs = 34u; Message = Some "expected 1, got 2" }
        { TestId = "test.c"; Outcome = Outcome.TimedOut; DurationMs = 5000u; Message = Some "Timed out after 5s" } ]
    FlakyEntries = []
    ImapGeneration = 7u
    CreatedAtMs = 1234567890L
  }

  testCase "WHY — write_then_read_preserves_every_field_exactly — a lossy round-trip on any field is a silent data-corruption bug" <| fun () ->
    let bytes = TestCacheWriter.write sampleData
    match TestCacheReader.read bytes with
    | Error e -> failwithf "expected a successful read, got Error %s" e
    | Ok roundTripped ->
      roundTripped
      |> Expect.equal "the round-tripped StcData must equal the original bit-for-bit (coverage words, outcomes, durations, messages, generation, timestamp)"
        sampleData

  testCase "WHY — write_then_read_preserves_v2_outcome_distinctions — AssertionFailed/ExceptionThrown/TimedOut must not collapse to the legacy v1 Fail byte" <| fun () ->
    let bytes = TestCacheWriter.write sampleData
    match TestCacheReader.read bytes with
    | Error e -> failwithf "expected a successful read, got Error %s" e
    | Ok roundTripped ->
      roundTripped.ResultEntries
      |> List.map (fun e -> e.Outcome)
      |> Expect.equal "outcomes must round-trip as their own distinct v2 byte values, not merge into Fail"
        [ Outcome.Pass; Outcome.AssertionFailed; Outcome.TimedOut ]

  testCase "WHY — read_rejects_corrupted_header_crc — a single flipped byte in the header must be caught by the CRC, never silently accepted" <| fun () ->
    let bytes = TestCacheWriter.write sampleData
    let corrupted = Array.copy bytes
    corrupted.[10] <- corrupted.[10] ^^^ 0xFFuy
    match TestCacheReader.read corrupted with
    | Error msg -> msg |> Expect.stringContains "a header CRC mismatch must be reported as such" "CRC"
    | Ok _ -> failwith "a corrupted header must be rejected, not silently accepted"

  testCase "WHY — read_rejects_corrupted_payload_crc — a flipped byte in a section payload must be caught by that section's CRC" <| fun () ->
    let bytes = TestCacheWriter.write sampleData
    let corrupted = Array.copy bytes
    // Flip a byte well past the 64-byte header + 48-byte directory, inside a payload.
    corrupted.[bytes.Length - 1] <- corrupted.[bytes.Length - 1] ^^^ 0xFFuy
    match TestCacheReader.read corrupted with
    | Error msg -> msg |> Expect.stringContains "a payload CRC mismatch must be reported as a section CRC error" "CRC"
    | Ok _ -> failwith "a corrupted payload must be rejected, not silently accepted"

  testCase "WHY — read_rejects_wrong_magic — a file that isn't STC1 must be rejected before any offset arithmetic happens" <| fun () ->
    let bytes = TestCacheWriter.write sampleData
    let corrupted = Array.copy bytes
    corrupted.[0] <- 0x00uy
    match TestCacheReader.read corrupted with
    | Error msg -> msg |> Expect.stringContains "the error must mention the invalid magic" "magic"
    | Ok _ -> failwith "a bad magic number must be rejected"

  testCase "WHY — read_rejects_file_shorter_than_header — a truncated file must fail the length check, never index out of bounds" <| fun () ->
    match TestCacheReader.read (Array.zeroCreate<byte> 10) with
    | Error msg -> msg |> Expect.stringContains "the error must mention the file being too short" "short"
    | Ok _ -> failwith "a 10-byte file must be rejected as too short for the 64-byte header"

  testCase "WHY — read_of_empty_coverage_and_results_succeeds — the empty case must not be treated as an error" <| fun () ->
    let bytes = TestCacheWriter.write StcData.empty
    match TestCacheReader.read bytes with
    | Ok data ->
      (data.CoverageEntries, data.ResultEntries) |> Expect.equal "empty StcData must round-trip to empty lists" ([], [])
    | Error e -> failwithf "empty StcData must be writable and readable, got Error %s" e

  testCase "WHY — bitmap_word_count_and_words_stay_in_sync_across_roundtrip — the declared word count must match the actual words written" <| fun () ->
    let data : StcData =
      { StcData.empty with
          CoverageEntries = [ { TestId = "t"; BitmapWordCount = 3u; BitmapWords = [| 1UL; 2UL; 3UL |] } ] }
    let bytes = TestCacheWriter.write data
    match TestCacheReader.read bytes with
    | Ok roundTripped ->
      roundTripped.CoverageEntries
      |> Expect.equal "the coverage entry's word count and exact words must round-trip together"
        [ { TestId = "t"; BitmapWordCount = 3u; BitmapWords = [| 1UL; 2UL; 3UL |] } ]
    | Error e -> failwithf "expected success, got %s" e
]
