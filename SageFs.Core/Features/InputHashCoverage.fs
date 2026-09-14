namespace SageFs.Features.LiveTesting

/// Coverage-aware wiring for `InputHash` (`TestRunKey.fs`) — the "later
/// phase" that file's own doc comment defers: computing a test's
/// `InputHash` from the real source content its `CoverageBitmap` says it
/// exercises, instead of an arbitrary caller-assembled content list.
///
/// Compile-order note: in `SageFs.Core.fsproj`, `TestRunKey.fs` is already
/// compiled AFTER `LiveTestingTypes.fs` (which owns `SequencePoint` /
/// `InstrumentationMap` / `CoverageBitmap`), so it could technically see the
/// coverage types directly. This module still lives in its own file rather
/// than inside `TestRunKey.fs`, so that file keeps the "pure, no way to see
/// bitmaps or disk" contract its own doc comment promises, and any future
/// resort of the coverage types relative to `TestRunKey.fs` cannot break
/// this module's compile — it is placed after both in the fsproj.
module InputHashCoverage =

  /// The distinct source files whose instrumented slots are hit in
  /// `bitmap`, sorted for a deterministic hash regardless of slot iteration
  /// order. A `bitmap`/`map` length mismatch (e.g. a stale bitmap against a
  /// rebuilt instrumentation map) cannot read out of range: iteration is
  /// bounded to `0 .. map.Slots.Length - 1`, and `CoverageBitmap.isSet`
  /// itself fails closed (`false`) for any index outside `bitmap.Count`.
  let coveredFiles (map: InstrumentationMap) (bitmap: CoverageBitmap) : string list =
    [ 0 .. map.Slots.Length - 1 ]
    |> List.filter (fun i -> CoverageBitmap.isSet i bitmap)
    |> List.map (fun i -> map.Slots.[i].File)
    |> List.distinct
    |> List.sort

  /// Sentinel folded into the hash in place of content for a covered file
  /// the reader could not read (deleted, permission error, ...). Distinct
  /// from any content a `Read`-style function could plausibly return, so a
  /// covered file's disappearance changes the hash rather than silently
  /// dropping out of it.
  [<Literal>]
  let private missingSentinel = " <missing>"

  /// A test's `InputHash`, computed from the real content of every source
  /// file its `bitmap` says it exercises (§5.3). For each covered file, in
  /// sorted order, feeds BOTH the file's path and its content (or
  /// `missingSentinel` when `readFile` returns `None`) into
  /// `InputHash.compute` — the path guards against two different covered
  /// file sets colliding just because their contents happen to match, and
  /// `InputHash.compute` already length-prefixes every element, so a
  /// path/content pair can never be split ambiguously across entries. An
  /// empty covered set still produces a stable hash (of the empty
  /// sequence), rather than throwing.
  ///
  /// Pure: `readFile` is injected so the caller decides how (and whether)
  /// to touch disk — this function performs no IO itself.
  let ofCoverage
    (readFile: string -> string option)
    (map: InstrumentationMap)
    (bitmap: CoverageBitmap)
    : string =
    coveredFiles map bitmap
    |> List.collect (fun file ->
      let content =
        match readFile file with
        | Some c -> c
        | None -> missingSentinel

      [ file; content ])
    |> InputHash.compute
