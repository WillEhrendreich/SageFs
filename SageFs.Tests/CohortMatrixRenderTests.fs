/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "Matrix view — a
/// picture, with a text fallback"): pure unit tests for `CohortMatrixRender`
/// — no daemon, no FSI, no I/O, no `Cohort.CohortFrame<'m>` construction.
/// These pin the char-grid glyphs, the PNG's structural validity and
/// dimensions, the §6.5 "well under 4 KB for 7,000 x 10" size claim, and
/// that re-rendering an unchanged matrix produces byte-identical output —
/// the property `SnapshotRenderGuard` (Dashboard.fs) relies on to Skip a tick
/// with nothing new to send.
module SageFs.Tests.CohortMatrixRenderTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.CohortMatrixRender
open SageFs.Server.Dashboard

let private bigEndianU32 (bytes: byte[]) (offset: int) : uint32 =
  (uint32 bytes.[offset] <<< 24)
  ||| (uint32 bytes.[offset + 1] <<< 16)
  ||| (uint32 bytes.[offset + 2] <<< 8)
  ||| uint32 bytes.[offset + 3]

/// True when `needle` (ASCII chunk-type bytes, e.g. "IDAT") occurs anywhere
/// in `haystack` — a minimal chunk-presence probe with no PNG decoder.
let private containsAsciiTag (haystack: byte[]) (needle: string) : bool =
  let pattern = Text.Encoding.ASCII.GetBytes(needle: string)
  let last = haystack.Length - pattern.Length
  seq { 0 .. last } |> Seq.exists (fun i -> Array.sub haystack i pattern.Length = pattern)

[<Tests>]
let cohortMatrixRenderTests =
  testList "CohortMatrixRender" [

    testList "outcomeAt" [
      test "a cell set only in Pass is CellOutcome.Pass" {
        let pass = [| [| true |] |]
        let fail = [| [| false |] |]
        let stale = [| [| false |] |]
        outcomeAt pass fail stale 0 0 |> Expect.equal "pass wins" CellOutcome.Pass
      }

      test "a cell set only in Fail is CellOutcome.Fail" {
        let pass = [| [| false |] |]
        let fail = [| [| true |] |]
        let stale = [| [| false |] |]
        outcomeAt pass fail stale 0 0 |> Expect.equal "fail" CellOutcome.Fail
      }

      test "a cell set only in Stale is CellOutcome.Stale" {
        let pass = [| [| false |] |]
        let fail = [| [| false |] |]
        let stale = [| [| true |] |]
        outcomeAt pass fail stale 0 0 |> Expect.equal "stale" CellOutcome.Stale
      }

      test "a cell set in none of the three planes is CellOutcome.NotRun" {
        let pass = [| [| false |] |]
        let fail = [| [| false |] |]
        let stale = [| [| false |] |]
        outcomeAt pass fail stale 0 0 |> Expect.equal "not run" CellOutcome.NotRun
      }

      test "documented priority when a cell is set in more than one plane: Pass beats Fail beats Stale" {
        let allSet = [| [| true |] |]
        outcomeAt allSet allSet allSet 0 0 |> Expect.equal "pass wins over fail and stale" CellOutcome.Pass
        outcomeAt [| [| false |] |] allSet allSet 0 0 |> Expect.equal "fail wins over stale" CellOutcome.Fail
      }
    ]

    testList "toCharGrid" [
      test "a known 2x3 matrix renders the exact glyphs, row-major, one glyph per test column" {
        // row 0 (integration): pass, fail, not-run
        // row 1 (a member):    not-run, stale, pass
        let pass = [| [| true; false; false |]; [| false; false; true |] |]
        let fail = [| [| false; true; false |]; [| false; false; false |] |]
        let stale = [| [| false; false; false |]; [| false; true; false |] |]
        toCharGrid pass fail stale
        |> Expect.equal "exact glyphs plus the trailing legend" (sprintf "✓✗.\n.~✓\n%s" legend)
      }

      test "an empty matrix (zero rows) renders as just the legend" {
        toCharGrid [||] [||] [||] |> Expect.equal "no rows, no glyphs, still the legend" legend
      }

      test "every glyph in the grid is one of the four documented characters" {
        let rng = Random(42)
        let pass = Array.init 5 (fun _ -> Array.init 20 (fun _ -> rng.NextDouble() < 0.6))
        let fail = Array.init 5 (fun r -> Array.init 20 (fun c -> not pass.[r].[c] && rng.NextDouble() < 0.5))
        let stale = Array.init 5 (fun r -> Array.init 20 (fun c -> not pass.[r].[c] && not fail.[r].[c] && rng.NextDouble() < 0.5))
        let grid = toCharGrid pass fail stale
        let glyphChars = set [ '.'; '✓'; '✗'; '~' ]
        grid.Split('\n')
        |> Array.filter (fun line -> line <> legend)
        |> Array.forall (fun line -> line |> Seq.forall glyphChars.Contains)
        |> Expect.isTrue "every non-legend character is a documented outcome glyph"
      }
    ]

    testList "toPng" [
      test "starts with the 8-byte PNG signature" {
        let bytes = toPng [| [| true |] |] [| [| false |] |] [| [| false |] |]
        bytes.[0 .. 7]
        |> Expect.equal "PNG magic bytes" [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
      }

      test "the IHDR chunk's width is the test (column) count and height is the row count" {
        let pass = Array.init 4 (fun _ -> Array.create 9 false)
        let fail = Array.init 4 (fun _ -> Array.create 9 false)
        let stale = Array.init 4 (fun _ -> Array.create 9 false)
        let bytes = toPng pass fail stale
        // signature(8) + length(4) + "IHDR"(4) = byte 16 is IHDR's data start;
        // width is the first 4 bytes of IHDR data, height the next 4.
        bigEndianU32 bytes 16 |> Expect.equal "width = column (test) count" 9u
        bigEndianU32 bytes 20 |> Expect.equal "height = row count" 4u
      }

      test "carries PLTE, IDAT and IEND chunks" {
        let bytes = toPng [| [| true; false |]; [| false; true |] |] [| [| false; true |]; [| true; false |] |] [| [| false; false |]; [| false; false |] |]
        containsAsciiTag bytes "PLTE" |> Expect.isTrue "has a palette chunk (paletted PNG)"
        containsAsciiTag bytes "IDAT" |> Expect.isTrue "has image data"
        containsAsciiTag bytes "IEND" |> Expect.isTrue "has the terminator chunk"
      }

      test "an empty matrix (zero rows) still encodes a valid 1x1 PNG rather than throwing" {
        let bytes = toPng [||] [||] [||]
        bytes.[0 .. 7] |> Expect.equal "still a PNG" [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
        bigEndianU32 bytes 16 |> Expect.equal "width clamped to 1" 1u
        bigEndianU32 bytes 20 |> Expect.equal "height clamped to 1" 1u
      }

      test "WHY — §6.5's own claim: a 7,000 x 10 cohort matrix PNG is well under 4 KB" {
        // Representative of a real cohort frame: the overwhelming majority of
        // (test, session) cells pass, matching real suites where most tests
        // pass on most checkouts — highly redundant, which is exactly what
        // makes the whole-suite-for-the-whole-cohort picture cheap (§6.5).
        let rowCount = 10
        let colCount = 7_000
        let pass = Array.init rowCount (fun _ -> Array.init colCount (fun c -> c % 37 <> 0))
        let fail = Array.init rowCount (fun r -> Array.init colCount (fun c -> c % 37 = 0 && (c + r) % 3 = 0))
        let stale = Array.init rowCount (fun r -> Array.init colCount (fun c -> c % 37 = 0 && (c + r) % 3 = 1))
        let bytes = toPng pass fail stale
        (bytes.Length, 4096)
        |> Expect.isLessThan (sprintf "7,000x10 PNG is %d bytes — must be well under 4096" bytes.Length)
      }
    ]

    testList "determinism — the property SnapshotRenderGuard depends on to Skip an unchanged tick" [
      test "toPng is referentially transparent: the same matrix always encodes to byte-identical PNG output" {
        let pass = [| [| true; false; true |]; [| false; true; false |] |]
        let fail = [| [| false; true; false |]; [| true; false; false |] |]
        let stale = [| [| false; false; false |]; [| false; false; true |] |]
        toPng pass fail stale |> Expect.equal "two calls, same bytes" (toPng pass fail stale)
      }

      test "toCharGrid is referentially transparent: the same matrix always renders to the identical string" {
        let pass = [| [| true; false |] |]
        let fail = [| [| false; true |] |]
        let stale = [| [| false; false |] |]
        toCharGrid pass fail stale |> Expect.equal "two calls, same string" (toCharGrid pass fail stale)
      }

      test "WHY — an unchanged frame's PNG bytes make SnapshotRenderGuard Skip the second tick" {
        let pass = [| [| true; false |]; [| false; true |] |]
        let fail = [| [| false; true |]; [| true; false |] |]
        let stale = [| [| false; false |]; [| false; false |] |]
        let firstBytes = toPng pass fail stale
        match SnapshotRenderGuard.decide SnapshotRenderGuard.RenderMemory.initial firstBytes with
        | SnapshotRenderGuard.Decision.Skip ->
          failtest "first tick must render — RenderMemory.initial has nothing to compare against"
        | SnapshotRenderGuard.Decision.Render memoryAfterFirst ->
          // A second, independently re-encoded call with the SAME source
          // arrays (the "unchanged frame" case) is Skip: no new PNG bytes
          // would reach the wire for this tick.
          let secondBytes = toPng pass fail stale
          SnapshotRenderGuard.decide memoryAfterFirst secondBytes
          |> Expect.equal "unchanged matrix is Skip — no re-render, no new bytes" SnapshotRenderGuard.Decision.Skip
      }
    ]
  ]
