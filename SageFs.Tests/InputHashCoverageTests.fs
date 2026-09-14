module SageFs.Tests.InputHashCoverageTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting

/// Slot 0 and 1 belong to A.fs, slot 2 belongs to B.fs, slot 3 belongs to
/// C.fs — enough spread across files to prove file-level (not just
/// slot-level) attribution and sorting.
let private mkSlot file line : SequencePoint =
  { File = file
    Line = line
    Column = 0
    EndLine = line
    EndColumn = 0
    BranchId = 0 }

let private threeFileMap : InstrumentationMap =
  { Slots =
      [| mkSlot "A.fs" 1
         mkSlot "A.fs" 2
         mkSlot "B.fs" 1
         mkSlot "C.fs" 1 |]
    TotalProbes = 4
    TrackerTypeName = "__SageFsCoverage"
    HitsFieldName = "Hits" }

/// Hits slot 0 (A.fs) and slot 2 (B.fs); slot 1 (A.fs, again) and slot 3
/// (C.fs) are not hit.
let private aAndBBitmap : CoverageBitmap =
  CoverageBitmap.ofBoolArray [| true; false; true; false |]

let private emptyBitmap : CoverageBitmap = CoverageBitmap.ofBoolArray [||]

let private contents =
  Map.ofList [ "A.fs", "let a = 1"; "B.fs", "let b = 2"; "C.fs", "let c = 3" ]

let private reader (map: Map<string, string>) (file: string) : string option = Map.tryFind file map

[<Tests>]
let coveredFilesTests =
  testList "InputHashCoverage.coveredFiles" [

    test "returns only the distinct covered files, sorted, excluding an uncovered file" {
      InputHashCoverage.coveredFiles threeFileMap aAndBBitmap
      |> Expect.equal "A.fs and B.fs are covered; C.fs is not; A.fs is not duplicated" [ "A.fs"; "B.fs" ]
    }

    test "an empty bitmap covers no files" {
      InputHashCoverage.coveredFiles threeFileMap emptyBitmap
      |> Expect.isEmpty "nothing hit"
    }

    test "a bitmap shorter than the map's slots does not crash and only reports files within range" {
      // A stale bitmap against a rebuilt (larger) instrumentation map: bit 0
      // set, nothing beyond it exists to check. CoverageBitmap.isSet fails
      // closed for every out-of-range index instead of throwing.
      let shortBitmap = CoverageBitmap.ofBoolArray [| true |]
      InputHashCoverage.coveredFiles threeFileMap shortBitmap
      |> Expect.equal "only slot 0 (A.fs) is even representable in a 1-bit bitmap" [ "A.fs" ]
    }

    testProperty "never returns a file absent from every hit slot" <|
      fun (hits: bool array) ->
        // Bound `hits` to threeFileMap's own slot count so both the map and
        // the test's own reference computation index the same fixed array —
        // an arbitrary-length `hits` array is exactly the mismatch
        // `coveredFiles` itself must tolerate (and does, via
        // `CoverageBitmap.isSet`'s bounds check), but this reference
        // computation is not the function under test and stays simple.
        let hits = hits |> Array.truncate threeFileMap.Slots.Length
        let bitmap = CoverageBitmap.ofBoolArray hits
        let hitFiles =
          [ 0 .. hits.Length - 1 ]
          |> List.filter (fun i -> hits.[i])
          |> List.map (fun i -> threeFileMap.Slots.[i].File)
          |> Set.ofList
        InputHashCoverage.coveredFiles threeFileMap bitmap
        |> List.forall (fun f -> Set.contains f hitFiles)
  ]

[<Tests>]
let ofCoverageTests =
  testList "InputHashCoverage.ofCoverage" [

    test "identical map, bitmap and reader produce an identical hash (determinism)" {
      let readA = reader contents
      let readB = reader contents
      InputHashCoverage.ofCoverage readA threeFileMap aAndBBitmap
      |> Expect.equal "same inputs, same hash" (InputHashCoverage.ofCoverage readB threeFileMap aAndBBitmap)
    }

    test "changing a COVERED file's content changes the hash" {
      let baseline = InputHashCoverage.ofCoverage (reader contents) threeFileMap aAndBBitmap
      let changed = contents |> Map.add "A.fs" "let a = 999"
      InputHashCoverage.ofCoverage (reader changed) threeFileMap aAndBBitmap
      |> Expect.notEqual "A.fs is covered; its content is part of the hash" baseline
    }

    test "changing a NON-covered file's content does not change the hash" {
      let baseline = InputHashCoverage.ofCoverage (reader contents) threeFileMap aAndBBitmap
      let changed = contents |> Map.add "C.fs" "let c = 999"
      InputHashCoverage.ofCoverage (reader changed) threeFileMap aAndBBitmap
      |> Expect.equal "C.fs is not covered by this bitmap; its content must not affect the hash" baseline
    }

    test "a covered file the reader cannot read (deletion) hashes differently than when it is readable" {
      let readable = InputHashCoverage.ofCoverage (reader contents) threeFileMap aAndBBitmap
      let deleted = contents |> Map.remove "A.fs"
      InputHashCoverage.ofCoverage (reader deleted) threeFileMap aAndBBitmap
      |> Expect.notEqual "the missing sentinel must bite, not silently vanish from the hash" readable
    }

    test "an empty covered set (empty bitmap) produces a stable, non-throwing hash" {
      let h1 = InputHashCoverage.ofCoverage (reader contents) threeFileMap emptyBitmap
      let h2 = InputHashCoverage.ofCoverage (reader contents) threeFileMap emptyBitmap
      h1 |> Expect.equal "stable across calls" h2
      h1.Length |> Expect.equal "still a well-formed 16 hex-char InputHash" 16
    }

    test "two different covered file sets do not collide just because contents match" {
      // A single-file map whose one file's content is identical to what
      // threeFileMap's A.fs would contain — proves the path is folded into
      // the hash, not just the content.
      let singleFileMap : InstrumentationMap =
        { Slots = [| mkSlot "Z.fs" 1 |]
          TotalProbes = 1
          TrackerTypeName = "__SageFsCoverage"
          HitsFieldName = "Hits" }
      let singleBitmap = CoverageBitmap.ofBoolArray [| true |]
      let onlyA : CoverageBitmap = CoverageBitmap.ofBoolArray [| true; false; false; false |]

      let hashA = InputHashCoverage.ofCoverage (reader (Map.ofList [ "A.fs", "let x = 1" ])) threeFileMap onlyA
      let hashZ = InputHashCoverage.ofCoverage (reader (Map.ofList [ "Z.fs", "let x = 1" ])) singleFileMap singleBitmap

      hashA |> Expect.notEqual "same content, different covered path — must not collide" hashZ
    }
  ]
