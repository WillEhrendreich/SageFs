/// ## BinaryPrimitives / Crc32 Mutation Tests
///
/// Proves the test suite catches mutations in the low-level binary
/// read/write primitives that back the .sagefs and .sagetc formats — a
/// flipped bounds check here (`>` vs `>=`, or checking stream length instead
/// of the section slice) is exactly the class of bug the roast calls out as
/// letting a section reader walk past its slice. Each case asserts EXACT
/// equality/behavior against the correct value so a mutant that returns any
/// other wrong value is killed too.
module BinaryPrimitivesMutationTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

let binaryPrimitivesMutationTests = testList "BinaryPrimitives/Crc32 mutations" [

  // ── Crc32 ────────────────────────────────────────────────────────────────

  testCase "WHY — crc32_of_known_input_matches_known_value — a wrong polynomial or bit order must not silently produce a plausible-looking CRC" <| fun () ->
    // CRC-32 (ISO 3309 / zlib) of the ASCII bytes "123456789" is the
    // textbook check value 0xCBF43926, used to validate CRC implementations.
    let data = Text.Encoding.ASCII.GetBytes "123456789"
    Crc32.computeAll data
    |> Expect.equal "CRC32(\"123456789\") must be the standard check value 0xCBF43926" 0xCBF43926u

  testCase "WHY — crc32_of_empty_is_zero" <| fun () ->
    Crc32.computeAll [||] |> Expect.equal "CRC32 of an empty buffer must be 0" 0u

  testCase "WHY — crc32_offset_and_length_scope_the_computation — compute must hash EXACTLY [offset, offset+length), not the whole array" <| fun () ->
    let data = Text.Encoding.ASCII.GetBytes "XX123456789YY"
    Crc32.compute data 2 9
    |> Expect.equal "computing over the embedded \"123456789\" slice must match the same standard check value"
      0xCBF43926u

  testCase "WHY — crc32_is_sensitive_to_every_byte — flipping a single byte must change the checksum" <| fun () ->
    let a = Crc32.computeAll (Text.Encoding.ASCII.GetBytes "123456789")
    let b = Crc32.computeAll (Text.Encoding.ASCII.GetBytes "123456780")
    (a = b) |> Expect.isFalse "changing the last digit must change the CRC"

  // ── writeLpString / readLpString round-trip ─────────────────────────────

  testCase "WHY — lpString_roundtrips_exactly" <| fun () ->
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw "hello, world"
    bw.Flush()
    ms.Position <- 0L
    use br = new BinaryReader(ms)
    BinaryPrimitives.readLpString br
    |> Expect.equal "a written lp-string must read back byte-for-byte identical" "hello, world"

  testCase "WHY — lpString_length_prefix_is_utf8_byte_count_not_char_count — multi-byte characters must be measured in UTF-8 bytes" <| fun () ->
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    // "café" is 4 chars but 5 UTF-8 bytes (é is 2 bytes).
    BinaryPrimitives.writeLpString bw "café"
    bw.Flush()
    ms.Position <- 0L
    use br = new BinaryReader(ms)
    br.ReadUInt32() |> Expect.equal "the length prefix must be the UTF-8 byte count (5), not the char count (4)" 5u

  // ── readLpStringBounded: bounds enforcement ──────────────────────────────

  testCase "WHY — readLpStringBounded_rejects_length_exceeding_budget — a length claiming more than the caller's budget must throw, never over-read" <| fun () ->
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw "0123456789"  // 10-byte string
    bw.Flush()
    ms.Position <- 0L
    use br = new BinaryReader(ms)
    Expect.throwsT<InvalidOperationException>
      "a 10-byte string against a 3-byte budget must throw, not silently truncate or over-read"
      (fun () -> BinaryPrimitives.readLpStringBounded br 3L |> ignore)

  testCase "WHY — readLpStringBounded_accepts_length_exactly_at_budget — the boundary must be inclusive (`>`, not `>=`)" <| fun () ->
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw "12345"  // 5-byte string
    bw.Flush()
    ms.Position <- 0L
    use br = new BinaryReader(ms)
    BinaryPrimitives.readLpStringBounded br 5L
    |> Expect.equal "a length exactly equal to the budget must be accepted, not rejected" "12345"

  // ── writeLpStringOption / readLpStringOption ─────────────────────────────

  testCase "WHY — lpStringOption_none_roundtrips_as_none" <| fun () ->
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw None
    bw.Flush()
    ms.Position <- 0L
    use br = new BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br
    |> Expect.equal "None must round-trip as None" None

  testCase "WHY — lpStringOption_some_roundtrips_as_same_value" <| fun () ->
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw (Some "present")
    bw.Flush()
    ms.Position <- 0L
    use br = new BinaryReader(ms)
    BinaryPrimitives.readLpStringOption br
    |> Expect.equal "Some \"present\" must round-trip unchanged, not collapse to None" (Some "present")

  testCase "WHY — lpStringOption_none_marker_is_exactly_0xFFFFFFFF — the sentinel value must not collide with a real 4-billion-byte length" <| fun () ->
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeLpStringOption bw None
    bw.Flush()
    ms.Position <- 0L
    use br = new BinaryReader(ms)
    br.ReadUInt32() |> Expect.equal "the None marker must be exactly 0xFFFFFFFF" 0xFFFFFFFFu
]
